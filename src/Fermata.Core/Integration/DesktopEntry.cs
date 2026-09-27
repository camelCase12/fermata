namespace Fermata.Integration;

/// <summary>Fermata's launcher entry, which install.sh installs as <c>io.github.camelcase12.Fermata.desktop</c>.</summary>
public static class DesktopEntry
{
    /// <summary>The desktop file's name without <c>.desktop</c>.</summary>
    /// <remarks>
    /// Desktops find the launcher entry, and with it the name and icon, from this ID: it is the Wayland
    /// app ID of the window and the <c>DesktopEntry</c> property of MPRIS.
    /// </remarks>
    public const string Id = "io.github.camelcase12.Fermata";
}
