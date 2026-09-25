namespace Fermata.Imaging;

/// <summary>
/// One family of similar colours in a cover. The colour is representative of the family's richer
/// pixels, and the position and covariance describe where in the cover the family's pixels are, in
/// coordinates from 0 to 1 across the cover's square. The weight is the fraction of the cover's pixels
/// that belong to the family. A neutral family holds the cover's greys, near-blacks and near-whites
/// rather than one of its hues.
/// </summary>
public readonly record struct ColorBlob(Oklab Color, double X, double Y, double Sxx, double Sxy, double Syy, double Weight,
    bool IsNeutral = false);

/// <summary>
/// What a cover looks like, reduced to a few numbers: its hue families, the colour to use as an accent,
/// the direction its shapes and edges mostly run in, how strongly they agree on that direction, how much
/// fine detail it has, and a seed that differs from cover to cover.
/// </summary>
/// <param name="Accent">The richest common hue, or null when the cover has no real colour.</param>
/// <param name="Orientation">The dominant direction of structure in radians, measured from the x axis.</param>
/// <param name="Coherence">How consistently the structure follows that direction, from 0 to 1.</param>
/// <param name="Detail">How busy the cover is, from 0 for flat colour to 1 for dense detail.</param>
public sealed record CoverPalette(IReadOnlyList<ColorBlob> Blobs, Oklab? Accent, double Orientation, double Coherence,
    double Detail, uint Seed)
{
    /// <summary>
    /// The most blobs a palette has. A single cover has at most six, which are four hue families and the
    /// dark and light neutral families, and a mosaic keeps its six heaviest.
    /// </summary>
    public const int MaxBlobs = 6;

    /// <summary>
    /// Combines the palettes of up to four covers shown as a 2×2 mosaic. Each cover's blobs move into its
    /// quadrant, and the heaviest blobs overall are kept.
    /// </summary>
    public static CoverPalette Mosaic(IReadOnlyList<CoverPalette> covers)
    {
        if (covers.Count == 1)
            return covers[0];
        var blobs = new List<ColorBlob>();
        double orientationX = 0, orientationY = 0, detail = 0;
        uint seed = 2166136261;
        for (int i = 0; i < covers.Count; i++)
        {
            var cover = covers[i];
            double left = i % 2 * 0.5, top = i / 2 * 0.5;
            foreach (var blob in cover.Blobs)
            {
                blobs.Add(blob with
                {
                    X = left + blob.X * 0.5,
                    Y = top + blob.Y * 0.5,
                    Sxx = blob.Sxx * 0.25,
                    Sxy = blob.Sxy * 0.25,
                    Syy = blob.Syy * 0.25,
                    Weight = blob.Weight / covers.Count,
                });
            }
            // Directions are averaged on the doubled angle, because a direction and its opposite are the same.
            orientationX += cover.Coherence * Math.Cos(2 * cover.Orientation);
            orientationY += cover.Coherence * Math.Sin(2 * cover.Orientation);
            detail += cover.Detail / covers.Count;
            seed = (seed ^ cover.Seed) * 16777619;
        }
        var kept = blobs.OrderByDescending(b => b.Weight).Take(MaxBlobs).ToList();
        var accent = covers.Select(c => c.Accent).FirstOrDefault(a => a is not null);
        double coherence = Math.Min(1, Math.Sqrt(orientationX * orientationX + orientationY * orientationY) / covers.Count);
        return new CoverPalette(kept, accent, Math.Atan2(orientationY, orientationX) / 2, coherence, detail, seed);
    }
}

/// <summary>
/// Measures a cover's colours and structure. Colours are grouped by hue rather than averaged, so two
/// different hues are never mixed into a third colour that the cover does not contain.
/// </summary>
public static class CoverAnalysis
{
    private const int HueBins = 72;
    private const double MinimumChroma = 0.04;
    private const double FamilyWidth = 20 * Math.PI / 180;
    private const double AssignWidth = 45 * Math.PI / 180;
    private const int MaxFamilies = 4;
    private const int DarkNeutral = -2, LightNeutral = -3;

    /// <summary>Analyses a square image given as RGBA bytes, row by row.</summary>
    public static CoverPalette Analyze(ReadOnlySpan<byte> rgba, int size)
    {
        int count = size * size;
        var colors = new Oklab[count];
        var weights = new double[count];
        var histogram = new double[HueBins];
        uint seed = 2166136261;
        double chromatic = 0;
        for (int i = 0; i < count; i++)
        {
            var color = Oklab.FromSrgb(rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]);
            colors[i] = color;
            seed = (seed ^ rgba[i * 4] ^ (uint)rgba[i * 4 + 1] << 8 ^ (uint)rgba[i * 4 + 2] << 16) * 16777619;
            double chroma = color.Chroma;
            if (chroma < MinimumChroma || color.L < 0.15 || color.L > 0.97)
                continue;
            // Richer colours count for more, and very dark or very light pixels for less.
            double weight = Math.Pow(chroma, 1.5) * (1 - 0.8 * Math.Abs(color.L - 0.6));
            weights[i] = weight;
            chromatic += weight;
            histogram[Bin(color.Hue)] += weight;
        }

        var hues = chromatic > 0 ? Peaks(Smooth(histogram)) : [];
        var families = Assign(colors, weights, hues);
        var blobs = new List<ColorBlob>();
        Oklab? accent = null;
        double bestScore = 0;
        for (int f = 0; f < hues.Count; f++)
        {
            if (Family(colors, weights, families, f, hues[f], size) is not { } blob)
                continue;
            blobs.Add(blob);
            // The accent is the family that is both rich and common, measured by chroma-weighted mass.
            double mass = 0;
            for (int i = 0; i < count; i++)
            {
                if (families[i] == f)
                    mass += weights[i];
            }
            double score = Math.Pow(mass / chromatic, 0.6) * blob.Color.Chroma;
            if (score > bestScore)
                (accent, bestScore) = (blob.Color, score);
        }
        // Pixels without a family (greys, near-black, near-white and stray hues) make two neutral families,
        // one dark and one light, so black and white are not averaged into grey. A neutral family is left
        // out when it covers very little of the cover.
        foreach (bool light in new[] { false, true })
        {
            if (Neutral(colors, families, light, size) is { } neutral && (neutral.Weight >= 0.015 || blobs.Count == 0))
                blobs.Add(neutral with { IsNeutral = true });
        }
        blobs.Sort((x, y) => y.Weight.CompareTo(x.Weight));

        var (orientation, coherence, detail) = Structure(colors, size);
        return new CoverPalette(blobs, accent, orientation, coherence, detail, seed);
    }

    private static int Bin(double hue) => (int)((hue + Math.PI) / (2 * Math.PI) * HueBins) % HueBins;

    private static double BinHue(int bin) => (bin + 0.5) / HueBins * 2 * Math.PI - Math.PI;

    private static double[] Smooth(double[] histogram)
    {
        var smoothed = new double[HueBins];
        for (int i = 0; i < HueBins; i++)
        {
            for (int d = -4; d <= 4; d++)
                smoothed[i] += histogram[(i + d + HueBins) % HueBins] * Math.Exp(-d * d / 8.0);
        }
        return smoothed;
    }

    /// <summary>The hues of the strongest peaks of the histogram, strongest first.</summary>
    private static List<double> Peaks(double[] histogram)
    {
        double max = histogram.Max();
        var peaks = new List<(double Hue, double Mass)>();
        for (int i = 0; i < HueBins; i++)
        {
            double value = histogram[i];
            if (value < max * 0.06 || value < histogram[(i + 1) % HueBins] || value <= histogram[(i - 1 + HueBins) % HueBins])
                continue;
            double mass = 0;
            for (int d = -3; d <= 3; d++)
                mass += histogram[(i + d + HueBins) % HueBins];
            peaks.Add((BinHue(i), mass));
        }
        var kept = new List<double>();
        foreach (var (hue, _) in peaks.OrderByDescending(p => p.Mass))
        {
            if (kept.Count < MaxFamilies && kept.All(k => AngleBetween(k, hue) > 25 * Math.PI / 180))
                kept.Add(hue);
        }
        return kept;
    }

    private static double AngleBetween(double a, double b)
    {
        double d = Math.Abs(a - b) % (2 * Math.PI);
        return d > Math.PI ? 2 * Math.PI - d : d;
    }

    /// <summary>
    /// Assigns each pixel to the family with the nearest hue, if it is chromatic and within
    /// <see cref="AssignWidth"/> of it. Other pixels are assigned −1.
    /// </summary>
    private static int[] Assign(Oklab[] colors, double[] weights, List<double> hues)
    {
        var families = new int[colors.Length];
        for (int i = 0; i < colors.Length; i++)
        {
            families[i] = -1;
            if (weights[i] == 0)
                continue;
            double nearest = AssignWidth;
            for (int f = 0; f < hues.Count; f++)
            {
                double distance = AngleBetween(colors[i].Hue, hues[f]);
                if (distance <= nearest)
                    (families[i], nearest) = (f, distance);
            }
        }
        return families;
    }

    /// <summary>Describes the pixels of one family as a blob.</summary>
    /// <remarks>
    /// The family's lightness and hue are the chroma-weighted means of the pixels within
    /// <see cref="FamilyWidth"/> of its peak hue, and its chroma is their 80th percentile, so the colour
    /// is one the cover really shows rather than an average diluted by duller pixels.
    /// </remarks>
    private static ColorBlob? Family(Oklab[] colors, double[] weights, int[] families, int family, double hue, int size)
    {
        double total = 0, lightness = 0, sinSum = 0, cosSum = 0;
        var chromas = new List<(double Chroma, double Weight)>();
        for (int i = 0; i < colors.Length; i++)
        {
            double weight = weights[i];
            if (families[i] != family || AngleBetween(colors[i].Hue, hue) > FamilyWidth)
                continue;
            total += weight;
            lightness += colors[i].L * weight;
            sinSum += Math.Sin(colors[i].Hue) * weight;
            cosSum += Math.Cos(colors[i].Hue) * weight;
            chromas.Add((colors[i].Chroma, weight));
        }
        if (total == 0)
            return null;
        chromas.Sort((a, b) => a.Chroma.CompareTo(b.Chroma));
        double target = total * 0.8, running = 0, chroma = chromas[^1].Chroma;
        foreach (var (c, w) in chromas)
        {
            running += w;
            if (running >= target)
            {
                chroma = c;
                break;
            }
        }
        var color = Oklab.FromLch(lightness / total, chroma, Math.Atan2(sinSum, cosSum));
        return Place(color, families, family, size);
    }

    /// <summary>
    /// Describes the dark or the light pixels without a family as one blob of their average colour. The
    /// pixels are marked in <paramref name="families"/> as <see cref="DarkNeutral"/> or
    /// <see cref="LightNeutral"/>.
    /// </summary>
    private static ColorBlob? Neutral(Oklab[] colors, int[] families, bool light, int size)
    {
        int family = light ? LightNeutral : DarkNeutral;
        double l = 0, a = 0, b = 0;
        int n = 0;
        for (int i = 0; i < colors.Length; i++)
        {
            if (families[i] == -1 && colors[i].L >= 0.5 == light)
                families[i] = family;
            if (families[i] != family)
                continue;
            l += colors[i].L;
            a += colors[i].A;
            b += colors[i].B;
            n++;
        }
        return n == 0 ? null : Place(new Oklab(l / n, a / n, b / n), families, family, size);
    }

    /// <summary>
    /// Makes a blob of the given colour from where the family's pixels are: their centroid, their
    /// covariance and the fraction of the cover they cover.
    /// </summary>
    private static ColorBlob Place(Oklab color, int[] families, int family, int size)
    {
        double x = 0, y = 0;
        int n = 0;
        for (int i = 0; i < families.Length; i++)
        {
            if (families[i] != family)
                continue;
            x += (i % size + 0.5) / size;
            y += (i / size + 0.5) / size;
            n++;
        }
        x /= n;
        y /= n;
        double sxx = 0, sxy = 0, syy = 0;
        for (int i = 0; i < families.Length; i++)
        {
            if (families[i] != family)
                continue;
            double dx = (i % size + 0.5) / size - x, dy = (i / size + 0.5) / size - y;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }
        // A small constant keeps a family that is a single line or point from having no extent.
        return new ColorBlob(color, x, y, sxx / n + 0.002, sxy / n, syy / n + 0.002, (double)n / families.Length);
    }

    /// <summary>
    /// The structure tensor of the lightness channel. Its main eigenvector is the direction in which
    /// lightness changes most, so shapes and edges run perpendicular to it.
    /// </summary>
    private static (double Orientation, double Coherence, double Detail) Structure(Oklab[] colors, int size)
    {
        double jxx = 0, jxy = 0, jyy = 0, magnitude = 0;
        int samples = 0;
        for (int y = 1; y < size - 1; y++)
        {
            for (int x = 1; x < size - 1; x++)
            {
                double gx = (colors[y * size + x + 1].L - colors[y * size + x - 1].L) / 2;
                double gy = (colors[(y + 1) * size + x].L - colors[(y - 1) * size + x].L) / 2;
                jxx += gx * gx;
                jxy += gx * gy;
                jyy += gy * gy;
                magnitude += Math.Sqrt(gx * gx + gy * gy);
                samples++;
            }
        }
        if (samples == 0)
            return (0, 0, 0);
        double half = (jxx + jyy) / 2, root = Math.Sqrt((jxx - jyy) * (jxx - jyy) / 4 + jxy * jxy);
        double coherence = half > 1e-12 ? Math.Pow(root / half, 2) : 0;
        double gradient = Math.Atan2(2 * jxy, jxx - jyy) / 2;
        double detail = Math.Clamp(magnitude / samples * 12, 0, 1);
        return (gradient + Math.PI / 2, coherence, detail);
    }
}
