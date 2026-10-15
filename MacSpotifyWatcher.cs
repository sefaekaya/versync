using System.Diagnostics;
using System.Globalization;

namespace LyricsSync.Probe;

/// <summary>
/// macOS'ta Spotify masaüstü uygulamasını AppleScript (osascript) ile yoklar ve şarkı /
/// oynatma / konum bilgisini <see cref="MediaSnapshot"/> olarak sunar.
///
/// macOS'ta SMTC benzeri herkese açık bir sistem API'si yok; Spotify'ın kendi AppleScript
/// sözlüğü ise konumu milisaniye hassasiyetinde verir. İlk çalıştırmada macOS, terminalin
/// Spotify'ı kontrol etmesi için otomasyon izni ister.
/// </summary>
public sealed class MacSpotifyWatcher : IMediaWatcher
{
    private const string AppId = "Spotify";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Her yoklamada osascript'in süresi birkaç on ms oynar. Konum tahmini bu kadardan az
    /// saparsa önceki zaman damgası korunur; böylece satır vurgusu titremez.
    /// </summary>
    private static readonly TimeSpan JitterTolerance = TimeSpan.FromMilliseconds(250);

    // Alanlar ASCII 31 (unit separator) ile ayrılır; şarkı adlarında geçmez.
    // Konum ms'ye çevrilip tamsayı olarak döner: ondalık ayırıcı sistem diline göre değişmesin.
    private const string Script = """
        if application "Spotify" is not running then return "not-running"
        tell application "Spotify"
            set fieldSep to character id 31
            set playState to player state as text
            if playState is "stopped" then return "stopped"
            try
                set trk to current track
                return playState & fieldSep & (name of trk) & fieldSep & (artist of trk) & fieldSep & (album of trk) & fieldSep & ((duration of trk) as text) & fieldSep & (((player position) * 1000) as integer as text) & fieldSep & (artwork url of trk)
            on error
                return "stopped"
            end try
        end tell
        """;

    private readonly object _gate = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private readonly CancellationTokenSource _cts = new();

    private MediaSnapshot? _snapshot;
    private bool _running;
    private Task? _loop;
    private string? _lastError;

    private CoverArt? _cover;
    private string? _coverKey;
    private int _coverAttempts;

    public event Action<string>? Log;

    public MediaSnapshot? Current
    {
        get { lock (_gate) return _snapshot; }
    }

    public CoverArt? Cover
    {
        get { lock (_gate) return _cover; }
    }

    public IReadOnlyList<string> GetAllSessionIds()
    {
        lock (_gate) return _running ? new[] { AppId } : Array.Empty<string>();
    }

    public async Task StartAsync()
    {
        // İlk okuma hemen yapılsın (ve otomasyon izni penceresi şimdi çıksın).
        await RefreshAsync(_cts.Token);
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
                await RefreshAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Kapanış.
        }
    }

    // ---- Durum okuma ------------------------------------------------------------------

    private async Task RefreshAsync(CancellationToken ct)
    {
        string output;
        DateTimeOffset stamp;
        try
        {
            var before = DateTimeOffset.Now;
            output = await RunOsascriptAsync(ct);
            var after = DateTimeOffset.Now;
            // Konum betiğin ortalarında okunur; süreç başlatma maliyetini ikiye böl.
            stamp = before + (after - before) / 2;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            ReportError(ex.Message);
            return;
        }

        var fields = output.Split('\u001f');
        if (fields.Length < 7)
        {
            lock (_gate)
            {
                _running = output != "not-running";
                if (_snapshot is not null)
                    Log?.Invoke(_running ? "Spotify durdu." : "Spotify kapandı.");
                _snapshot = null;
            }
            return;
        }

        _lastError = null;

        var duration = TimeSpan.FromMilliseconds(ParseLong(fields[4]));
        var position = TimeSpan.FromMilliseconds(ParseLong(fields[5]));
        var isPlaying = fields[0] == "playing";

        var snapshot = new MediaSnapshot(
            AppId: AppId,
            Title: fields[1],
            Artist: fields[2],
            Album: fields[3],
            Duration: duration,
            Position: position,
            PositionTimestamp: stamp,
            IsPlaying: isPlaying,
            PlaybackRate: 1.0);

        MediaSnapshot? previous;
        lock (_gate)
        {
            previous = _snapshot;
            _running = true;

            // Aynı şarkı çalmaya devam ediyor ve tahmin tutuyorsa eski referansı koru (titreme olmasın).
            if (previous is not null
                && previous.TrackKey == snapshot.TrackKey
                && previous.IsPlaying == snapshot.IsPlaying
                && (snapshot.Position - previous.EstimatePosition(stamp)).Duration() < JitterTolerance)
            {
                snapshot = previous;
            }

            _snapshot = snapshot;
        }

        if (!ReferenceEquals(previous, snapshot))
            MediaWatcher.ReportChanges(previous, snapshot, Log);

        await EnsureCoverAsync(fields[6], snapshot.TrackKey, ct);
    }

    private static async Task<string> RunOsascriptAsync(CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/usr/bin/osascript")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(Script);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("osascript başlatılamadı.");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);

        if (process.ExitCode != 0)
        {
            var err = (await stderr).Trim();
            // -1743: kullanıcı otomasyon iznini vermedi.
            if (err.Contains("-1743"))
                err = "Spotify'ı kontrol etme izni yok. Sistem Ayarları → Gizlilik ve Güvenlik → Otomasyon "
                      + "bölümünden terminal uygulamana Spotify izni ver.";
            throw new InvalidOperationException(err.Length > 0 ? err : $"osascript çıkış kodu {process.ExitCode}");
        }

        return (await stdout).TrimEnd('\r', '\n');
    }

    /// <summary>Aynı hata her yoklamada tekrar tekrar günlüğe düşmesin.</summary>
    private void ReportError(string message)
    {
        if (message == _lastError) return;
        _lastError = message;
        Log?.Invoke($"Okuma hatası: {message}");
    }

    private static long ParseLong(string s) =>
        long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>
    /// Şarkı değişince kapağı bir kez indirir. Hata olursa sonraki yoklamalarda (en çok 8 kez)
    /// yeniden denenir.
    /// </summary>
    private async Task EnsureCoverAsync(string artworkUrl, string trackKey, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_coverKey != trackKey)
            {
                _coverKey = trackKey;
                _coverAttempts = 0;
                _cover = null;
            }
            else if (_cover is not null || _coverAttempts >= 8)
            {
                return;
            }
            _coverAttempts++;
        }

        if (!Uri.TryCreate(artworkUrl.Trim(), UriKind.Absolute, out var uri)) return;

        try
        {
            var bytes = await _http.GetByteArrayAsync(uri, ct);
            var cover = await CoverArt.FromEncodedImageAsync(bytes, trackKey, ct);
            lock (_gate)
            {
                if (_coverKey == trackKey) _cover = cover;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Kapak okunamadı: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop; } catch { /* yok say */ }
        }
        _cts.Dispose();
        _http.Dispose();
    }
}
