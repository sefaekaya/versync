using System.Text.Json;

namespace LyricsSync.Probe.Storage;

/// <summary>
/// Kullanıcı ayarları: %LOCALAPPDATA%\LyricsSync\settings.json
/// Dosya yoksa varsayılanlarla oluşturulur. API anahtarları boşsa ortam değişkenlerinden okunur
/// (ANTHROPIC_API_KEY, DEEPL_API_KEY).
/// </summary>
public sealed class AppSettings
{
    /// <summary>"auto" | "claude" | "deepl" | "none". auto: önce Claude, yoksa DeepL anahtarı kullanılır.</summary>
    public string TranslationProvider { get; set; } = "auto";

    /// <summary>Hedef dil (ISO 639-1). Varsayılan Türkçe.</summary>
    public string TargetLanguage { get; set; } = "tr";

    public string AnthropicApiKey { get; set; } = "";
    public string ClaudeModel { get; set; } = "claude-sonnet-5-5";

    public string DeepLApiKey { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static AppSettings LoadOrCreate()
    {
        AppSettings settings;
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions)
                           ?? new AppSettings();
            }
            else
            {
                settings = new AppSettings();
                Directory.CreateDirectory(AppPaths.Root);
                File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(settings, JsonOptions));
            }
        }
        catch
        {
            settings = new AppSettings(); // bozuk dosya: varsayılanlarla devam
        }

        if (string.IsNullOrWhiteSpace(settings.AnthropicApiKey))
            settings.AnthropicApiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(settings.DeepLApiKey))
            settings.DeepLApiKey = Environment.GetEnvironmentVariable("DEEPL_API_KEY") ?? "";
        if (string.IsNullOrWhiteSpace(settings.TargetLanguage))
            settings.TargetLanguage = "tr";
        if (string.IsNullOrWhiteSpace(settings.ClaudeModel))
            settings.ClaudeModel = "claude-sonnet-5-5";

        settings.AnthropicApiKey = settings.AnthropicApiKey.Trim();
        settings.DeepLApiKey = settings.DeepLApiKey.Trim();
        settings.TargetLanguage = settings.TargetLanguage.Trim().ToLowerInvariant();
        return settings;
    }
}
