using System.Text;

namespace Fermata.Metadata;

/// <summary>Text encodings used by ID3v2 and other binary tag formats.</summary>
internal enum TagTextEncoding : byte
{
    Latin1 = 0,
    /// <summary>UTF-16 with a byte-order mark (little-endian when the mark is missing).</summary>
    Utf16 = 1,
    Utf16BigEndian = 2,
    Utf8 = 3,
}

internal static class TextDecoding
{
    public static int TerminatorWidth(TagTextEncoding encoding) =>
        encoding is TagTextEncoding.Utf16 or TagTextEncoding.Utf16BigEndian ? 2 : 1;

    /// <summary>Index of the first NUL terminator, aligned for UTF-16; -1 if there is none.</summary>
    public static int IndexOfTerminator(ReadOnlySpan<byte> data, TagTextEncoding encoding)
    {
        if (TerminatorWidth(encoding) == 1)
            return data.IndexOf((byte)0);
        for (int i = 0; i + 1 < data.Length; i += 2)
        {
            if (data[i] == 0 && data[i + 1] == 0)
                return i;
        }
        return -1;
    }

    /// <summary>Decodes one string, stopping at the first terminator and trimming trailing NULs and spaces.</summary>
    public static string Decode(ReadOnlySpan<byte> data, TagTextEncoding encoding)
    {
        int end = IndexOfTerminator(data, encoding);
        if (end >= 0)
            data = data[..end];
        return DecodeRaw(data, encoding).TrimEnd('\0', ' ');
    }

    /// <summary>Decodes NUL-separated values (ID3v2.4 multi-value text frames).</summary>
    public static List<string> DecodeList(ReadOnlySpan<byte> data, TagTextEncoding encoding)
    {
        var values = new List<string>(1);
        int width = TerminatorWidth(encoding);
        // UTF-16 values each carry their own byte-order mark; remember the first one for values that omit it.
        bool bigEndian = encoding == TagTextEncoding.Utf16BigEndian;
        while (!data.IsEmpty)
        {
            int end = IndexOfTerminator(data, encoding);
            var value = end < 0 ? data : data[..end];
            if (encoding == TagTextEncoding.Utf16 && value.Length >= 2)
            {
                if (value[0] == 0xFE && value[1] == 0xFF)
                    bigEndian = true;
                else if (value[0] == 0xFF && value[1] == 0xFE)
                    bigEndian = false;
            }
            string text = encoding == TagTextEncoding.Utf16
                ? DecodeUtf16(value, bigEndian)
                : DecodeRaw(value, encoding);
            text = text.Trim('\0', ' ');
            if (text.Length > 0)
                values.Add(text);
            if (end < 0)
                break;
            data = data[(end + width)..];
        }
        return values;
    }

    public static string DecodeRaw(ReadOnlySpan<byte> data, TagTextEncoding encoding) => encoding switch
    {
        TagTextEncoding.Latin1 => Encoding.Latin1.GetString(data),
        TagTextEncoding.Utf16 => DecodeUtf16(data, bigEndian: false),
        TagTextEncoding.Utf16BigEndian => Encoding.BigEndianUnicode.GetString(data),
        _ => DecodeUtf8(data),
    };

    public static string DecodeUtf8(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            data = data[3..];
        return Encoding.UTF8.GetString(data);
    }

    /// <summary>Decodes UTF-16, honouring a byte-order mark if present.</summary>
    public static string DecodeUtf16(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length >= 2)
        {
            if (data[0] == 0xFF && data[1] == 0xFE)
            {
                data = data[2..];
                bigEndian = false;
            }
            else if (data[0] == 0xFE && data[1] == 0xFF)
            {
                data = data[2..];
                bigEndian = true;
            }
        }
        if ((data.Length & 1) != 0)
            data = data[..^1];
        return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(data);
    }

    /// <summary>Parses the leading integer of text such as "07", "3/12" or "2004-05-12".</summary>
    public static int LeadingInt(ReadOnlySpan<char> text)
    {
        text = text.TrimStart();
        int value = 0, digits = 0;
        foreach (char c in text)
        {
            if (c is < '0' or > '9' || digits == 9)
                break;
            value = value * 10 + (c - '0');
            digits++;
        }
        return value;
    }

    /// <summary>Parses "n/total" pairs used by track and disc numbers.</summary>
    public static (int Number, int Total) NumberPair(ReadOnlySpan<char> text)
    {
        int number = LeadingInt(text);
        int slash = text.IndexOf('/');
        int total = slash >= 0 ? LeadingInt(text[(slash + 1)..]) : 0;
        return (number, total);
    }

    /// <summary>Finds a four-digit year anywhere near the start of a date such as "2004-05-12" or "12.05.2004".</summary>
    public static int Year(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        for (int i = 0; i + 4 <= text.Length; i++)
        {
            var candidate = text.Slice(i, 4);
            bool fourDigits = char.IsAsciiDigit(candidate[0]) && char.IsAsciiDigit(candidate[1])
                && char.IsAsciiDigit(candidate[2]) && char.IsAsciiDigit(candidate[3]);
            bool bounded = (i == 0 || !char.IsAsciiDigit(text[i - 1])) && (i + 4 == text.Length || !char.IsAsciiDigit(text[i + 4]));
            if (fourDigits && bounded)
            {
                int year = LeadingInt(candidate);
                if (year is >= 1000 and <= 9999)
                    return year;
            }
        }
        return 0;
    }

    /// <summary>Interprets tag booleans such as "1", "true" and "yes".</summary>
    public static bool Flag(ReadOnlySpan<char> text)
    {
        text = text.Trim();
        return text is "1" || text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }
}
