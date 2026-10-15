# LyricsSync

Spotify masaüstü uygulamasının çaldığı şarkıyı ve anlık konumunu okur (Windows'ta medya oturumundan
(SMTC), macOS'ta Spotify'ın AppleScript arayüzünden), [LRCLIB](https://lrclib.net)'den senkron sözleri çeker, **tüm şarkıyı tek seferde Türkçeye
çevirir** ve konsolda o anki satırı çevirisiyle birlikte vurgular.

## Gereksinimler

- **Windows:** Windows 10 (1809+) veya Windows 11, .NET 8 SDK, Spotify masaüstü uygulaması
- **macOS:** .NET 8 veya daha yeni SDK (ör. .NET 10), Spotify masaüstü uygulaması
  - İlk çalıştırmada macOS, terminalin Spotify'ı kontrol etmesi için izin ister; **İzin Ver** de.
    Yanlışlıkla reddettiysen: Sistem Ayarları → Gizlilik ve Güvenlik → Otomasyon → terminal
    uygulaman → Spotify'ı aç.
- Çeviri için **bir** API anahtarı:
  - **DeepL** Free Plan gayet yeterli çalışıyor
  - - **Claude** Kullanılabilir ama ücretli

## Kurulum: API anahtarı

Programı bir kez çalıştırınca şu dosya oluşur:

```
Windows: %LOCALAPPDATA%\LyricsSync\settings.json
macOS:   ~/Library/Application Support/LyricsSync/settings.json
```

Windows'ta Not Defteri ile aç (`Win+R` → `notepad %LOCALAPPDATA%\LyricsSync\settings.json`),
macOS'ta `open -e ~/Library/Application\ Support/LyricsSync/settings.json` ile aç ve anahtarını yaz:

```json
{
  "TranslationProvider": "auto",
  "TargetLanguage": "tr",
  "AnthropicApiKey": "sk-ant-...",
  "ClaudeModel": "claude-sonnet-5-5",
  "DeepLApiKey": ""
}
```

- `TranslationProvider`: `auto` (önce Claude, yoksa DeepL), `claude`, `deepl` veya `none`
- `ClaudeModel`: daha ucuz/hızlı çeviri için `claude-haiku-5-5` yazabilirsin
- Alternatif: anahtarı `ANTHROPIC_API_KEY` veya `DEEPL_API_KEY` ortam değişkeni olarak da verebilirsin

Ayarı değiştirdikten sonra programı yeniden başlat.

> Not: Anahtar bu prototipte düz metin dosyasında duruyor. Arayüzlü sürümde Windows Kimlik
> Bilgileri Yöneticisi'ne taşınacak.

## Çalıştırma

```bash
dotnet run                 # Spotify'ı izler
dotnet run -- --any        # Hangi uygulama çalıyorsa onu izler (yalnızca Windows)
dotnet run -- --list       # Medya oturumlarını listeler ve çıkar
dotnet run > log.txt       # Aktif satırları (ve çevirilerini) dosyaya yazar
```

## Tuşlar

| Tuş | İşlev |
|---|---|
| `T` | Görünüm: orijinal + çeviri → yalnızca çeviri → yalnızca orijinal |
| `E` | Şarkıyı önbelleği atlayarak yeniden çevirir |
| `+` / `-` | Bu şarkının ofsetini ±100 ms değiştirir (+ = sözler daha erken) |
| `0` | Bu şarkının ofsetini sıfırlar |
| `R` | Sözleri önbelleği atlayarak yeniden arar |
| `Ctrl+C` | Çıkış |

## Çeviri nasıl çalışıyor

1. Sözler yüklenince tüm şarkı **tek istekte** çevrilir; satır satır anlık çeviri yapılmaz.
2. Tekrar eden satırlar (nakarat) bir kez gönderilir; `♪` gibi harf içermeyen satırlar gönderilmez.
3. AI model çıktıyı bir araç çağrısı (tool use) olarak vermesi zorunlu kılınır; böylece yanıt
   her zaman geçerli JSON ve satır sayısı girdiyle aynı olur. Sayı tutmazsa hata gösterilir, `E` ile tekrar denenir.
4. Şarkının dili algılanır; şarkı zaten Türkçeyse çeviri gösterilmez.
5. Sonuç ayar dosyasının yanındaki `cache/translations/` klasörüne kaydedilir. Aynı şarkı tekrar
   çalınca API'ye gidilmez, maliyet sıfırdır.

## Dosyalar

| Dosya | Görev |
|---|---|
| `IMediaWatcher.cs` | Platform bağımsız izleyici arayüzü, platforma göre seçim |
| `MediaSessionWatcher.cs` | Windows: SMTC izleme |
| `MacSpotifyWatcher.cs` | macOS: Spotify'ı AppleScript (osascript) ile yoklama |
| `MediaSnapshot.cs` | Konum tahmini |
| `CoverArt.cs` | Kapağı konsolda Braille noktalarıyla çizme (Windows: WinRT, macOS: `sips`) |
| `Lyrics/LrcParser.cs`, `Lyrics/LrcLibClient.cs`, `Lyrics/LyricsService.cs` | Sözleri bulma ve ayrıştırma |
| `Translation/ITranslator.cs` | Çevirmen arayüzü |
| `Translation/ClaudeTranslator.cs` | Claude ile çeviri (tool use ile yapılandırılmış çıktı) |
| `Translation/DeepLTranslator.cs` | DeepL ile çeviri |
| `Translation/TranslatorFactory.cs` | Ayarlara göre sağlayıcı seçimi |
| `Translation/TranslationService.cs` | Şarkı değişince çeviri, tekilleştirme, dil algılama |
| `Storage/AppSettings.cs` | settings.json |
| `Storage/LyricsCache.cs`, `Storage/TranslationCache.cs`, `Storage/OffsetStore.cs` | Önbellekler ve ofsetler |
| `ConsoleRenderer.cs` | Konsol arayüzü ve tuşlar |
| `Program.cs` | Giriş noktası |
