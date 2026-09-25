using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Fermata.Library;
using Fermata.ViewModels;

namespace Fermata.Views;

/// <summary>
/// One song in a list. Rows are recycled as the list scrolls; each keeps its "playing" and "liked"
/// classes current by listening only while it is on screen.
/// </summary>
public partial class TrackRow : UserControl
{
    private Track? track;
    private TrackList? owner;
    private bool subscribed;
    private TrackListStyle layout = (TrackListStyle)(-1);

    public TrackRow()
    {
        InitializeComponent();
        LikeButton.Click += (_, _) => Act(shell => shell.ToggleLike(track!));
        MoreButton.Click += (_, _) => OpenMenu(MoreButton);
        ArtistLink.Click += (_, _) => Act(shell => shell.OpenArtistOf(track!));
        AlbumLink.Click += (_, _) => Act(shell => shell.OpenAlbumOf(track!));
        PlayOverlay.PointerPressed += PlayPressed;
        NumberPlay.PointerPressed += PlayPressed;
        ContextRequested += (_, e) =>
        {
            OpenMenu(this);
            e.Handled = true;
        };
    }

    private void Act(Action<Shell> action)
    {
        if (track is not null && App.Shell is { } shell)
            action(shell);
    }

    private void PlayPressed(object? sender, PointerPressedEventArgs e)
    {
        if (track is not null && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            owner?.PlayRow(this);
            e.Handled = true;
        }
    }

    private void OpenMenu(Control anchor)
    {
        if (track is null || App.Shell is not { } shell)
            return;
        var tracks = owner?.TracksForMenu(this) ?? [track];
        var menu = TrackMenu.Build(shell, tracks, owner?.RemoveFromPlaylistAction(this));
        menu.Placement = anchor == this ? PlacementMode.Pointer : PlacementMode.BottomEdgeAlignedRight;
        menu.Open(anchor);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        track = DataContext as Track;
        if (track is null)
            return;
        Art.Source = App.Shell?.Library.ArtOf(track);
        Art.PlaceholderKey = track.AlbumKey ?? track.Title;
        Number.Text = track.TrackNumber > 0 ? track.TrackNumber.ToString() : "–";
        // Without an artist column (album and artist pages), the artist is shown per song only where it differs.
        Classes.Set("guest", track.AlbumArtist.Length > 0 && !string.Equals(track.Credit, track.AlbumArtist, StringComparison.OrdinalIgnoreCase));
        UpdateLiked();
        UpdatePlaying();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        owner = this.FindAncestorOfType<TrackList>();
        ApplyLayout(owner?.Model?.Style ?? TrackListStyle.Library);
        if (App.Services is { } services && !subscribed)
        {
            services.Player.TrackChanged += UpdatePlaying;
            services.UserData.TrackChanged += OnTrackDataChanged;
            subscribed = true;
        }
        UpdateLiked();
        UpdatePlaying();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (App.Services is { } services && subscribed)
        {
            services.Player.TrackChanged -= UpdatePlaying;
            services.UserData.TrackChanged -= OnTrackDataChanged;
            subscribed = false;
        }
    }

    /// <summary>Column widths per list style; which cells show is decided by the style sheet.</summary>
    private void ApplyLayout(TrackListStyle style)
    {
        if (style == layout)
            return;
        layout = style;
        Classes.Set("library", style == TrackListStyle.Library);
        Classes.Set("album", style == TrackListStyle.Album);
        Classes.Set("artist", style == TrackListStyle.Artist);
        SetColumns(Columns.ColumnDefinitions, style);
    }

    /// <summary>Lays out a row, or the column titles above rows: leading cell, title, artist, album, like, duration, menu.</summary>
    public static void SetColumns(ColumnDefinitions columns, TrackListStyle style)
    {
        var column = new GridLength(3, GridUnitType.Star);
        columns[0].Width = new GridLength(style == TrackListStyle.Album ? 44 : 52);
        columns[2].Width = style == TrackListStyle.Library ? column : new GridLength(0);
        columns[3].Width = style != TrackListStyle.Album ? column : new GridLength(0);
    }

    private void OnTrackDataChanged(string path)
    {
        if (path == track?.Path)
            UpdateLiked();
    }

    private void UpdateLiked()
    {
        bool liked = track is not null && App.Shell is { } shell && shell.IsLiked(track);
        Classes.Set("liked", liked);
        ToolTip.SetTip(LikeButton, liked ? "Remove from liked songs" : "Like");
    }

    private void UpdatePlaying() =>
        Classes.Set("playing", track is not null && App.Services?.Player.CurrentTrack?.Path == track.Path);
}
