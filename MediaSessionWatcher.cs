using Windows.Foundation;
using Windows.Media.Control;

namespace LyricsSync.Probe;

/// <summary>
/// Windows medya oturumlarını (SMTC) izler, filtreye uyan oturumu (varsayılan: Spotify) bulur
/// ve şarkı / oynatma / konum bilgisini <see cref="MediaSnapshot"/> olarak sunar.
///
/// Güncelleme kaynakları:
///  - SMTC olayları (şarkı, oynatma durumu, zaman çizelgesi değişince)
///  - 1 sn'lik yedek yoklama (bazı uygulamalar olayları her zaman tetiklemez)
/// </summary>
public sealed class MediaSessionWatcher : IMediaWatcher
{
    private readonly Func<string, bool> _appFilter;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private MediaSnapshot? _snapshot;
    private Timer? _pollTimer;
    private bool _disposed;

    private CoverArt? _cover;
    private string? _coverKey;
    private int _coverAttempts;

    public MediaSessionWatcher(Func<string, bool> appFilter) => _appFilter = appFilter;

    /// <summary>İnsan tarafından okunabilir olay günlüğü (şarkı değişti, duraklatıldı, sarıldı...).</summary>
    public event Action<string>? Log;

    public MediaSnapshot? Current
    {
        get { lock (_gate) return _snapshot; }
    }

    /// <summary>Çalan şarkının noktalı kapak çizimi (henüz yüklenmediyse veya başka şarkıya aitse null).</summary>
    public CoverArt? Cover
    {
        get { lock (_gate) return _cover; }
    }

    /// <summary>Sistemde o an görünen tüm medya oturumlarının uygulama kimlikleri (teşhis için).</summary>
    public IReadOnlyList<string> GetAllSessionIds()
    {
        var manager = _manager;
        if (manager is null) return Array.Empty<string>();
        try
        {
            return manager.GetSessions().Select(s => s.SourceAppUserModelId).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public async Task StartAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.SessionsChanged += OnSessionsChanged;

        AttachToMatchingSession();
        await RefreshAsync();

        _pollTimer = new Timer(_ => _ = RefreshAsync(), null, 1000, 1000);
    }

    // ---- Oturum bağlama -------------------------------------------------------------

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        AttachToMatchingSession();
        _ = RefreshAsync();
    }

    private void AttachToMatchingSession()
    {
        var manager = _manager;
        if (manager is null || _disposed) return;

        GlobalSystemMediaTransportControlsSession? match = null;
        try
        {
            match = manager.GetSessions().FirstOrDefault(s => _appFilter(s.SourceAppUserModelId));
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Oturum listesi okunamadı: {ex.Message}");
        }

        lock (_gate)
        {
            var oldId = _session?.SourceAppUserModelId;
            DetachHandlers(_session);
            _session = match;
            AttachHandlers(_session);

            var newId = _session?.SourceAppUserModelId;
            if (oldId != newId)
            {
                if (newId is null)
                {
                    _snapshot = null;
                    Log?.Invoke("Medya oturumu kapandı / bulunamadı.");
                }
                else
                {
                    Log?.Invoke($"Medya oturumuna bağlanıldı: {newId}");
                }
            }
        }
    }

    private void AttachHandlers(GlobalSystemMediaTransportControlsSession? s)
    {
        if (s is null) return;
        s.MediaPropertiesChanged += OnMediaPropertiesChanged;
        s.PlaybackInfoChanged += OnPlaybackInfoChanged;
        s.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
    }

    private void DetachHandlers(GlobalSystemMediaTransportControlsSession? s)
    {
        if (s is null) return;
        try
        {
            s.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            s.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            s.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        }
        catch
        {
            // Oturum zaten kapanmış olabilir; yok say.
        }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) => _ = RefreshAsync();
    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) => _ = RefreshAsync();
    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) => _ = RefreshAsync();

    // ---- Durum okuma ------------------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_disposed) return;

        GlobalSystemMediaTransportControlsSession? session;
        lock (_gate) session = _session;

        if (session is null)
        {
            lock (_gate) _snapshot = null;
            return;
        }

        // Olaylar ve yoklama aynı anda gelebilir; okumaları sıraya koy.
        await _refreshLock.WaitAsync();
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            var timeline = session.GetTimelineProperties();
            var playback = session.GetPlaybackInfo();

            var now = DateTimeOffset.Now;
            var stamp = timeline.LastUpdatedTime;
            // Geçersiz / gelecekteki zaman damgalarına karşı koruma.
            if (stamp == default || stamp > now + TimeSpan.FromSeconds(1))
                stamp = now;

            var snapshot = new MediaSnapshot(
                AppId: session.SourceAppUserModelId,
                Title: props?.Title ?? "",
                Artist: props?.Artist ?? "",
                Album: props?.AlbumTitle ?? "",
                Duration: timeline.EndTime - timeline.StartTime,
                Position: timeline.Position - timeline.StartTime,
                PositionTimestamp: stamp,
                IsPlaying: playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                PlaybackRate: playback.PlaybackRate is double r && r > 0 ? r : 1.0);

            MediaSnapshot? previous;
            lock (_gate)
            {
                previous = _snapshot;
                _snapshot = snapshot;
            }

            MediaWatcher.ReportChanges(previous, snapshot, Log);
            await EnsureCoverAsync(props, snapshot.TrackKey);
        }
        catch (Exception ex)
        {
            // Oturum kapanırken COMException vb. gelebilir; bir sonraki yoklamada toparlanır.
            Log?.Invoke($"Okuma hatası: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Şarkı değişince kapağı bir kez yükler. Spotify küçük resmi bazen şarkı bilgisinden birkaç
    /// saniye sonra verir; boş gelirse sonraki yoklamalarda (en çok 8 kez) yeniden denenir.
    /// </summary>
    private async Task EnsureCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties? props, string trackKey)
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

        try
        {
            if (props?.Thumbnail is null) return;
            using var stream = await props.Thumbnail.OpenReadAsync();
            var cover = await CoverArt.FromStreamAsync(stream, trackKey);
            lock (_gate)
            {
                if (_coverKey == trackKey) _cover = cover;
            }
        }
        catch (Exception ex)
        {
            Log?.Invoke($"Kapak okunamadı: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _pollTimer?.Dispose();

        if (_manager is not null)
        {
            try { _manager.SessionsChanged -= OnSessionsChanged; } catch { /* yok say */ }
        }

        lock (_gate)
        {
            DetachHandlers(_session);
            _session = null;
        }

        return ValueTask.CompletedTask;
    }
}
