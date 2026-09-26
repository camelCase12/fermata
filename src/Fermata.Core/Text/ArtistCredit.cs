namespace Fermata.Text;

/// <summary>Splits artist credits such as "Daft Punk feat. Pharrell Williams" into performer names.</summary>
/// <remarks>
/// Only unambiguous separators split a credit. Ampersands and commas do not, as in
/// "Simon &amp; Garfunkel" and "Earth, Wind &amp; Fire".
/// </remarks>
public static class ArtistCredit
{
    private static readonly string[] Separators =
    [
        " feat. ", " feat ", " ft. ", " ft ", " featuring ", " vs. ", " vs ", "; ", " / ", " × ",
        " (feat. ", " (ft. ", " [feat. ",
    ];

    /// <summary>How a credit reads on screen.</summary>
    /// <remarks>
    /// A credit reads as written, as in "A feat. B", except that list separators from tags, as in "A; B"
    /// and "A / B", read as a list such as "A &amp; B" or "A, B &amp; C".
    /// </remarks>
    public static string Display(string credit)
    {
        if (!credit.Contains("; ", StringComparison.Ordinal) && !credit.Contains(" / ", StringComparison.Ordinal))
            return credit;
        var names = Split(credit);
        return names.Count switch
        {
            < 2 => credit,
            2 => $"{names[0]} & {names[1]}",
            _ => string.Join(", ", names.Take(names.Count - 1)) + " & " + names[^1],
        };
    }

    public static List<string> Split(string credit)
    {
        var names = new List<string>(2);
        var rest = credit.AsSpan().Trim();
        while (!rest.IsEmpty)
        {
            int best = -1, width = 0;
            foreach (string separator in Separators)
            {
                int index = rest.IndexOf(separator, StringComparison.OrdinalIgnoreCase);
                if (index > 0 && (best < 0 || index < best))
                {
                    best = index;
                    width = separator.Length;
                }
            }
            var name = best < 0 ? rest : rest[..best];
            name = name.Trim().TrimEnd(")]").Trim();
            if (!name.IsEmpty && !Contains(names, name))
                names.Add(name.ToString());
            if (best < 0)
                break;
            rest = rest[(best + width)..];
        }
        return names;
    }

    private static bool Contains(List<string> names, ReadOnlySpan<char> name)
    {
        foreach (string existing in names)
        {
            if (name.Equals(existing, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
