using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Fermata.Library;
using Fermata.ViewModels;
using Fermata.ViewModels.Pages;

namespace Fermata.Views.Pages;

public partial class SearchView : UserControl
{
    private SearchViewModel? model;

    public SearchView()
    {
        InitializeComponent();
        TopCard.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
                model?.OpenTopResultCommand.Execute(null);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (model is not null)
            model.PropertyChanged -= OnModelChanged;
        model = DataContext as SearchViewModel;
        if (model is not null)
            model.PropertyChanged += OnModelChanged;
        ShowTopResult();
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchViewModel.TopResult))
            ShowTopResult();
    }

    /// <summary>Shows the top result, which can be a song, album or artist.</summary>
    private void ShowTopResult()
    {
        var library = App.Shell?.Library;
        TopArt.IsRound = false;
        switch (model?.TopResult)
        {
            case Track track:
                TopArt.Source = library?.ArtOf(track);
                TopTitle.Text = track.Title;
                TopSubtitle.Text = $"Song · {track.DisplayArtist}";
                break;
            case Album album:
                TopArt.Source = album.Art;
                TopTitle.Text = album.Title;
                TopSubtitle.Text = $"Album · {album.Artist}";
                break;
            case Artist artist:
                TopArt.Source = artist.Art;
                TopArt.IsRound = true;
                TopTitle.Text = artist.Name;
                TopSubtitle.Text = $"Artist · {Formats.Count(artist.Tracks.Count, "song")}";
                break;
        }
    }
}
