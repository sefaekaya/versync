using System.Collections.Concurrent;
using LyricsSync.Probe.Lyrics;
using LyricsSync.Probe.Storage;
using LyricsSync.Probe.Translation;

namespace LyricsSync.Probe;

public enum DisplayMode
{
    Both,             // orijinal + altında çeviri
    TranslationOnly,  // yalnızca çeviri (yoksa orijinal)
    OriginalOnly,     // yalnızca orijinal
}

/// <summary>
/// Konsol arayüzü (~20 FPS). Her karede:
///  1) SMTC anlık görüntüsünden konumu yerel saatle tahmin eder,
///  2) söz ve çeviri servislerini günceller,
///  3) şarkı bazlı ofseti ekleyerek aktif satırı bulur, orijinali ve çevirisini vurgular,
///  4) klavye kısayollarını işler.
/// </summary>
public sealed class ConsoleRenderer
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(50);

    /// <summary>Aktif satırın üstünde ve altında gösterilecek söz satırı sayısı.</summary>
    private const int LinesBefore = 1;
    private const int LinesAfter = 3;
    private const int Slots = LinesBefore + 1 + LinesAfter;
    /// <summary>Söz alanının sabit yüksekliği: 1 bilgi satırı + her söz için en fazla 2 satır.</summary>
    private const int LyricsRows = 1 + Slots * 2;

    private readonly record struct Row(string Text, ConsoleColor Color);

    private readonly MediaSessionWatcher _watcher;
    private readonly LyricsService _lyrics;
    private readonly TranslationService _translation;
    private readonly OffsetStore _offsets;
    private readonly ConcurrentQueue<string> _events;
    private readonly Action<string> _log;
    private readonly bool _followAny;
    private readonly bool _redirected = Console.IsOutputRedirected;
    private readonly bool _canReadKeys = !Console.IsInputRedirected;

    private DisplayMode _mode = DisplayMode.Both;
    private int _lastRowCount;
    private int _lastPrintedIndex = int.MinValue;
    private string? _lastPrintedTrack;

    public ConsoleRenderer(
        MediaSessionWatcher watcher,
        LyricsService lyrics,
        TranslationService translation,
        OffsetStore offsets,
        ConcurrentQueue<string> events,
        Action<string> log,
        bool followAny)
    {
        _watcher = watcher;
        _lyrics = lyrics;
        _translation = translation;
        _offsets = offsets;
        _events = events;
        _log = log;
        _followAny = followAny;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!_redirected)
        {
            Console.CursorVisible = false;
            Console.Clear();
        }

        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.Now;
            var snap = _watcher.Current;
            _lyrics.Update(snap, now);
            _translation.Update(_lyrics.Current, snap);

            if (_redirected)
            {
                PrintLyricChanges(snap, now);
            }
            else
            {
                HandleKeys(snap);
                Draw(snap, now);
            }

            try { await Task.Delay(FrameInterval, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Restore()
    {
        if (_redirected) return;
        try
        {
            Console.ResetColor();
            Console.CursorVisible = true;
            Console.SetCursorPosition(0, Math.Min(_lastRowCount, Math.Max(0, Console.BufferHeight - 1)));
        }
        catch { /* konsol kapanmış olabilir */ }
    }

    // ---- Klavye -----------------------------------------------------------------------

    private void HandleKeys(MediaSnapshot? snap)
    {
        if (!_canReadKeys) return;

        try
        {
            while (Console.KeyAvailable)
            {
                var key = Console.ReadKey(intercept: true);

                switch (char.ToLowerInvariant(key.KeyChar))
                {
                    case 't':
                        _mode = _mode switch
                        {
                            DisplayMode.Both => DisplayMode.TranslationOnly,
                            DisplayMode.TranslationOnly => DisplayMode.OriginalOnly,
                            _ => DisplayMode.Both,
                        };
                        _log($"Görünüm: {ModeLabel(_mode)}");
                        continue;

                    case 'e':
                        _log("Yeniden çevriliyor (önbellek atlandı)…");
                        _translation.Retranslate();
                        continue;
                }

                if (snap is null) continue;

                switch (key.KeyChar)
                {
                    case '+':
                    case '=':
                        _log($"Ofset: {FormatOffset(_offsets.Adjust(snap.TrackKey, +100))}");
                        break;
                    case '-':
                    case '_':
                        _log($"Ofset: {FormatOffset(_offsets.Adjust(snap.TrackKey, -100))}");
                        break;
                    case '0':
                        _offsets.Reset(snap.TrackKey);
                        _log("Ofset sıfırlandı");
                        break;
                    case 'r':
                    case 'R':
                        _log("Sözler yeniden aranıyor (önbellek atlandı)…");
                        _lyrics.Reload();
                        break;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Konsol girdisi yok; klavye kısayolları devre dışı.
        }
    }

    // ---- Çizim ------------------------------------------------------------------------

    private void Draw(MediaSnapshot? snap, DateTimeOffset now)
    {
        int width;
        try { width = Math.Max(40, Console.WindowWidth - 1); }
        catch { width = 79; }

        var rows = BuildRows(snap, now, width);

        try
        {
            Console.SetCursorPosition(0, 0);
            foreach (var row in rows)
            {
                Console.ForegroundColor = row.Color;
                Console.Write(Fit(row.Text, width));
                Console.Write('\n');
            }

            Console.ResetColor();
            for (int i = rows.Count; i < _lastRowCount; i++)
            {
                Console.Write(new string(' ', width));
                Console.Write('\n');
            }

            _lastRowCount = rows.Count;
        }
        catch
        {
            // Pencere yeniden boyutlandırılırken geçici hatalar olabilir.
        }
    }

    private List<Row> BuildRows(MediaSnapshot? snap, DateTimeOffset now, int width)
    {
        var rule = new Row(new string('─', width), ConsoleColor.DarkGray);
        var rows = new List<Row>
        {
            new(" Spotify Lyrics Sync — Aşama 3 (Türkçe çeviri)", ConsoleColor.White),
            rule,
        };

        if (snap is null)
        {
            rows.Add(new(_followAny
                ? " Çalan bir medya oturumu bulunamadı."
                : " Spotify bulunamadı. Spotify masaüstü uygulamasını açıp bir şarkı başlat.", ConsoleColor.Yellow));
            rows.Add(new("", ConsoleColor.Gray));
            var ids = _watcher.GetAllSessionIds();
            rows.Add(new(ids.Count == 0 ? " Sistemde hiç medya oturumu yok." : " Görünen oturumlar:", ConsoleColor.Gray));
            rows.AddRange(ids.Select(id => new Row($"   - {id}", ConsoleColor.DarkGray)));
        }
        else
        {
            var pos = snap.EstimatePosition(now);
            var offsetMs = _offsets.Get(snap.TrackKey);
            var lyrics = _lyrics.Current;
            var tr = _translation.Current;
            // Çeviri durumu yalnızca ekrandaki söz kümesine aitse geçerlidir.
            if (!ReferenceEquals(tr.Source, lyrics) && tr.Status != TranslationStatus.Disabled)
                tr = TranslationState.For(lyrics, TranslationStatus.Idle);

            rows.Add(new($" {(snap.IsPlaying ? "▶" : "⏸")} {Or(snap.Artist)} — {Or(snap.Title)}", ConsoleColor.White));
            rows.Add(new($" {MediaSessionWatcher.Fmt(pos)} / {MediaSessionWatcher.Fmt(snap.Duration)}  "
                         + ProgressBar(pos, snap.Duration, Math.Min(40, width - 30)), ConsoleColor.Gray));
            rows.Add(new($" {SourceLabel(lyrics, snap)} · ofset {FormatOffset(offsetMs)}", ConsoleColor.DarkGray));
            rows.Add(new($" {TranslationLabel(tr, lyrics, snap)} · görünüm: {ModeLabel(_mode)}", ConsoleColor.DarkGray));
            rows.Add(rule);
            rows.AddRange(BuildLyricRows(snap, lyrics, tr, pos + TimeSpan.FromMilliseconds(offsetMs)));
        }

        rows.Add(rule);
        rows.Add(new(" Olaylar:", ConsoleColor.Gray));
        var evs = _events.ToArray().TakeLast(4).ToArray();
        if (evs.Length == 0) rows.Add(new("   (henüz yok)", ConsoleColor.DarkGray));
        rows.AddRange(evs.Select(e => new Row("   " + e, ConsoleColor.DarkGray)));
        rows.Add(rule);
        rows.Add(new(" [T] görünüm  [E] yeniden çevir  [+/-] ofset  [0] ofset sıfırla  [R] sözleri yeniden ara  [Ctrl+C] çıkış",
            ConsoleColor.DarkGray));

        return rows;
    }

    /// <summary>Her zaman tam <see cref="LyricsRows"/> satır döndürür, böylece düzen zıplamaz.</summary>
    private List<Row> BuildLyricRows(MediaSnapshot snap, LyricsState lyrics, TranslationState tr, TimeSpan lyricPos)
    {
        var rows = new List<Row>(LyricsRows);

        if (lyrics.TrackKey != snap.TrackKey || lyrics.Status is LyricsStatus.Loading or LyricsStatus.Idle)
        {
            rows.Add(new("   Sözler aranıyor…", ConsoleColor.DarkGray));
        }
        else
        {
            switch (lyrics.Status)
            {
                case LyricsStatus.Synced:
                {
                    rows.Add(new("", ConsoleColor.Gray));
                    var texts = lyrics.Lines.Select(l => l.Text).ToList();
                    AddWindow(rows, texts, LrcParser.FindIndex(lyrics.Lines, lyricPos), tr, () =>
                    {
                        var until = lyrics.Lines[0].Time - lyricPos;
                        return until > TimeSpan.Zero ? $"♪  (ilk satıra {until.TotalSeconds:0.0} sn)" : "♪";
                    });
                    break;
                }

                case LyricsStatus.PlainOnly:
                {
                    rows.Add(new("   (senkron söz yok — konum yaklaşık)", ConsoleColor.DarkYellow));
                    int count = lyrics.PlainLines.Count;
                    int idx = snap.Duration > TimeSpan.Zero
                        ? Math.Clamp((int)(lyricPos / snap.Duration * count), 0, count - 1)
                        : 0;
                    AddWindow(rows, lyrics.PlainLines, idx, tr, () => "♪");
                    break;
                }

                case LyricsStatus.Instrumental:
                    rows.Add(new("   ♪  Enstrümantal parça  ♪", ConsoleColor.Cyan));
                    break;

                case LyricsStatus.NotFound:
                    rows.Add(new("   Bu şarkı için söz bulunamadı. [R] ile yeniden deneyebilirsin.", ConsoleColor.Yellow));
                    break;

                case LyricsStatus.Error:
                    rows.Add(new($"   Sözler alınamadı: {lyrics.Error}", ConsoleColor.Red));
                    rows.Add(new("   İnternet bağlantını kontrol edip [R] ile yeniden dene.", ConsoleColor.DarkGray));
                    break;
            }
        }

        while (rows.Count < LyricsRows) rows.Add(new("", ConsoleColor.Gray));
        if (rows.Count > LyricsRows) rows.RemoveRange(LyricsRows, rows.Count - LyricsRows);
        return rows;
    }

    private void AddWindow(List<Row> rows, IReadOnlyList<string> texts, int index, TranslationState tr, Func<string> introText)
    {
        bool translated = tr.Status == TranslationStatus.Done;
        bool twoRows = translated && _mode == DisplayMode.Both;
        int rowsPerSlot = twoRows ? 2 : 1;

        for (int k = -LinesBefore; k <= LinesAfter; k++)
        {
            int i = index + k;
            int start = rows.Count;

            if (k == 0 && i < 0)
            {
                rows.Add(new(" ► " + introText(), ConsoleColor.Cyan));
            }
            else if (i == texts.Count)
            {
                rows.Add(new("   ·  ·  ·", ConsoleColor.DarkGray));
            }
            else if (i >= 0 && i < texts.Count)
            {
                var original = string.IsNullOrWhiteSpace(texts[i]) ? "♪" : texts[i].Trim();
                var translation = translated ? tr.Lookup(original) : null;

                bool active = k == 0;
                var prefix = active ? " ► " : "   ";
                var origColor = active ? ConsoleColor.Cyan : Math.Abs(k) == 1 ? ConsoleColor.Gray : ConsoleColor.DarkGray;
                var trColor = active ? ConsoleColor.Yellow : ConsoleColor.DarkYellow;

                switch (_mode)
                {
                    case DisplayMode.Both:
                        rows.Add(new(prefix + original, origColor));
                        if (twoRows && translation is not null && translation != original)
                            rows.Add(new("     " + translation, trColor));
                        break;

                    case DisplayMode.TranslationOnly:
                        if (translation is not null) rows.Add(new(prefix + translation, trColor));
                        else rows.Add(new(prefix + original, origColor));
                        break;

                    default:
                        rows.Add(new(prefix + original, origColor));
                        break;
                }
            }

            // Her söz yuvası sabit yükseklikte olsun.
            while (rows.Count - start < rowsPerSlot) rows.Add(new("", ConsoleColor.Gray));
        }
    }

    private static string SourceLabel(LyricsState state, MediaSnapshot snap)
    {
        if (state.TrackKey != snap.TrackKey) return "Sözler: aranıyor";

        var src = state.SourceId is long id ? $"LRCLIB #{id}" : "LRCLIB";
        var cache = state.FromCache ? ", önbellek" : "";
        return state.Status switch
        {
            LyricsStatus.Synced => $"Sözler: {src} (senkron{cache})",
            LyricsStatus.PlainOnly => $"Sözler: {src} (yalnızca düz{cache})",
            LyricsStatus.Instrumental => $"Sözler: {src} (enstrümantal{cache})",
            LyricsStatus.NotFound => "Sözler: bulunamadı",
            LyricsStatus.Error => "Sözler: hata",
            _ => "Sözler: aranıyor",
        };
    }

    private string TranslationLabel(TranslationState tr, LyricsState lyrics, MediaSnapshot snap)
    {
        var provider = _translation.ProviderName;
        var cache = tr.FromCache ? ", önbellek" : "";
        bool lyricsReady = lyrics.TrackKey == snap.TrackKey &&
                           lyrics.Status is LyricsStatus.Synced or LyricsStatus.PlainOnly;

        return tr.Status switch
        {
            TranslationStatus.Disabled =>
                $"Çeviri: kapalı — {_translation.DisabledReason} Ayar dosyası: {AppPaths.SettingsFile}",
            TranslationStatus.Translating => $"Çeviri: {provider} · çevriliyor…",
            TranslationStatus.Done => $"Çeviri: {provider} · hazır ({tr.SourceLanguage ?? "?"} → {_translation.TargetLanguage}{cache})",
            TranslationStatus.SameLanguage => $"Çeviri: şarkı zaten '{_translation.TargetLanguage}' dilinde, gösterilmiyor",
            TranslationStatus.Error => $"Çeviri: HATA — {tr.Error}",
            _ => lyricsReady ? $"Çeviri: {provider} · bekleniyor" : $"Çeviri: {provider}",
        };
    }

    // ---- Yönlendirilmiş çıktı (dosyaya yazma) -----------------------------------------

    private void PrintLyricChanges(MediaSnapshot? snap, DateTimeOffset now)
    {
        if (snap is null) return;

        if (snap.TrackKey != _lastPrintedTrack)
        {
            _lastPrintedTrack = snap.TrackKey;
            _lastPrintedIndex = int.MinValue;
            Console.WriteLine($"## {snap.Artist} — {snap.Title}");
        }

        var state = _lyrics.Current;
        if (state.TrackKey != snap.TrackKey || state.Status != LyricsStatus.Synced) return;

        var pos = snap.EstimatePosition(now) + TimeSpan.FromMilliseconds(_offsets.Get(snap.TrackKey));
        int idx = LrcParser.FindIndex(state.Lines, pos);
        if (idx == _lastPrintedIndex || idx < 0) return;

        _lastPrintedIndex = idx;
        var line = state.Lines[idx];
        Console.WriteLine($"[{MediaSessionWatcher.Fmt(line.Time)}] {(line.Text.Length == 0 ? "♪" : line.Text)}");

        var tr = _translation.Current;
        if (ReferenceEquals(tr.Source, state) && tr.Lookup(line.Text) is { } t)
            Console.WriteLine($"           {t}");
    }

    // ---- Yardımcılar ------------------------------------------------------------------

    private static string ModeLabel(DisplayMode m) => m switch
    {
        DisplayMode.Both => "orijinal + çeviri",
        DisplayMode.TranslationOnly => "yalnızca çeviri",
        _ => "yalnızca orijinal",
    };

    private static string FormatOffset(int ms) =>
        ms == 0 ? "0 ms" : $"{ms:+0;-0} ms ({(ms > 0 ? "sözler daha erken" : "sözler daha geç")})";

    private static string ProgressBar(TimeSpan pos, TimeSpan total, int barWidth)
    {
        if (barWidth < 10) barWidth = 10;
        double ratio = total > TimeSpan.Zero ? Math.Clamp(pos / total, 0, 1) : 0;
        int filled = (int)Math.Round(ratio * barWidth);
        return "[" + new string('█', filled) + new string('░', barWidth - filled) + "]";
    }

    private static string Or(string s) => string.IsNullOrWhiteSpace(s) ? "—" : s;

    private static string Fit(string s, int width) =>
        s.Length > width ? s[..(width - 1)] + "…" : s.PadRight(width);
}
