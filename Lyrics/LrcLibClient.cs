using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LyricsSync.Probe.Lyrics;

/// <summary>LRCLIB API'sinin döndürdüğü kayıt (yalnızca kullandığımız alanlar).</summary>
public sealed class LrcLibTrack
{
    public long Id { get; set; }
    public string? TrackName { get; set; }
    public string? ArtistName { get; set; }
    public string? AlbumName { get; set; }
    public double? Duration { get; set; }
    public bool Instrumental { get; set; }
    public string? PlainLyrics { get; set; }
    public string? SyncedLyrics { get; set; }

    public bool HasSynced => !string.IsNullOrWhiteSpace(SyncedLyrics);
    public bool HasPlain => !string.IsNullOrWhiteSpace(PlainLyrics);
}

/// <summary>Söz araması için şarkı bilgisi.</summary>
public sealed record TrackQuery(string Title, string Artist, string Album, TimeSpan Duration)
{
    public int DurationSeconds => (int)Math.Round(Duration.TotalSeconds);
}

/// <summary>
/// lrclib.net istemcisi.
/// Strateji:
///  1) /api/get ile birebir eşleşme (başlık + sanatçı + albüm + süre)
///  2) Olmazsa /api/search: temizlenmiş başlık + ana sanatçı, süre farkı ≤ 3 sn olanlar
///  3) Olmazsa /api/search?q=... serbest arama, yine süreye göre filtre
/// Senkron söz içeren kayıt her zaman düz söze tercih edilir.
/// </summary>
public sealed class LrcLibClient : IDisposable
{
    private const double DurationToleranceSeconds = 3;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    // " - Remastered 2011", " - Live", " - Radio Edit" gibi ekler
    private static readonly Regex DashSuffix = new(@"\s+-\s+.*$", RegexOptions.Compiled);
    // "(feat. X)", "[ft. X]", "(with X)"
    private static readonly Regex FeatPart = new(@"\s*[\(\[](feat\.?|ft\.?|with)\s[^\)\]]*[\)\]]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    // "A, B", "A & B", "A feat. B" → "A"
    private static readonly Regex ArtistSplit = new(@"\s*(,|&|;|\s+feat\.?\s+|\s+ft\.?\s+|\s+x\s+)\s*", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;

    public LrcLibClient()
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://lrclib.net/"),
            Timeout = TimeSpan.FromSeconds(20),
        };
        // LRCLIB, istemcinin kendini User-Agent ile tanıtmasını istiyor.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("LyricsSync/0.2");
    }

    public async Task<LrcLibTrack?> FindBestAsync(TrackQuery q, CancellationToken ct)
    {
        LrcLibTrack? plainFallback = null;

        // 1) Birebir eşleşme
        if (!string.IsNullOrWhiteSpace(q.Album) && q.DurationSeconds > 0)
        {
            var exact = await GetAsync(q, ct);
            if (exact is not null)
            {
                if (exact.HasSynced || exact.Instrumental) return exact;
                if (exact.HasPlain) plainFallback = exact;
            }
        }

        var cleanTitle = CleanTitle(q.Title);
        var primaryArtist = PrimaryArtist(q.Artist);

        // 2) Alanlı arama
        var results = await SearchAsync($"api/search?track_name={E(cleanTitle)}&artist_name={E(primaryArtist)}", ct);
        var best = PickBest(results, q);
        if (best is not null && (best.HasSynced || best.Instrumental)) return best;
        plainFallback ??= best;

        // 3) Serbest arama
        results = await SearchAsync($"api/search?q={E(primaryArtist + " " + cleanTitle)}", ct);
        best = PickBest(results, q);
        if (best is not null && (best.HasSynced || best.Instrumental)) return best;

        return plainFallback ?? best;
    }

    private async Task<LrcLibTrack?> GetAsync(TrackQuery q, CancellationToken ct)
    {
        var url = $"api/get?track_name={E(q.Title)}&artist_name={E(q.Artist)}&album_name={E(q.Album)}&duration={q.DurationSeconds}";
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<LrcLibTrack>(JsonOptions, ct);
    }

    private async Task<List<LrcLibTrack>> SearchAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return new List<LrcLibTrack>();
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<List<LrcLibTrack>>(JsonOptions, ct) ?? new List<LrcLibTrack>();
    }

    private static LrcLibTrack? PickBest(IEnumerable<LrcLibTrack> results, TrackQuery q)
    {
        bool checkDuration = q.Duration > TimeSpan.Zero;
        double target = q.Duration.TotalSeconds;

        return results
            .Where(r => r.HasSynced || r.HasPlain || r.Instrumental)
            .Where(r => !checkDuration || (r.Duration is double d && Math.Abs(d - target) <= DurationToleranceSeconds))
            .OrderByDescending(r => r.HasSynced)
            .ThenBy(r => checkDuration && r.Duration is double d ? Math.Abs(d - target) : 0)
            .FirstOrDefault();
    }

    internal static string CleanTitle(string title)
    {
        var t = FeatPart.Replace(title, "");
        t = DashSuffix.Replace(t, "");
        t = t.Trim();
        return t.Length > 0 ? t : title.Trim();
    }

    internal static string PrimaryArtist(string artist)
    {
        var parts = ArtistSplit.Split(artist);
        var first = parts.Length > 0 ? parts[0].Trim() : "";
        return first.Length > 0 ? first : artist.Trim();
    }

    private static string E(string s) => Uri.EscapeDataString(s ?? "");

    public void Dispose() => _http.Dispose();
}
