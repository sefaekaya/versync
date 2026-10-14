using LyricsSync.Probe.Lyrics;
using LyricsSync.Probe.Storage;

namespace LyricsSync.Probe.Translation;

public enum TranslationStatus
{
    Disabled,      // çevirmen yok (anahtar yok / kapalı)
    Idle,          // çevrilecek söz yok
    Translating,
    Done,
    SameLanguage,  // şarkı zaten hedef dilde
    Error,
}

/// <summary>Bir söz kümesinin çeviri durumu. Map: orijinal satır → çeviri.</summary>
public sealed record TranslationState(
    LyricsState? Source,
    TranslationStatus Status,
    IReadOnlyDictionary<string, string> Map,
    string? SourceLanguage,
    bool FromCache,
    string? Error)
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    public static TranslationState For(LyricsState? source, TranslationStatus status, string? error = null) =>
        new(source, status, Empty, null, false, error);

    public string? Lookup(string line) =>
        Status == TranslationStatus.Done && Map.TryGetValue(line.Trim(), out var t) && t.Length > 0 ? t : null;
}

/// <summary>
/// Söz servisi yeni sözler yayınladığında tüm şarkıyı tek seferde çevirir.
/// - Tekrar eden satırlar (nakarat) bir kez gönderilir.
/// - Harf içermeyen satırlar (♪, boş) gönderilmez.
/// - Sonuç diske önbelleklenir; şarkı hedef dildeyse çeviri gösterilmez.
/// </summary>
public sealed class TranslationService : IDisposable
{
    private readonly ITranslator? _translator;
    private readonly TranslationCache _cache;
    private readonly string _targetLanguage;

    private LyricsState? _activeSource;
    private string? _activeTitle;
    private string? _activeArtist;
    private CancellationTokenSource? _cts;
    private TranslationState _state;

    public TranslationService(ITranslator? translator, string? disabledReason, TranslationCache cache, string targetLanguage)
    {
        _translator = translator;
        _cache = cache;
        _targetLanguage = targetLanguage;
        DisabledReason = disabledReason;
        _state = TranslationState.For(null, translator is null ? TranslationStatus.Disabled : TranslationStatus.Idle);
    }

    public event Action<string>? Log;

    public string? DisabledReason { get; }
    public string ProviderName => _translator?.Name ?? "kapalı";
    public string TargetLanguage => _targetLanguage;
    public TranslationState Current => Volatile.Read(ref _state);

    /// <summary>Render döngüsünden her karede çağrılır; yeni söz kümesi gelince çeviriyi başlatır.</summary>
    public void Update(LyricsState lyrics, MediaSnapshot? snap)
    {
        if (_translator is null) return;
        if (ReferenceEquals(lyrics, _activeSource)) return;

        _activeSource = lyrics;
        _activeTitle = snap?.Title ?? "";
        _activeArtist = snap?.Artist ?? "";
        Start(bypassCache: false);
    }

    /// <summary>Önbelleği atlayarak yeniden çevirir.</summary>
    public void Retranslate()
    {
        if (_translator is null || _activeSource is null) return;
        Start(bypassCache: true);
    }

    private void Start(bool bypassCache)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var source = _activeSource!;
        var lines = ExtractLines(source);
        if (lines.Count == 0)
        {
            Publish(TranslationState.For(source, TranslationStatus.Idle));
            return;
        }

        var translator = _translator!;
        var title = _activeTitle ?? "";
        var artist = _activeArtist ?? "";
        var key = TranslationCache.KeyFor(translator.CacheId, _targetLanguage, lines);

        if (bypassCache)
        {
            _cache.Remove(key);
        }
        else if (_cache.TryGet(key) is { } cached)
        {
            Publish(BuildState(source, cached.SourceLanguage, cached.Map, fromCache: true));
            return;
        }

        Publish(TranslationState.For(source, TranslationStatus.Translating));
        Log?.Invoke($"Çevriliyor: {lines.Count} benzersiz satır → {ProviderName}");

        _ = Task.Run(async () =>
        {
            TranslationState result;
            try
            {
                var r = await translator.TranslateAsync(lines, title, artist, _targetLanguage, ct);
                ct.ThrowIfCancellationRequested();

                var map = new Dictionary<string, string>();
                for (int i = 0; i < lines.Count; i++)
                    map[lines[i]] = r.Lines[i].Trim();

                _cache.Save(key, new TranslationCache.Entry
                {
                    CreatedAt = DateTimeOffset.Now,
                    SourceLanguage = r.SourceLanguage,
                    Map = map,
                });

                result = BuildState(source, r.SourceLanguage, map, fromCache: false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // şarkı değişti
            }
            catch (TranslationException ex)
            {
                result = TranslationState.For(source, TranslationStatus.Error, ex.Message);
            }
            catch (HttpRequestException ex)
            {
                result = TranslationState.For(source, TranslationStatus.Error, $"Bağlantı hatası: {ex.Message}");
            }
            catch (TaskCanceledException)
            {
                result = TranslationState.For(source, TranslationStatus.Error, "Çeviri isteği zaman aşımına uğradı.");
            }
            catch (Exception ex)
            {
                result = TranslationState.For(source, TranslationStatus.Error, $"{ex.GetType().Name}: {ex.Message}");
            }

            if (ct.IsCancellationRequested) return;
            Publish(result);
            Log?.Invoke(Describe(result));
        }, ct);
    }

    private TranslationState BuildState(LyricsState source, string? sourceLanguage, Dictionary<string, string> map, bool fromCache)
    {
        bool same = sourceLanguage is not null &&
                    sourceLanguage.StartsWith(_targetLanguage, StringComparison.OrdinalIgnoreCase);

        return same
            ? new TranslationState(source, TranslationStatus.SameLanguage, map, sourceLanguage, fromCache, null)
            : new TranslationState(source, TranslationStatus.Done, map, sourceLanguage, fromCache, null);
    }

    private static List<string> ExtractLines(LyricsState s)
    {
        IEnumerable<string> raw = s.Status switch
        {
            LyricsStatus.Synced => s.Lines.Select(l => l.Text),
            LyricsStatus.PlainOnly => s.PlainLines,
            _ => Enumerable.Empty<string>(),
        };

        return raw
            .Select(l => l.Trim())
            .Where(l => l.Any(char.IsLetter))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string Describe(TranslationState s)
    {
        var lang = s.SourceLanguage is null ? "" : $" (kaynak dil: {s.SourceLanguage})";
        var cache = s.FromCache ? ", önbellekten" : "";
        return s.Status switch
        {
            TranslationStatus.Done => $"Çeviri hazır{lang}{cache}",
            TranslationStatus.SameLanguage => $"Şarkı zaten hedef dilde, çeviri gösterilmiyor{lang}",
            TranslationStatus.Error => $"Çeviri başarısız: {s.Error}",
            _ => s.Status.ToString(),
        };
    }

    private void Publish(TranslationState s) => Volatile.Write(ref _state, s);

    public void Dispose()
    {
        _cts?.Cancel();
        _translator?.Dispose();
    }
}
