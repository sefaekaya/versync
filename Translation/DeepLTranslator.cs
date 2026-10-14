using System.Net.Http.Json;
using System.Text.Json;

namespace LyricsSync.Probe.Translation;

/// <summary>
/// DeepL API ile çeviri. Ücretsiz anahtarlar ":fx" ile biter ve api-free.deepl.com'a gider.
/// Satırlar tek istekte dizi olarak gönderilir; şarkı bilgisi "context" alanıyla verilir
/// (context ücretlendirilmez ve çevrilmez).
/// </summary>
public sealed class DeepLTranslator : ITranslator
{
    private readonly HttpClient _http;

    public DeepLTranslator(string apiKey)
    {
        var isFree = apiKey.EndsWith(":fx", StringComparison.OrdinalIgnoreCase);
        _http = new HttpClient
        {
            BaseAddress = new Uri(isFree ? "https://api-free.deepl.com/" : "https://api.deepl.com/"),
            Timeout = TimeSpan.FromSeconds(60),
        };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"DeepL-Auth-Key {apiKey}");
    }

    public string Name => "DeepL";
    public string CacheId => "deepl";

    public async Task<TranslationResult> TranslateAsync(
        IReadOnlyList<string> lines, string title, string artist, string targetLanguage, CancellationToken ct)
    {
        var body = new
        {
            text = lines,
            target_lang = targetLanguage.ToUpperInvariant(),
            context = $"These are song lyrics, one line per text. Artist: {artist}. Song: {title}.",
        };

        using var resp = await _http.PostAsJsonAsync("v2/translate", body, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new TranslationException(DescribeError((int)resp.StatusCode, json));

        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement.GetProperty("translations").EnumerateArray().ToList();
        if (items.Count != lines.Count)
            throw new TranslationException($"Satır sayısı uyuşmadı ({lines.Count} gönderildi, {items.Count} geldi).");

        var translated = items.Select(i => i.GetProperty("text").GetString() ?? "").ToList();

        // Satır bazında algılanan dillerden en sık olanı şarkının dili kabul edilir.
        var source = items
            .Select(i => i.TryGetProperty("detected_source_language", out var d) ? d.GetString() : null)
            .Where(s => !string.IsNullOrEmpty(s))
            .GroupBy(s => s!.ToLowerInvariant())
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

        return new TranslationResult(source, translated);
    }

    private static string DescribeError(int status, string json)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("message", out var m)) message = m.GetString();
        }
        catch { /* JSON değil */ }

        return status switch
        {
            403 => "DeepL API anahtarı geçersiz (403).",
            429 => "DeepL hız sınırı (429). Biraz sonra [E] ile tekrar dene.",
            456 => "DeepL aylık karakter kotası doldu (456).",
            >= 500 => $"DeepL geçici hata ({status}). [E] ile tekrar dene.",
            _ => $"DeepL hatası ({status}): {message}",
        };
    }

    public void Dispose() => _http.Dispose();
}
