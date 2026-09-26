using System.Globalization;
using Avalonia.Data.Converters;

namespace Fermata.ViewModels;

/// <summary>Human-readable formatting shared by view models and bindings.</summary>
public static class Formats
{
    /// <summary>"3:07", or "1:02:33" for an hour or more.</summary>
    public static string Duration(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
            time = TimeSpan.Zero;
        int hours = (int)time.TotalHours;
        return hours > 0
            ? $"{hours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    /// <summary>"47 min", "2 hr 13 min", "3 days".</summary>
    public static string LongDuration(TimeSpan time)
    {
        if (time.TotalDays >= 2)
            return $"{time.TotalDays:0} days";
        int minutes = Math.Max(1, (int)Math.Round(time.TotalMinutes));
        if (minutes >= 60)
            return minutes % 60 > 0 ? $"{minutes / 60} hr {minutes % 60} min" : $"{minutes / 60} hr";
        return $"{minutes} min";
    }

    public static string Count(int count, string singular, string? plural = null) =>
        $"{count.ToString("N0", CultureInfo.InvariantCulture)} {(count == 1 ? singular : plural ?? singular + "s")}";

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>"Today", "Yesterday", "Monday", "12 March", "12 March 2024".</summary>
    public static string Day(DateTime local, DateTime today)
    {
        int days = (today.Date - local.Date).Days;
        return days switch
        {
            0 => "Today",
            1 => "Yesterday",
            < 7 => local.ToString("dddd", CultureInfo.InvariantCulture),
            _ when local.Year == today.Year => local.ToString("d MMMM", CultureInfo.InvariantCulture),
            _ => local.ToString("d MMMM yyyy", CultureInfo.InvariantCulture),
        };
    }

    public static readonly IValueConverter DurationConverter = new FuncValueConverter<TimeSpan, string>(Duration);
    public static readonly IValueConverter NotEmpty = new FuncValueConverter<string?, bool>(s => !string.IsNullOrEmpty(s));
    public static readonly IValueConverter YearText = new FuncValueConverter<int, string>(year => year > 0 ? year.ToString(CultureInfo.InvariantCulture) : "");
}
