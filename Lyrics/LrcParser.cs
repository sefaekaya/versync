using System.Globalization;
using System.Text.RegularExpressions;

namespace LyricsSync.Probe.Lyrics;

/// <summary>Zaman damgalı tek bir söz satırı. Text boş olabilir (enstrümantal ara).</summary>
public sealed record LyricLine(TimeSpan Time, string Text);

/// <summary>
/// LRC formatı ayrıştırıcı.
/// Desteklenenler:
///  - [mm:ss], [mm:ss.x], [mm:ss.xx], [mm:ss.xxx] ve [mm:ss:xx]
///  - Aynı satırda birden çok zaman etiketi: [00:12.00][01:30.00]Nakarat
///  - [offset:+/-ms] etiketi
///  - Kelime düzeyi etiketler (&lt;mm:ss.xx&gt;) temizlenir, satır düzeyinde kullanılır
///  - [ar:], [ti:] gibi meta etiketler yok sayılır
/// </summary>
public static class LrcParser
{
    private static readonly Regex TimeTag =
        new(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    private static readonly Regex OffsetTag =
        new(@"^\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex WordTag =
        new(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>", RegexOptions.Compiled);

    public static IReadOnlyList<LyricLine> Parse(string? lrc)
    {
        if (string.IsNullOrWhiteSpace(lrc)) return Array.Empty<LyricLine>();

        var lines = new List<LyricLine>();
        var offset = TimeSpan.Zero;

        foreach (var raw in lrc.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var om = OffsetTag.Match(line);
            if (om.Success)
            {
                if (int.TryParse(om.Groups[1].Value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms))
                    offset = TimeSpan.FromMilliseconds(ms);
                continue;
            }

            // Satır başındaki ardışık zaman etiketlerini topla.
            var times = new List<TimeSpan>();
            int pos = 0;
            while (pos < line.Length)
            {
                var m = TimeTag.Match(line, pos);
                if (!m.Success || m.Index != pos) break;
                times.Add(ParseTime(m));
                pos = m.Index + m.Length;
                while (pos < line.Length && line[pos] == ' ') pos++;
            }

            if (times.Count == 0) continue; // meta etiket veya düz metin

            var text = WordTag.Replace(line[pos..], "").Trim();
            foreach (var t in times)
                lines.Add(new LyricLine(t, text));
        }

        // LRC standardında pozitif offset sözlerin daha erken gösterilmesi demektir.
        return lines
            .Select(l =>
            {
                var t = l.Time - offset;
                return l with { Time = t < TimeSpan.Zero ? TimeSpan.Zero : t };
            })
            .OrderBy(l => l.Time)
            .ToList();
    }

    /// <summary>Verilen konumdaki aktif satırın indeksi; ilk satırdan önceyse -1.</summary>
    public static int FindIndex(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        int lo = 0, hi = lines.Count - 1, result = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (lines[mid].Time <= position)
            {
                result = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return result;
    }

    private static TimeSpan ParseTime(Match m)
    {
        int minutes = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int seconds = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int millis = 0;

        if (m.Groups[3].Success)
        {
            var frac = m.Groups[3].Value;
            int value = int.Parse(frac, CultureInfo.InvariantCulture);
            millis = frac.Length switch
            {
                1 => value * 100,
                2 => value * 10,
                _ => value,
            };
        }

        return new TimeSpan(0, 0, minutes, seconds, millis);
    }
}
