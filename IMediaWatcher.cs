namespace LyricsSync.Probe;

/// <summary>
/// Çalan şarkıyı ve konumunu izleyen platforma özgü kaynak.
///  - Windows: <see cref="MediaSessionWatcher"/> (SMTC)
///  - macOS:   <see cref="MacSpotifyWatcher"/> (Spotify AppleScript)
/// </summary>
public interface IMediaWatcher : IAsyncDisposable
{
    /// <summary>İnsan tarafından okunabilir olay günlüğü (şarkı değişti, duraklatıldı, sarıldı...).</summary>
    event Action<string>? Log;

    MediaSnapshot? Current { get; }

    /// <summary>Çalan şarkının noktalı kapak çizimi (henüz yüklenmediyse veya başka şarkıya aitse null).</summary>
    CoverArt? Cover { get; }

    /// <summary>Sistemde o an görünen medya kaynaklarının kimlikleri (teşhis için).</summary>
    IReadOnlyList<string> GetAllSessionIds();

    Task StartAsync();
}

public static class MediaWatcher
{
    /// <summary>Konum beklenenden bu kadar saparsa "sarma" (seek) olarak kabul edilir.</summary>
    internal static readonly TimeSpan SeekThreshold = TimeSpan.FromSeconds(2);

    /// <summary>Çalışılan işletim sistemine uygun izleyiciyi oluşturur.</summary>
    public static IMediaWatcher Create(Func<string, bool> appFilter)
    {
#if WINDOWS
        return new MediaSessionWatcher(appFilter);
#else
        if (OperatingSystem.IsMacOS())
            return new MacSpotifyWatcher();
        throw new PlatformNotSupportedException("Yalnızca Windows ve macOS destekleniyor.");
#endif
    }

    /// <summary>İki anlık görüntü arasındaki değişiklikleri (şarkı, oynat/duraklat, sarma) günlüğe yazar.</summary>
    internal static void ReportChanges(MediaSnapshot? prev, MediaSnapshot cur, Action<string>? log)
    {
        if (log is null) return;

        if (prev is null || prev.TrackKey != cur.TrackKey)
        {
            if (!string.IsNullOrWhiteSpace(cur.Title))
                log($"Şarkı: {cur.Artist} — {cur.Title} ({MediaSnapshot.Fmt(cur.Duration)})");
            return;
        }

        if (prev.IsPlaying != cur.IsPlaying)
            log(cur.IsPlaying
                ? $"Oynatma başladı @ {MediaSnapshot.Fmt(cur.Position)}"
                : $"Duraklatıldı @ {MediaSnapshot.Fmt(cur.Position)}");

        // Yeni bir zaman çizelgesi değeri geldiyse, önceki tahminle karşılaştır.
        if (cur.PositionTimestamp != prev.PositionTimestamp)
        {
            var expected = prev.EstimatePosition(cur.PositionTimestamp);
            var drift = cur.Position - expected;

            if (drift.Duration() >= SeekThreshold)
                log($"Sarma algılandı: {MediaSnapshot.Fmt(expected)} → {MediaSnapshot.Fmt(cur.Position)}");
            else if (prev.IsPlaying && cur.IsPlaying && drift.Duration() >= TimeSpan.FromMilliseconds(50))
                log($"Senkron düzeltmesi: {drift.TotalMilliseconds:+0;-0} ms");
        }
    }
}
