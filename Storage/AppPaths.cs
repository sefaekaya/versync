namespace LyricsSync.Probe.Storage;

/// <summary>Uygulama verilerinin yeri: %LOCALAPPDATA%\LyricsSync</summary>
public static class AppPaths
{
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LyricsSync");

    public static string LyricsCacheDir => Path.Combine(Root, "cache", "lyrics");
    public static string TranslationCacheDir => Path.Combine(Root, "cache", "translations");
    public static string OffsetsFile => Path.Combine(Root, "offsets.json");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
}
