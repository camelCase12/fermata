using System.Runtime.CompilerServices;
using Avalonia.Controls;
using NWayland.Protocols.Wayland;
using NWayland.Protocols.XdgActivationV1;
using NWayland.Protocols.XdgShell;

namespace Fermata.Services;

/// <summary>The Wayland requests for a window that Avalonia 12.1 does not make: the app ID and activation.</summary>
/// <remarks>
/// <para>
/// Avalonia's Wayland backend sends no <c>xdg_toplevel.set_app_id</c>, so desktops could not match the
/// window to its launcher entry, name and icon. Its <c>Window.Activate</c> does nothing, so a second launch
/// could not bring the window forward. This class makes both requests on the backend's own protocol
/// objects. It reaches them through members that are internal to Avalonia.Wayland 12.1.3, with
/// <see cref="UnsafeAccessorAttribute"/>, which the native build resolves at compile time. If an update
/// renames them, the requests are skipped and one line on standard error says why.
/// </para>
/// <para>
/// The protocol objects belong to the backend's Wayland thread, so every request is posted to that thread.
/// An exception there would end the thread, so none escapes.
/// </para>
/// </remarks>
internal static class WaylandWindow
{
    private const string WindowImpl = "Avalonia.Wayland.WindowImpl, Avalonia.Wayland";
    private const string WindowBaseImpl = "Avalonia.Wayland.WindowBaseImpl, Avalonia.Wayland";
    private const string WorkerClient = "Avalonia.Wayland.Server.WaylandWorkerClient, Avalonia.Wayland";
    private const string ShellSurfaceProxy = "Avalonia.Wayland.Server.Persistent.WXdgShellSurfaceProxy, Avalonia.Wayland";
    private const string ShellSurface = "Avalonia.Wayland.Server.Persistent.IWXdgShellSurface, Avalonia.Wayland";
    private const string Surface = "Avalonia.Wayland.Server.Persistent.WSurface, Avalonia.Wayland";
    private const string Toplevel = "Avalonia.Wayland.Server.Persistent.WXdgTopLevel, Avalonia.Wayland";
    private const string Globals = "Avalonia.Wayland.Server.Transient.WaylandGlobals, Avalonia.Wayland";

    private static int reported;

    /// <summary>Whether the window is shown by Avalonia's Wayland backend, rather than on X11 or XWayland.</summary>
    public static bool IsWayland(TopLevel window) => window.PlatformImpl?.GetType().FullName == "Avalonia.Wayland.WindowImpl";

    /// <summary>Sets the app ID, the name of the launcher entry that desktops take the window's name and icon from.</summary>
    /// <remarks>Call it before the window is shown, so the compositor has the ID before the window appears.</remarks>
    public static void SetAppId(Window window, string appId) =>
        Post(window, "set the app ID", surface => GetXdgToplevel(surface)?.SetAppId(appId));

    /// <summary>Asks the compositor to focus the window.</summary>
    /// <param name="window">The window.</param>
    /// <param name="token">
    /// The activation token that the launcher of this request set, or null to ask the compositor for one.
    /// A requested token carries no input event to vouch for it, so the compositor may only mark the
    /// window as wanting attention.
    /// </param>
    public static void Activate(Window window, string? token) =>
        Post(window, "activate the window", surface =>
        {
            if (GetWlSurface(surface) is not { } wlSurface || GetGlobals(surface) is not { } globals
                || !GetKnownGlobals(globals).TryGetValue("xdg_activation_v1", out var entry))
            {
                return;
            }
            var activation = XdgActivationV1.Bind(GetRegistry(globals), entry.Name, 1);
            if (token is not null)
            {
                activation.Activate(token, wlSurface);
                activation.Destroy();
                return;
            }
            var request = activation.GetActivationToken(new XdgActivationTokenV1.Listener.Relay
            {
                OnDone = (sender, issued) => Run("activate the window", () =>
                {
                    sender.Destroy();
                    // The window may have closed, or been recreated for a new connection, meanwhile.
                    if (GetWlSurface(surface) == wlSurface)
                        activation.Activate(issued, wlSurface);
                    activation.Destroy();
                }),
            });
            request.SetSurface(wlSurface);
            request.Commit();
        });

    /// <summary>Runs a request on the Wayland thread with the backend's surface object for the window.</summary>
    private static void Post(Window window, string what, Action<object> request)
    {
        // The accessors do not check the types of the objects they are given, so this must.
        if (!IsWayland(window))
            return;
        try
        {
            var impl = window.PlatformImpl!;
            if (GetSurfaceProxy(impl) is not { } proxy)
                return;
            object surface = GetProxyTarget(proxy);
            if (surface.GetType().FullName != "Avalonia.Wayland.Server.Persistent.WXdgTopLevel")
                return;
            PostOob(GetClient(impl), () => Run(what, () => request(surface)));
        }
        catch (Exception error)
        {
            Report(what, error);
        }
    }

    private static void Run(string what, Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            Report(what, error);
        }
    }

    private static void Report(string what, Exception error)
    {
        if (Interlocked.Exchange(ref reported, 1) == 0)
            Console.Error.WriteLine($"fermata: could not {what} on Wayland: {error.GetType().Name}: {error.Message}");
    }

    // UI thread: the window's platform object, its surface proxy and the Wayland thread's queue.

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_SurfaceProxy")]
    [return: UnsafeAccessorType(ShellSurfaceProxy)]
    private static extern object? GetSurfaceProxy([UnsafeAccessorType(WindowImpl)] object window);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_ProxyTarget")]
    [return: UnsafeAccessorType(ShellSurface)]
    private static extern object GetProxyTarget([UnsafeAccessorType(ShellSurfaceProxy)] object proxy);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Client")]
    [return: UnsafeAccessorType(WorkerClient)]
    private static extern object GetClient([UnsafeAccessorType(WindowBaseImpl)] object window);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "PostOob")]
    private static extern void PostOob([UnsafeAccessorType(WorkerClient)] object client, Action callback);

    // Wayland thread: the surface's protocol objects and the compositor's globals.

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_xdgTopLevel")]
    private static extern ref XdgToplevel? GetXdgToplevel([UnsafeAccessorType(Toplevel)] object surface);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_WlSurface")]
    private static extern WlSurface? GetWlSurface([UnsafeAccessorType(Surface)] object surface);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Globals")]
    [return: UnsafeAccessorType(Globals)]
    private static extern object? GetGlobals([UnsafeAccessorType(Surface)] object surface);

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Registry")]
    private static extern WlRegistry GetRegistry([UnsafeAccessorType(Globals)] object globals);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_knownGlobals")]
    private static extern ref Dictionary<string, (uint Name, uint Version)> GetKnownGlobals([UnsafeAccessorType(Globals)] object globals);
}
