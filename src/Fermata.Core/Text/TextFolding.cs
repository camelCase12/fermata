using System.Text;

namespace Fermata.Text;

/// <summary>
/// Case- and accent-insensitive text keys for searching and sorting.
/// </summary>
/// <remarks>
/// Folding lowercases, strips diacritics (<c>Björk</c> → <c>bjork</c>), expands ligatures
/// (<c>Æ</c> → <c>ae</c>), deletes apostrophes and periods inside words
/// (<c>Guns N' Roses</c> → <c>guns n roses</c>, <c>R.E.M.</c> → <c>rem</c>) and collapses every
/// other run of non-alphanumeric characters into a single space (<c>AC/DC</c> → <c>ac dc</c>).
/// </remarks>
public static class TextFolding
{
    /// <summary>Returns the folded form of <paramref name="text"/>, or <see cref="string.Empty"/>.</summary>
    public static string Fold(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
            return string.Empty;
        var builder = new StringBuilder(text.Length);
        AppendFolded(builder, text);
        return builder.ToString();
    }

    /// <summary>Appends the folded form of <paramref name="text"/>, separated from existing content by one space.</summary>
    public static void AppendFolded(StringBuilder builder, ReadOnlySpan<char> text)
    {
        bool pendingSpace = builder.Length > 0 && builder[^1] != ' ';
        foreach (char c in text)
        {
            if (IsJoiner(c) || IsCombiningMark(c))
                continue;
            if (!char.IsLetterOrDigit(c))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }
            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }
            char folded = FoldChar(c);
            if (folded == '\0')
                builder.Append(FoldingTable.Expansion(c));
            else
                builder.Append(folded);
        }
    }

    /// <summary>The sort key for a name.</summary>
    /// <remarks>
    /// It is the folded name without a leading English article, so "The Beatles" sorts under B. Digits
    /// sort before letters.
    /// </remarks>
    public static string SortKey(ReadOnlySpan<char> text)
    {
        string folded = Fold(text);
        foreach (string article in Articles)
        {
            if (folded.Length > article.Length && folded.StartsWith(article, StringComparison.Ordinal))
                return folded[article.Length..];
        }
        return folded;
    }

    private static readonly string[] Articles = ["the "];

    /// <summary>Folds one UTF-16 code unit.</summary>
    /// <returns>The folded unit, or NUL when <see cref="FoldingTable.Expansion"/> applies.</returns>
    internal static char FoldChar(char c)
    {
        if (c < 0x80)
            return c is >= 'A' and <= 'Z' ? (char)(c | 0x20) : c;
        if (c is >= FoldingTable.LatinFirst and <= FoldingTable.LatinLast)
            return FoldingTable.Latin[c - FoldingTable.LatinFirst];
        if (c is >= FoldingTable.GreekFirst and <= FoldingTable.GreekLast)
            return FoldingTable.Greek[c - FoldingTable.GreekFirst];
        if (c is >= FoldingTable.CyrillicFirst and <= FoldingTable.CyrillicLast)
            return FoldingTable.Cyrillic[c - FoldingTable.CyrillicFirst];
        if (c is >= FoldingTable.LatinAdditionalFirst and <= FoldingTable.LatinAdditionalLast)
            return FoldingTable.LatinAdditional[c - FoldingTable.LatinAdditionalFirst];
        return char.ToLowerInvariant(c);
    }

    // Punctuation that joins parts of one word. Removing it keeps "don't" and "dont" equal.
    private static bool IsJoiner(char c) => c is '\'' or '’' or 'ʼ' or '.';

    private static bool IsCombiningMark(char c) =>
        c is >= '̀' and <= 'ͯ' or >= '᪰' and <= '᫿' or >= '⃐' and <= '⃿';
}
