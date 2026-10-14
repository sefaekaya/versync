using System.Net.Http.Json;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LyricsSync.Probe.Translation;

/// <summary>
/// Claude (Anthropic Messages API) ile şarkı sözü çevirisi.
/// Çıktının satır satır ve geçerli JSON olması için "tool use" zorunlu kılınır:
/// model, submit_translation aracını { source_language, translations[] } ile çağırmak zorundadır.
/// </summary>
public sealed class ClaudeTranslator : ITranslator
{
    private const string ToolName = "submit_translation";

    private static readonly JsonSerializerOptions PromptJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Unicode karakterler okunur kalsın
    };

    private readonly HttpClient _http;
    private readonly string _model;

    public ClaudeTranslator(string apiKey, string model)
    {
        _model = model;
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.anthropic.com/"),
            Timeout = TimeSpan.FromSeconds(120),
        };
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    public string Name => $"Claude ({_model})";
    public string CacheId => $"claude:{_model}";

    public async Task<TranslationResult> TranslateAsync(
        IReadOnlyList<string> lines, string title, string artist, string targetLanguage, CancellationToken ct)
    {
        var languageName = LanguageName(targetLanguage);

        var system =
            $"You translate song lyrics into {languageName}. " +
            "Translate for meaning and feeling, not word by word: render idioms, slang and metaphors " +
            $"the way a native {languageName} speaker would naturally say them, and keep the tone (romantic, angry, playful, etc.). " +
            "Rules: (1) Return exactly one translation per input line, in the same order; never merge, split, skip or reorder lines. " +
            "(2) Keep each translation about as short as the original so it can be read while the song plays. " +
            $"(3) If a line is already in {languageName}, return it unchanged. " +
            "(4) Keep non-lexical vocals (oh, yeah, la la, na na) and proper names as they are. " +
            "(5) Use the whole song as context so repeated or ambiguous lines are translated consistently. " +
            "(6) No notes, explanations or quotation marks — only the translated line text.";

        var userPayload = JsonSerializer.Serialize(new { artist, title, lines }, PromptJson);
        var user =
            $"Translate these lyrics into {languageName}. The input has {lines.Count} lines; " +
            $"your translations array must have exactly {lines.Count} items.\n\n{userPayload}";

        var body = new
        {
            model = _model,
            max_tokens = 8192,
            system,
            tools = new[]
            {
                new
                {
                    name = ToolName,
                    description = "Submit the line-by-line translation of the song lyrics.",
                    input_schema = new
                    {
                        type = "object",
                        properties = new
                        {
                            source_language = new
                            {
                                type = "string",
                                description = "ISO 639-1 code of the predominant language of the original lyrics, e.g. en, es, ko, tr.",
                            },
                            translations = new
                            {
                                type = "array",
                                items = new { type = "string" },
                                description = "One translated line per input line, same order and same count.",
                            },
                        },
                        @required = new[] { "source_language", "translations" },
                    },
                },
            },
            tool_choice = new { type = "tool", name = ToolName },
            messages = new[] { new { role = "user", content = user } },
        };

        using var resp = await _http.PostAsJsonAsync("v1/messages", body, ct);
        var json = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new TranslationException(DescribeError((int)resp.StatusCode, json));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("stop_reason", out var stop) && stop.GetString() == "max_tokens")
            throw new TranslationException("Claude yanıtı yarıda kesildi (şarkı çok uzun).");

        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            if (block.GetProperty("type").GetString() != "tool_use") continue;

            var input = block.GetProperty("input");
            string? source = input.TryGetProperty("source_language", out var sl) ? sl.GetString() : null;

            if (!input.TryGetProperty("translations", out var arr) || arr.ValueKind != JsonValueKind.Array)
                throw new TranslationException("Claude yanıtında çeviri listesi yok.");

            var translated = arr.EnumerateArray().Select(e => e.GetString() ?? "").ToList();
            if (translated.Count != lines.Count)
                throw new TranslationException($"Satır sayısı uyuşmadı ({lines.Count} gönderildi, {translated.Count} geldi).");

            return new TranslationResult(source?.Trim().ToLowerInvariant(), translated);
        }

        throw new TranslationException("Claude yanıtında çeviri bulunamadı.");
    }

    private static string DescribeError(int status, string json)
    {
        string? message = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.TryGetProperty("message", out var m))
                message = m.GetString();
        }
        catch { /* JSON değil */ }

        return status switch
        {
            401 => "Claude API anahtarı geçersiz (401).",
            403 => $"Claude API erişim izni yok (403). {message}",
            404 => $"Model bulunamadı (404): ayarlardaki ClaudeModel değerini kontrol et. {message}",
            429 => "Claude API hız/kota sınırı (429). Biraz sonra [E] ile tekrar dene.",
            >= 500 => $"Claude API geçici hata ({status}). [E] ile tekrar dene.",
            _ => $"Claude API hatası ({status}): {message}",
        };
    }

    internal static string LanguageName(string code) => code switch
    {
        "tr" => "Turkish",
        "en" => "English",
        "de" => "German",
        "fr" => "French",
        "es" => "Spanish",
        _ => code,
    };

    public void Dispose() => _http.Dispose();
}
