# LyricsSync.Probe — Aşama 3 (Türkçe çeviri)

Spotify masaüstü uygulamasının çaldığı şarkıyı ve anlık konumunu Windows medya oturumundan (SMTC)
okur, [LRCLIB](https://lrclib.net)'den senkron sözleri çeker, **tüm şarkıyı tek seferde Türkçeye
çevirir** ve konsolda o anki satırı çevirisiyle birlikte vurgular.

## Gereksinimler

- Windows 10 (1809+) veya Windows 11, .NET 8 SDK, Spotify masaüstü uygulaması
- Çeviri için **bir** API anahtarı:
  - **Claude** (önerilen, şarkı sözünde deyim/argo/mecazı daha iyi çevirir): https://console.anthropic.com → API Keys
  - **DeepL** (ayda 500.000 karaktere kadar ücretsiz plan var): https://www.deepl.com/pro-api

## Kurulum: API anahtarı

Programı bir kez çalıştırınca şu dosya oluşur:

```
%LOCALAPPDATA%\LyricsSync\settings.json
```

Not Defteri ile aç (`Win+R` → `notepad %LOCALAPPDATA%\LyricsSync\settings.json`) ve anahtarını yaz:

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

```powershell
dotnet run                 # Spotify'ı izler
dotnet run -- --any        # Hangi uygulama çalıyorsa onu izler
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
3. Claude'a çıktıyı bir araç çağrısı (tool use) olarak vermesi zorunlu kılınır; böylece yanıt
   her zaman geçerli JSON ve satır sayısı girdiyle aynı olur. Sayı tutmazsa hata gösterilir, `E` ile tekrar denenir.
4. Şarkının dili algılanır; şarkı zaten Türkçeyse çeviri gösterilmez.
5. Sonuç `%LOCALAPPDATA%\LyricsSync\cache\translations\` altına kaydedilir. Aynı şarkı tekrar
   çalınca API'ye gidilmez, maliyet sıfırdır.

**Yaklaşık maliyet (Claude Sonnet 5.5):** Ortalama bir şarkı ~1.500 girdi + ~1.500 çıktı token'ı,
yani şarkı başına yaklaşık 1,5–2 sent. Haiku ile çok daha ucuz. Her şarkı yalnızca ilk çalınışında ücretlendirilir.

## Test listesi

1. Anahtarı girip İngilizce bir şarkı çal → durum satırında "çevriliyor…", birkaç saniye sonra "hazır (en → tr)"
2. Çeviriler doğal mı? Deyim/argo içeren bir şarkı dene
3. Nakarat tekrarlarında çeviri tutarlı mı?
4. `T` ile görünümleri dene
5. Aynı şarkıyı tekrar aç → "önbellek" yazıyor ve anında geliyor mu?
6. Türkçe bir şarkı çal → "zaten 'tr' dilinde" yazıyor mu?
7. İspanyolca/Korece gibi başka dilde bir şarkı dene
8. Yanlış anahtar gir → anlaşılır bir hata mesajı çıkıyor mu?

## Dosyalar

| Dosya | Görev |
|---|---|
| `MediaSessionWatcher.cs`, `MediaSnapshot.cs` | SMTC izleme, konum tahmini |
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
