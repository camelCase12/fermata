using CommunityToolkit.Mvvm.ComponentModel;
using Fermata.Library;

namespace Fermata.ViewModels;

/// <summary>Sidebar destinations; pages report which one they belong to so it can be highlighted.</summary>
public enum Section
{
    None,
    Home,
    Explore,
    Songs,
    Albums,
    Artists,
    Liked,
    History,
    Playlist,
    Settings,
}

/// <summary>A page shown in the main area. Pages refresh themselves when the library changes.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(Shell shell)
    {
        Shell = shell;
    }

    protected Shell Shell { get; }

    public virtual Section Section => Section.None;

    /// <summary>Covers the page's ambient backdrop is made from: empty for pages without art of their own.</summary>
    [ObservableProperty] public partial IReadOnlyList<ArtSource> Backdrop { get; protected set; } = [];

    /// <summary>Library version this page last built its content from.</summary>
    private long builtFor = -1;

    /// <summary>Called when the page becomes visible; rebuilds content if the library changed meanwhile.</summary>
    public void Activate()
    {
        long version = Shell.Services.Library.Snapshot.Version;
        if (version != builtFor)
        {
            builtFor = version;
            Refresh();
        }
        OnActivated();
    }

    /// <summary>The library changed while the page is visible.</summary>
    public void LibraryChanged()
    {
        builtFor = Shell.Services.Library.Snapshot.Version;
        Refresh();
    }

    /// <summary>Builds or rebuilds the page's content from the current library snapshot.</summary>
    protected abstract void Refresh();

    protected virtual void OnActivated()
    {
    }

    public virtual void Deactivate()
    {
    }
}
