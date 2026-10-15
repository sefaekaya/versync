using System.Collections.Concurrent;
using System.Text;
using LyricsSync.Probe;
using LyricsSync.Probe.Lyrics;
using LyricsSync.Probe.Storage;
using LyricsSync.Probe.Translation;

// -----------------------------------------------------------------------------------------
// Aşama 3: SMTC'den (Windows) veya AppleScript'ten (macOS) Spotify'ın çaldığı şarkıyı ve konumunu okur, LRCLIB'den senkron
// sözleri çeker, tüm şarkıyı Türkçeye çevirir (Claude veya DeepL) ve konsolda o anki
// satırı çevirisiyle birlikte vurgular.
//
// Kullanım:
//   dotnet run                → yalnızca Spotify'ı izler
//   dotnet run -- --any       → hangi uygulama çalıyorsa onu izler (tarayıcı, VLC vb. test için; yalnızca Windows)
//   dotnet run -- --list      → sistemdeki medya oturumlarını listeleyip çıkar
//   dotnet run > log.txt      → aktif söz satırlarını zaman damgasıyla dosyaya yazar
// -----------------------------------------------------------------------------------------

Console.OutputEncoding = Encoding.UTF8;

bool followAny = args.Contains("--any", StringComparer.OrdinalIgnoreCase);
bool listOnly = args.Contains("--list", StringComparer.OrdinalIgnoreCase);

Func<string, bool> filter = followAny
    ? _ => true
    : id => id.Contains("spotify", StringComparison.OrdinalIgnoreCase);

var events = new ConcurrentQueue<string>();
const int MaxEvents = 8;
void Log(string msg)
{
    events.Enqueue($"{DateTime.Now:HH:mm:ss}  {msg}");
    while (events.Count > MaxEvents && events.TryDequeue(out _)) { }
}

await using var watcher = MediaWatcher.Create(filter);
watcher.Log += Log;

try
{
    await watcher.StartAsync();
}
catch (Exception ex)
{
    Console.WriteLine($"Medya izleyici başlatılamadı: {ex.Message}");
    Console.WriteLine(OperatingSystem.IsWindows()
        ? "Windows 10 1809 veya üzeri gerekir."
        : "macOS'ta Spotify masaüstü uygulaması gerekir.");
    return 1;
}

if (listOnly)
{
    var ids = watcher.GetAllSessionIds();
    Console.WriteLine(ids.Count == 0 ? "Hiç medya oturumu yok." : "Medya oturumları:");
    foreach (var id in ids) Console.WriteLine($"  - {id}");
    return 0;
}

using var lrclib = new LrcLibClient();
using var lyrics = new LyricsService(lrclib, new LyricsCache());
lyrics.Log += Log;
var offsets = new OffsetStore();

var settings = AppSettings.LoadOrCreate();
var translator = TranslatorFactory.Create(settings, out var disabledReason);
using var translation = new TranslationService(translator, disabledReason, new TranslationCache(), settings.TargetLanguage);
translation.Log += Log;
Log(translator is null
    ? $"Çeviri kapalı: {disabledReason}"
    : $"Çeviri sağlayıcısı: {translator.Name} → {settings.TargetLanguage}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var renderer = new ConsoleRenderer(watcher, lyrics, translation, offsets, events, Log, followAny);
try
{
    await renderer.RunAsync(cts.Token);
}
finally
{
    renderer.Restore();
}

Console.WriteLine("Çıkıldı.");
return 0;
