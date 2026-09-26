using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Fermata.ViewModels;
using Fermata.ViewModels.Pages;
using Fermata.Views;
using Fermata.Views.Pages;

namespace Fermata;

/// <summary>Chooses the view for a page or dialog.</summary>
/// <remarks>Page views are kept alive with their view models.</remarks>
public sealed class ViewLocator : IDataTemplate
{
    private readonly ConditionalWeakTable<PageViewModel, Control> pages = [];

    public Control? Build(object? data)
    {
        if (data is PageViewModel page)
            return pages.GetValue(page, CreatePage);
        return data switch
        {
            PromptDialog => new PromptDialogView(),
            ConfirmDialog => new ConfirmDialogView(),
            TrackInfoDialog => new TrackInfoDialogView(),
            _ => new TextBlock { Text = data?.GetType().Name },
        };
    }

    public bool Match(object? data) => data is PageViewModel or DialogViewModel;

    private static Control CreatePage(PageViewModel page) => page switch
    {
        HomeViewModel => new HomeView(),
        ExploreViewModel => new ExploreView(),
        SongsViewModel => new SongsView(),
        AlbumsViewModel => new AlbumsView(),
        ArtistsViewModel => new ArtistsView(),
        AlbumViewModel => new AlbumView(),
        ArtistViewModel => new ArtistView(),
        PlaylistViewModel => new PlaylistView(),
        GenreViewModel => new GenreView(),
        SearchViewModel => new SearchView(),
        HistoryViewModel => new HistoryView(),
        SettingsViewModel => new SettingsView(),
        _ => new TextBlock { Text = page.GetType().Name },
    };
}
