using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LyricsSync.Probe.Storage;

/// <summary>
/// Çeviri önbelleği. Anahtar = sağlayıcı + hedef dil + sözlerin kendisi; böylece aynı şarkı
/// tekrar çalınca API'ye gidilmez, sözler değişirse (R ile yeniden arama) yeniden çevrilir.
/// </summary>
public sealed class TranslationCache
{
    public sealed class Entry
    {
        public DateTimeOffset CreatedAt { get; set; }
        public string? SourceLanguage { get; set; }
        public Dictionary<string, string> Map { get; set; } = new();
    }

    public TranslationCache()
    {
        Directory.CreateDirectory(AppPaths.TranslationCacheDir);
    }

    public static string KeyFor(string cacheId, string targetLanguage, IEnumerable<string> lines)
    {
        var raw = $"{cacheId}\n{targetLanguage}\n{string.Join("\n", lines)}";
        return Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
    }

    public Entry? TryGet(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<Entry>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    public void Save(string key, Entry entry)
    {
        try { File.WriteAllText(PathFor(key), JsonSerializer.Serialize(entry)); }
        catch { /* önbelleğe yazılamaması kritik değil */ }
    }

    public void Remove(string key)
    {
        try { File.Delete(PathFor(key)); } catch { /* yok say */ }
    }

    private static string PathFor(string key) => Path.Combine(AppPaths.TranslationCacheDir, key + ".json");
}
