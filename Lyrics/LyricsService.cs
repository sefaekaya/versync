using LyricsSync.Probe.Storage;

namespace LyricsSync.Probe.Lyrics;

public enum LyricsStatus
{
    Idle,
    Loading,
    Synced,
    PlainOnly,
    Instrumental,
    NotFound,
    Error,
}

/// <summary>Bir şarkı için söz durumu. Renderer her karede bunu okur.</summary>
public sealed record LyricsState(
    string TrackKey,
    LyricsStatus Status,
    IReadOnlyList<LyricLine> Lines,
    IReadOnlyList<string> PlainLines,
    long? SourceId,
    bool FromCache,
    string? Error)
{
    public static LyricsState Idle { get; } =
        new("", LyricsStatus.Idle, Array.Empty<LyricLine>(), Array.Empty<string>(), null, false, null);
}

/// <summary>
/// Çalan şarkıyı izler; şarkı değişip bilgiler oturunca (debounce) sözleri önbellekten
/// ya da LRCLIB'den yükler. <see cref="Update"/> render döngüsünden her karede çağrılır.
/// </summary>
public sealed class LyricsService : IDisposable
{
    /// <summary>
    /// Şarkı değişiminde SMTC bazen yeni başlığı eski süreyle birkaç yüz ms gösterir.
    /// Başlık + süre bu kadar süre sabit kalınca arama yapılır.
    /// </summary>
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);
    /// <summary>Süre hiç gelmezse bu kadar bekledikten sonra süresiz aranır.</summary>
    private static readonly TimeSpan MaxWaitForDuration = TimeSpan.FromSeconds(2);

    private readonly LrcLibClient _client;
    private readonly LyricsCache _cache;

    private string? _pendingKey;
    private int _pendingDuration;
    private DateTimeOffset _pendingSince;

    private string? _activeKey;
    private TrackQuery? _activeQuery;
    private CancellationTokenSource? _loadCts;
    private LyricsState _state = LyricsState.Idle;

    public LyricsService(LrcLibClient client, LyricsCache cache)
    {
        _client = client;
        _cache = cache;
    }

    public event Action<string>? Log;

    public LyricsState Current => Volatile.Read(ref _state);

    public void Update(MediaSnapshot? snap, DateTimeOffset now)
    {
        if (snap is null || string.IsNullOrWhiteSpace(snap.Title))
            return; // oturum yoksa son durumu koru

        if (snap.TrackKey == _activeKey)
            return;

        int duration = (int)Math.Round(snap.Duration.TotalSeconds);
        if (snap.TrackKey != _pendingKey || duration != _pendingDuration)
        {
            _pendingKey = snap.TrackKey;
            _pendingDuration = duration;
            _pendingSince = now;
            return;
        }

        var stableFor = now - _pendingSince;
        if (stableFor < Debounce) return;
        if (duration <= 0 && stableFor < MaxWaitForDuration) return;

        StartLoad(new TrackQuery(snap.Title, snap.Artist, snap.Album, snap.Duration), snap.TrackKey, bypassCache: false);
    }

    /// <summary>Sözleri önbelleği atlayarak yeniden arar (yanlış eşleşme durumunda).</summary>
    public void Reload()
    {
        if (_activeQuery is null || _activeKey is null) return;
        _cache.Remove(_activeQuery);
        StartLoad(_activeQuery, _activeKey, bypassCache: true);
    }

    private void StartLoad(TrackQuery query, string trackKey, bool bypassCache)
    {
        // Eski yüklemeyi iptal et (CTS'yi burada dispose etmiyoruz; arka plandaki görev hâlâ token'ı okuyabilir).
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _activeKey = trackKey;
        _activeQuery = query;
        Publish(new LyricsState(trackKey, LyricsStatus.Loading, Array.Empty<LyricLine>(), Array.Empty<string>(), null, false, null));

        _ = Task.Run(async () =>
        {
            LyricsState result;
            try
            {
                LrcLibTrack? track;
                bool fromCache = false;

                if (!bypassCache && _cache.TryGet(query, out var cached))
                {
                    track = cached;
                    fromCache = true;
                }
                else
                {
                    track = await _client.FindBestAsync(query, ct);
                    ct.ThrowIfCancellationRequested();
                    _cache.Save(query, track);
                }

                result = BuildState(trackKey, track, fromCache);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // yeni şarkıya geçildi
            }
            catch (Exception ex)
            {
                result = new LyricsState(trackKey, LyricsStatus.Error, Array.Empty<LyricLine>(), Array.Empty<string>(),
                    null, false, ex.Message);
            }

            if (ct.IsCancellationRequested) return;
            Publish(result);
            Log?.Invoke(Describe(result));
        }, ct);
    }

    private static LyricsState BuildState(string trackKey, LrcLibTrack? track, bool fromCache)
    {
        if (track is null)
            return new LyricsState(trackKey, LyricsStatus.NotFound, Array.Empty<LyricLine>(), Array.Empty<string>(), null, fromCache, null);

        if (track.Instrumental)
            return new LyricsState(trackKey, LyricsStatus.Instrumental, Array.Empty<LyricLine>(), Array.Empty<string>(), track.Id, fromCache, null);

        var synced = LrcParser.Parse(track.SyncedLyrics);
        if (synced.Count > 0)
            return new LyricsState(trackKey, LyricsStatus.Synced, synced, Array.Empty<string>(), track.Id, fromCache, null);

        var plain = (track.PlainLyrics ?? "")
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        return plain.Count > 0
            ? new LyricsState(trackKey, LyricsStatus.PlainOnly, Array.Empty<LyricLine>(), plain, track.Id, fromCache, null)
            : new LyricsState(trackKey, LyricsStatus.NotFound, Array.Empty<LyricLine>(), Array.Empty<string>(), track.Id, fromCache, null);
    }

    private static string Describe(LyricsState s)
    {
        var src = s.SourceId is long id ? $"LRCLIB #{id}" : "LRCLIB";
        var cache = s.FromCache ? " (önbellekten)" : "";
        return s.Status switch
        {
            LyricsStatus.Synced => $"Senkron sözler yüklendi: {s.Lines.Count} satır, {src}{cache}",
            LyricsStatus.PlainOnly => $"Yalnızca düz söz bulundu: {s.PlainLines.Count} satır, {src}{cache}",
            LyricsStatus.Instrumental => $"Enstrümantal parça, {src}{cache}",
            LyricsStatus.NotFound => $"Söz bulunamadı{cache}",
            LyricsStatus.Error => $"Söz alınamadı: {s.Error}",
            _ => s.Status.ToString(),
        };
    }

    private void Publish(LyricsState s) => Volatile.Write(ref _state, s);

    public void Dispose()
    {
        _loadCts?.Cancel();
        _loadCts?.Dispose();
    }
}
