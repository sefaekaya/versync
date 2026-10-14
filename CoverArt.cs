using System.Runtime.InteropServices;
using System.Text;
using Windows.Graphics.Imaging;

namespace LyricsSync.Probe;

/// <summary>
/// Albüm kapağını konsolda Braille noktalarıyla (her karakter 2×4 nokta) çizer.
/// Parlaklık, 4×4 Bayer eşik matrisiyle nokta yoğunluğuna çevrilir (noktalı baskı / halftone);
/// her karakterin rengi, yanan noktaların kapaktaki ortalama rengidir (24-bit ANSI).
/// </summary>
public sealed class CoverArt
{
    /// <summary>Nokta ızgarasının kenar uzunluğu. 96 nokta = 48 karakter × 24 satır (kare).</summary>
    public const int DotSize = 96;
    public const int Columns = DotSize / 2;
    public const int Rows = DotSize / 4;

    public string TrackKey { get; }
    public IReadOnlyList<string> Lines { get; }

    private CoverArt(string trackKey, IReadOnlyList<string> lines)
    {
        TrackKey = trackKey;
        Lines = lines;
    }

    /// <summary>SMTC küçük resim akışından kapağı okur ve noktalı çizime çevirir.</summary>
    public static async Task<CoverArt> FromStreamAsync(
        Windows.Storage.Streams.IRandomAccessStream stream, string trackKey)
    {
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var transform = new BitmapTransform
        {
            ScaledWidth = DotSize,
            ScaledHeight = DotSize,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var data = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            transform,
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);

        return new CoverArt(trackKey, Render(data.DetachPixelData()));
    }

    // Braille nokta bitleri: [sütun, satır] → bit.  Karakter = U+2800 + bitler.
    private static readonly int[,] DotBit =
    {
        { 0x01, 0x02, 0x04, 0x40 }, // sol sütun
        { 0x08, 0x10, 0x20, 0x80 }, // sağ sütun
    };

    // 4×4 Bayer eşik matrisi (0..15).
    private static readonly int[,] Bayer =
    {
        {  0,  8,  2, 10 },
        { 12,  4, 14,  6 },
        {  3, 11,  1,  9 },
        { 15,  7, 13,  5 },
    };

    private static List<string> Render(byte[] bgra)
    {
        // Parlaklık haritası + otomatik kontrast (koyu kapaklar da görünsün).
        var lum = new double[DotSize * DotSize];
        for (int i = 0; i < lum.Length; i++)
            lum[i] = 0.114 * bgra[i * 4] + 0.587 * bgra[i * 4 + 1] + 0.299 * bgra[i * 4 + 2];

        var sorted = (double[])lum.Clone();
        Array.Sort(sorted);
        double lo = sorted[(int)(sorted.Length * 0.03)];
        double hi = sorted[(int)(sorted.Length * 0.97)];
        if (hi - lo < 16) { lo = Math.Max(0, lo - 8); hi = lo + 16; }

        var lines = new List<string>(Rows);
        var sb = new StringBuilder();

        for (int cy = 0; cy < Rows; cy++)
        {
            sb.Clear();
            for (int cx = 0; cx < Columns; cx++)
            {
                int bits = 0, lit = 0;
                double r = 0, g = 0, b = 0;      // yanan noktaların rengi
                double ar = 0, ag = 0, ab = 0;   // hücrenin tüm noktalarının ortalama rengi

                for (int dx = 0; dx < 2; dx++)
                for (int dy = 0; dy < 4; dy++)
                {
                    int x = cx * 2 + dx, y = cy * 4 + dy;
                    int i = (y * DotSize + x) * 4;
                    ar += bgra[i + 2]; ag += bgra[i + 1]; ab += bgra[i];

                    double t = Math.Pow(Math.Clamp((lum[y * DotSize + x] - lo) / (hi - lo), 0, 1), 0.8);
                    double threshold = (Bayer[y & 3, x & 3] + 0.5) / 16.0;
                    if (t > threshold)
                    {
                        bits |= DotBit[dx, dy];
                        r += bgra[i + 2]; g += bgra[i + 1]; b += bgra[i];
                        lit++;
                    }
                }

                if (lit == 0)
                {
                    // Çok koyu hücre: hücrenin kendi renginin sönük tek noktası.
                    sb.Append("\u001b[38;2;")
                      .Append((int)(ar / 8 * 0.5 + 24)).Append(';')
                      .Append((int)(ag / 8 * 0.5 + 24)).Append(';')
                      .Append((int)(ab / 8 * 0.5 + 24)).Append('m')
                      .Append('\u2802');
                    continue;
                }

                sb.Append("\u001b[38;2;")
                  .Append(Boost(r / lit)).Append(';')
                  .Append(Boost(g / lit)).Append(';')
                  .Append(Boost(b / lit)).Append('m')
                  .Append((char)(0x2800 + bits));
            }
            sb.Append("\u001b[0m");
            lines.Add(sb.ToString());
        }

        return lines;
    }

    /// <summary>Küçük noktalar koyu terminalde sönük kalır; renkleri biraz canlandır.</summary>
    private static int Boost(double v) => (int)Math.Clamp(v * 1.25 + 30, 0, 255);

    // ---- ANSI (VT) desteği ------------------------------------------------------------

    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr h, uint mode);

    /// <summary>Konsolda ANSI renk kodlarını açar. Açılamazsa false döner (kapak gösterilmez).</summary>
    public static bool EnableAnsi()
    {
        try
        {
            var h = GetStdHandle(StdOutputHandle);
            if (!GetConsoleMode(h, out var mode)) return false;
            return (mode & EnableVirtualTerminalProcessing) != 0
                   || SetConsoleMode(h, mode | EnableVirtualTerminalProcessing);
        }
        catch
        {
            return false;
        }
    }
}
