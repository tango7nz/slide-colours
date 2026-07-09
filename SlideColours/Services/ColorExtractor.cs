using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WpfColor = System.Windows.Media.Color;

namespace SlideColours.Services;

/// <summary>
/// Finds the dominant *colour* of a slide image — biased towards vivid, saturated pixels so that
/// white lyric text and black backgrounds don't drag the result to grey.
/// </summary>
public static class ColorExtractor
{
    private const int HueBins = 24;

    /// <summary>Returns the dominant colour, or null if the slide is essentially colourless (black/white/grey).</summary>
    public static WpfColor? Extract(byte[] imageBytes)
    {
        var frame = BitmapFrame.Create(new MemoryStream(imageBytes),
            BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var bitmap = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        int width = bitmap.PixelWidth, height = bitmap.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[stride * height];
        bitmap.CopyPixels(pixels, stride, 0);

        // Sample at most ~8000 pixels regardless of thumbnail size
        int step = Math.Max(1, (int)Math.Sqrt(width * (double)height / 8000));

        var binWeight = new double[HueBins];
        var binR = new double[HueBins];
        var binG = new double[HueBins];
        var binB = new double[HueBins];
        double vividWeight = 0;
        int sampleCount = 0;

        for (int y = 0; y < height; y += step)
        {
            int row = y * stride;
            for (int x = 0; x < width; x += step)
            {
                int i = row + x * 4;
                byte b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
                sampleCount++;

                RgbToHsv(r, g, b, out double hue, out double sat, out double val);
                if (val < 0.08 || sat < 0.15)
                    continue; // too dark or too grey to carry hue information

                double weight = sat * val; // vivid pixels dominate
                int bin = (int)(hue / 360.0 * HueBins) % HueBins;
                binWeight[bin] += weight;
                binR[bin] += r * weight;
                binG[bin] += g * weight;
                binB[bin] += b * weight;
                vividWeight += weight;
            }
        }

        if (sampleCount == 0 || vividWeight / sampleCount < 0.02)
            return null; // essentially a black & white slide

        // Best hue bin, scored together with its neighbours so a hue straddling a bin edge still wins
        int best = 0;
        double bestScore = -1;
        for (int i = 0; i < HueBins; i++)
        {
            double score = binWeight[i]
                + 0.5 * binWeight[(i + 1) % HueBins]
                + 0.5 * binWeight[(i + HueBins - 1) % HueBins];
            if (score > bestScore) { bestScore = score; best = i; }
        }

        double wSum = 0, rSum = 0, gSum = 0, bSum = 0;
        foreach (int i in new[] { (best + HueBins - 1) % HueBins, best, (best + 1) % HueBins })
        {
            wSum += binWeight[i];
            rSum += binR[i];
            gSum += binG[i];
            bSum += binB[i];
        }
        if (wSum <= 0)
            return null;

        return WpfColor.FromRgb((byte)(rSum / wSum), (byte)(gSum / wSum), (byte)(bSum / wSum));
    }

    /// <summary>Shapes an extracted colour for stage lighting: optional saturation boost and full-intensity normalisation.</summary>
    public static WpfColor ShapeForLighting(WpfColor c, double saturationBoost, bool fullBrightness)
    {
        RgbToHsv(c.R, c.G, c.B, out double h, out double s, out double v);
        s = Math.Min(1.0, s * (1.0 + saturationBoost));
        if (fullBrightness)
            v = 1.0;
        return HsvToRgb(h, s, v);
    }

    public static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        double rd = r / 255.0, gd = g / 255.0, bd = b / 255.0;
        double max = Math.Max(rd, Math.Max(gd, bd));
        double min = Math.Min(rd, Math.Min(gd, bd));
        double delta = max - min;

        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0)
            h = 0;
        else if (max == rd)
            h = 60 * (((gd - bd) / delta) % 6);
        else if (max == gd)
            h = 60 * ((bd - rd) / delta + 2);
        else
            h = 60 * ((rd - gd) / delta + 4);

        if (h < 0) h += 360;
    }

    public static WpfColor HsvToRgb(double h, double s, double v)
    {
        double c = v * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = v - c;
        (double r, double g, double b) = ((int)(h / 60) % 6) switch
        {
            0 => (c, x, 0.0),
            1 => (x, c, 0.0),
            2 => (0.0, c, x),
            3 => (0.0, x, c),
            4 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return WpfColor.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
