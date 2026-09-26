using System.Globalization;
using System.Text;

/// <summary>The designs of Fermata's icons.</summary>
internal static class Designs
{
    public static List<Icon> All() =>
    [
        new Icon("Play").Solid("M8.5 5.8 L18.6 12 L8.5 18.2 Z"),
        new Icon("Pause").Box(6, 5, 10, 19, 1.5f).Box(14, 5, 18, 19, 1.5f),
        new Icon("Next").Solid("M6.5 6.8 L14.8 12 L6.5 17.2 Z").Box(16.5f, 6, 18.5f, 18, 1),
        new Icon("Previous").Box(5.5f, 6, 7.5f, 18, 1).Solid("M17.5 6.8 L9.2 12 L17.5 17.2 Z"),
        new Icon("Shuffle")
            .Path("M3.5 7 H7 C11 7 13 17 17 17 H19.5").Path("M3.5 17 H7 C11 17 13 7 17 7 H19.5")
            .Lines(17, 4.5f, 19.5f, 7, 17, 9.5f).Lines(17, 14.5f, 19.5f, 17, 17, 19.5f),
        Repeat("Repeat"),
        Repeat("RepeatOne").Lines(10.8f, 10.8f, 12.2f, 10, 12.2f, 14),
        new Icon("VolumeHigh").Solid(Speaker).Arc(11, 12, 4.5f, -40, 80).Arc(11, 12, 8, -45, 90),
        new Icon("VolumeMedium").Solid(Speaker).Arc(11, 12, 4.5f, -40, 80),
        new Icon("VolumeLow").Solid(Speaker),
        new Icon("VolumeOff").Solid(Speaker).Line(15, 9.5f, 20, 14.5f).Line(20, 9.5f, 15, 14.5f),
        new Icon("Heart").Solid(Heart),
        new Icon("HeartOutline").Path(Heart),
        new Icon("ThumbDown").Path("M3.5 4 H6.5 V13 H3.5 Z")
            .Path("M6.5 13 L10 19.8 C10.4 20.5 11.3 20.7 12 20.3 C12.8 19.8 13.1 18.9 12.8 18 L11.8 14.5 H17.8 C19.1 14.5 20.1 13.3 19.8 12 L18.6 5.6 C18.4 4.7 17.6 4 16.6 4 H6.5"),
        new Icon("Search").Circle(10.5f, 10.5f, 6.5f).Line(15.3f, 15.3f, 20, 20),
        new Icon("Home").Lines(3.5f, 11.5f, 12, 4.5f, 20.5f, 11.5f).Path("M6 9.6 V19.5 H10 V14.5 H14 V19.5 H18 V9.6"),
        new Icon("Explore").Circle(12, 12, 9).Solid("M15.2 8.8 L13.4 13.4 L8.8 15.2 L10.6 10.6 Z"),
        new Icon("Song").Dot(10.5f, 17, 3).Line(13.5f, 17, 13.5f, 4.5f).Path("M13.5 4.5 C15.5 5 18 6 18 9"),
        new Icon("Album").Circle(12, 12, 9).Circle(12, 12, 3),
        new Icon("Artist").Circle(12, 8, 4).Path("M4.5 20 C4.5 16.1 7.9 13.5 12 13.5 C16.1 13.5 19.5 16.1 19.5 20"),
        new Icon("Playlist").Line(4, 6, 15, 6).Line(4, 11, 15, 11).Line(4, 16, 10, 16)
            .Dot(16, 17.5f, 2.5f).Lines(18.5f, 17.5f, 18.5f, 8, 21, 9.2f),
        new Icon("PlaylistAdd").Line(4, 6, 16, 6).Line(4, 11, 16, 11).Line(4, 16, 11, 16)
            .Line(18, 12, 18, 20).Line(14, 16, 22, 16),
        new Icon("Queue").Line(4, 6, 20, 6).Line(4, 11, 20, 11).Line(4, 16, 11, 16).Solid("M15 14 L20 17 L15 20 Z"),
        new Icon("Radio").Dot(12, 12, 2).Arc(12, 12, 5.5f, -40, 80).Arc(12, 12, 5.5f, 140, 80)
            .Arc(12, 12, 9, -40, 80).Arc(12, 12, 9, 140, 80),
        new Icon("Folder").Path("M3.5 7 A1.5 1.5 0 0 1 5 5.5 H9.2 L11.2 8 H19 A1.5 1.5 0 0 1 20.5 9.5 V17.5 A1.5 1.5 0 0 1 19 19 H5 A1.5 1.5 0 0 1 3.5 17.5 Z"),
        new Icon("Settings").Path(Gear()).Circle(12, 12, 3),
        new Icon("History").Arc(13, 12, 8, 180, 300).Lines(2.8f, 9.8f, 5, 12.2f, 7.2f, 9.8f).Lines(13, 8, 13, 12, 16, 14),
        new Icon("Plus").Line(12, 5, 12, 19).Line(5, 12, 19, 12),
        new Icon("Close").Line(6, 6, 18, 18).Line(18, 6, 6, 18),
        new Icon("More").Dot(12, 5.5f, 1.8f).Dot(12, 12, 1.8f).Dot(12, 18.5f, 1.8f),
        new Icon("ChevronLeft").Lines(14.5f, 5.5f, 8, 12, 14.5f, 18.5f),
        new Icon("ChevronRight").Lines(9.5f, 5.5f, 16, 12, 9.5f, 18.5f),
        new Icon("ChevronDown").Lines(5.5f, 9, 12, 15.5f, 18.5f, 9),
        new Icon("Delete").Line(4, 6.5f, 20, 6.5f).Path("M9 6.5 V5 A1 1 0 0 1 10 4 H14 A1 1 0 0 1 15 5 V6.5")
            .Path("M6 6.5 L7 18.6 A1.5 1.5 0 0 0 8.5 20 H15.5 A1.5 1.5 0 0 0 17 18.6 L18 6.5")
            .Line(10, 10.5f, 10, 16.5f).Line(14, 10.5f, 14, 16.5f),
        new Icon("Edit").Path("M4.5 19.5 L5.3 15.9 L15.6 5.6 A1.9 1.9 0 0 1 18.4 8.4 L8.1 18.7 Z").Line(13.8f, 7.4f, 16.6f, 10.2f),
        new Icon("Refresh").Arc(12, 12, 7.5f, -30, 300).Lines(10, 2.2f, 12.3f, 4.5f, 10, 6.8f),
        new Icon("Info").Circle(12, 12, 9).Line(12, 11, 12, 16.5f).Dot(12, 7.8f, 1.3f),
        new Icon("Export").Path("M4 14 V18.5 A1.5 1.5 0 0 0 5.5 20 H18.5 A1.5 1.5 0 0 0 20 18.5 V14")
            .Line(12, 15, 12, 4).Lines(7.5f, 8.5f, 12, 4, 16.5f, 8.5f),
        new Icon("Equalizer").Box(5, 11, 8, 20, 1).Box(10.5f, 5, 13.5f, 20, 1).Box(16, 14, 19, 20, 1),
    ];

    private const string Speaker = "M4 9.5 H7 L11 6 V18 L7 14.5 H4 Z";

    private const string Heart =
        "M12 19.3 C12 19.3 4 14.6 4 9.1 C4 6.8 5.8 5 8.1 5 C9.7 5 11.1 5.9 12 7.2 " +
        "C12.9 5.9 14.3 5 15.9 5 C18.2 5 20 6.8 20 9.1 C20 14.6 12 19.3 12 19.3 Z";

    private static Icon Repeat(string name) => new Icon(name)
        .Path("M5 11 V9.5 A2.5 2.5 0 0 1 7.5 7 H19").Lines(16.5f, 4.5f, 19, 7, 16.5f, 9.5f)
        .Path("M19 13 V14.5 A2.5 2.5 0 0 1 16.5 17 H5").Lines(7.5f, 14.5f, 5, 17, 7.5f, 19.5f);

    /// <summary>The outline of a gear with eight teeth.</summary>
    private static string Gear()
    {
        var path = new StringBuilder();
        for (int tooth = 0; tooth < 8; tooth++)
        {
            double centre = tooth * 45;
            foreach (var (angle, radius) in new[] { (centre - 15, 6.8), (centre - 8, 9.0), (centre + 8, 9.0), (centre + 15, 6.8) })
            {
                double radians = angle * Math.PI / 180;
                path.Append(path.Length == 0 ? "M" : " L").Append(' ')
                    .Append((12 + radius * Math.Cos(radians)).ToString("0.###", CultureInfo.InvariantCulture)).Append(' ')
                    .Append((12 + radius * Math.Sin(radians)).ToString("0.###", CultureInfo.InvariantCulture));
            }
        }
        return path.Append(" Z").ToString();
    }
}
