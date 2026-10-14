namespace LyricsSync.Probe;

/// <summary>
/// SMTC'den okunan anlık durum. Konum (Position) yalnızca PositionTimestamp anında doğrudur;
/// aradaki anlar için <see cref="EstimatePosition"/> ile tahmin yapılır (interpolasyon).
/// </summary>
public sealed record MediaSnapshot(
    string AppId,
    string Title,
    string Artist,
    string Album,
    TimeSpan Duration,
    TimeSpan Position,
    DateTimeOffset PositionTimestamp,
    bool IsPlaying,
    double PlaybackRate)
{
    /// <summary>Şarkıyı benzersiz tanımlamak için basit anahtar (şarkı değişimi algılama).</summary>
    public string TrackKey => $"{Title}\u001f{Artist}\u001f{Album}";

    /// <summary>
    /// Verilen andaki tahmini konum: son bilinen konum + (geçen süre × oynatma hızı).
    /// Duraklatılmışsa konum sabit kalır. Sonuç [0, Duration] aralığına sıkıştırılır.
    /// </summary>
    public TimeSpan EstimatePosition(DateTimeOffset now)
    {
        var position = Position;

        if (IsPlaying)
        {
            var elapsed = now - PositionTimestamp;
            if (elapsed > TimeSpan.Zero)
                position += TimeSpan.FromTicks((long)(elapsed.Ticks * PlaybackRate));
        }

        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && position > Duration) position = Duration;
        return position;
    }
}
