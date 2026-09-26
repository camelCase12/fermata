using CommunityToolkit.Mvvm.ComponentModel;
using Fermata.Library;

namespace Fermata.ViewModels;

/// <summary>A section of the sidebar.</summary>
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

/// <summary>A page shown in the main area.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(Shell shell)
    {
        Shell = shell;
    }

    protected Shell Shell { get; }

    public virtual Section Section => Section.None;

    /// <summary>The covers the page's ambient backdrop is made from, or none.</summary>
    [ObservableProperty] public partial IReadOnlyList<ArtSource> Backdrop { get; protected set; } = [];

    /// <summary>Library version this page last built its content from.</summary>
    private long builtFor = -1;

    /// <summary>Prepares the page to be shown.</summary>
    /// <remarks>The content is rebuilt if the library changed since it was built.</remarks>
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

    /// <summary>Rebuilds the page after the library changed while it is shown.</summary>
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
