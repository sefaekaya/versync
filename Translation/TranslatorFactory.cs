using LyricsSync.Probe.Storage;

namespace LyricsSync.Probe.Translation;

public static class TranslatorFactory
{
    /// <summary>Ayarlara göre çevirmeni oluşturur; kullanılamıyorsa null ve nedenini döndürür.</summary>
    public static ITranslator? Create(AppSettings s, out string? disabledReason)
    {
        disabledReason = null;
        var provider = (s.TranslationProvider ?? "auto").Trim().ToLowerInvariant();
        bool hasClaude = s.AnthropicApiKey.Length > 0;
        bool hasDeepL = s.DeepLApiKey.Length > 0;

        switch (provider)
        {
            case "none":
            case "off":
                disabledReason = "Çeviri ayarlardan kapalı.";
                return null;

            case "claude":
                if (hasClaude) return new ClaudeTranslator(s.AnthropicApiKey, s.ClaudeModel);
                disabledReason = "Claude seçili ama AnthropicApiKey boş.";
                return null;

            case "deepl":
                if (hasDeepL) return new DeepLTranslator(s.DeepLApiKey);
                disabledReason = "DeepL seçili ama DeepLApiKey boş.";
                return null;

            default: // auto
                if (hasClaude) return new ClaudeTranslator(s.AnthropicApiKey, s.ClaudeModel);
                if (hasDeepL) return new DeepLTranslator(s.DeepLApiKey);
                disabledReason = "API anahtarı yok.";
                return null;
        }
    }
}
