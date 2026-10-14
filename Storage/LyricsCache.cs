using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LyricsSync.Probe.Lyrics;

namespace LyricsSync.Probe.Storage;

/// <summary>
/// LRCLIB sonuçlarının disk önbelleği (şarkı başına bir JSON dosyası).
/// Bulunan sözler kalıcı saklanır; "bulunamadı" sonucu 1 gün sonra yeniden denenir.
/// (Aşama 3'te çevirilerle birlikte SQLite'a taşınacak.)
/// </summary>
public sealed class LyricsCache
{
    private static readonly TimeSpan NotFoundTtl = TimeSpan.FromDays(1);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

    private sealed class Entry
    {
        public DateTimeOffset FetchedAt { get; set; }
        public LrcLibTrack? Track { get; set; }
    }

    public LyricsCache()
    {
        Directory.CreateDirectory(AppPaths.LyricsCacheDir);
    }

    /// <summary>true: önbellekte geçerli kayıt var (track null olabilir = bulunamadı).</summary>
    public bool TryGet(TrackQuery q, out LrcLibTrack? track)
    {
        track = null;
        var path = PathFor(q);
        if (!File.Exists(path)) return false;

        try
        {
            var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(path), JsonOptions);
            if (entry is null) return false;
            if (entry.Track is null && DateTimeOffset.Now - entry.FetchedAt > NotFoundTtl) return false;

            track = entry.Track;
            return true;
        }
        catch
        {
            return false; // bozuk dosya: yeniden indir
        }
    }

    public void Save(TrackQuery q, LrcLibTrack? track)
    {
        try
        {
            var entry = new Entry { FetchedAt = DateTimeOffset.Now, Track = track };
            File.WriteAllText(PathFor(q), JsonSerializer.Serialize(entry, JsonOptions));
        }
        catch
        {
            // Önbelleğe yazılamaması uygulamayı durdurmamalı.
        }
    }

    public void Remove(TrackQuery q)
    {
        try { File.Delete(PathFor(q)); } catch { /* yok say */ }
    }

    private static string PathFor(TrackQuery q)
    {
        var key = $"{q.Title}|{q.Artist}|{q.Album}|{q.DurationSeconds}".ToLowerInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(AppPaths.LyricsCacheDir, hash + ".json");
    }
}
