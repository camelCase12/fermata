namespace Fermata.Imaging;

/// <summary>A colour in the OKLab colour space.</summary>
/// <param name="L">The perceived lightness, from 0 to 1.</param>
/// <param name="A">The position on the green–red axis.</param>
/// <param name="B">The position on the blue–yellow axis.</param>
public readonly record struct Oklab(double L, double A, double B)
{
    public double Chroma => Math.Sqrt(A * A + B * B);

    /// <summary>Hue angle in radians.</summary>
    public double Hue => Math.Atan2(B, A);

    public static Oklab FromLch(double l, double chroma, double hue) => new(l, chroma * Math.Cos(hue), chroma * Math.Sin(hue));

    public Oklab WithLightness(double l) => FromLch(l, Chroma, Hue);

    public static Oklab Mix(Oklab a, Oklab b, double t) =>
        new(a.L + (b.L - a.L) * t, a.A + (b.A - a.A) * t, a.B + (b.B - a.B) * t);

    private static readonly double[] LinearTable = Enumerable.Range(0, 256).Select(v =>
    {
        double c = v / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }).ToArray();

    public static Oklab FromSrgb(byte r, byte g, byte b) => FromLinear(LinearTable[r], LinearTable[g], LinearTable[b]);

    public static Oklab FromLinear(double r, double g, double b)
    {
        double l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        double m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        double s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        return new(0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s,
            1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s,
            0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s);
    }

    public (double R, double G, double B) ToLinear()
    {
        double l = Math.Pow(L + 0.3963377774 * A + 0.2158037573 * B, 3);
        double m = Math.Pow(L - 0.1055613458 * A - 0.0638541728 * B, 3);
        double s = Math.Pow(L - 0.0894841775 * A - 1.2914855480 * B, 3);
        return (4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s,
            -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s,
            -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s);
    }

    /// <summary>Converts the colour to 8-bit sRGB.</summary>
    /// <remarks>A colour outside the sRGB gamut loses chroma until it fits, keeping its lightness and hue.</remarks>
    public (byte R, byte G, byte B) ToSrgb()
    {
        var color = this;
        for (int i = 0; i < 24 && !color.InGamut(); i++)
            color = FromLch(L, color.Chroma * 0.92, Hue);
        var (r, g, b) = color.ToLinear();
        return (Encode(r), Encode(g), Encode(b));
    }

    private bool InGamut()
    {
        var (r, g, b) = ToLinear();
        return r is >= -0.0005 and <= 1.0005 && g is >= -0.0005 and <= 1.0005 && b is >= -0.0005 and <= 1.0005;
    }

    private static byte Encode(double linear)
    {
        linear = Math.Clamp(linear, 0, 1);
        double c = linear <= 0.0031308 ? linear * 12.92 : 1.055 * Math.Pow(linear, 1 / 2.4) - 0.055;
        return (byte)Math.Round(c * 255);
    }
}
