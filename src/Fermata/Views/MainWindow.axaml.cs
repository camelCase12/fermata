using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Fermata.Controls;
using Fermata.Library;
using Fermata.ViewModels;

namespace Fermata.Views;

public partial class MainWindow : Window
{
    private ListBoxItem? dropTarget;
    private Key? shortcutKey;

    public MainWindow()
    {
        InitializeComponent();
        // Tunnelling, so shortcuts work whatever has focus (except while typing).
        AddHandler(KeyDownEvent, OnShortcut, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnShortcutReleased, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnAnyPointerPressed, RoutingStrategies.Tunnel);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
        DragDrop.SetAllowDrop(this, true);
        DragDrop.SetAllowDrop(PlaylistList, true);
        PlaylistList.AddHandler(DragDrop.DragEnterEvent, OnPlaylistDragOver);
        PlaylistList.AddHandler(DragDrop.DragOverEvent, OnPlaylistDragOver);
        PlaylistList.AddHandler(DragDrop.DragLeaveEvent, OnPlaylistDragLeave);
        PlaylistList.AddHandler(DragDrop.DropEvent, OnPlaylistDrop);
        SearchBox.KeyDown += OnSearchKey;
    }

    private MainViewModel Model => (MainViewModel)DataContext!;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        var settings = Model.Shell.Services.Settings;
        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);
        if (settings.WindowMaximized)
            WindowState = WindowState.Maximized;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Nothing needs to update on screen while the window is minimized.
        if (change.Property == WindowStateProperty && DataContext is MainViewModel model)
            model.Player.SetWindowVisible(WindowState != WindowState.Minimized);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        var settings = Model.Shell.Services.Settings;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            settings.WindowWidth = Bounds.Width;
            settings.WindowHeight = Bounds.Height;
        }
        Model.Shell.Services.SaveSettings();
    }

    /// <summary>Brings the window forward (another launch, or MPRIS Raise).</summary>
    public void Raise()
    {
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Model.SubmitSearch();
                e.Handled = true;
                break;
            case Key.Escape:
                Model.SearchText = "";
                LeaveSearchBox();
                e.Handled = true;
                break;
        }
    }

    private void OnShortcut(object? sender, KeyEventArgs e)
    {
        var model = Model;
        var player = model.Player;
        var shell = model.Shell;
        bool typing = e.Source is TextBox || FocusManager?.GetFocusedElement() is TextBox;
        var modifiers = e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Alt | KeyModifiers.Meta);
        bool plain = modifiers == KeyModifiers.None;

        // Dialogs take Escape and Enter for themselves.
        if (shell.Dialog is not null)
        {
            if (e.Key == Key.Escape)
            {
                CancelDialog(shell.Dialog);
                e.Handled = true;
            }
            return;
        }

        bool handled = true;
        switch (e.Key)
        {
            case Key.MediaPlayPause:
                player.PlayPauseCommand.Execute(null);
                break;
            case Key.MediaNextTrack:
                player.NextCommand.Execute(null);
                break;
            case Key.MediaPreviousTrack:
                player.PreviousCommand.Execute(null);
                break;
            case Key.MediaStop:
                shell.Player.Pause();
                break;
            case Key.Q when modifiers == KeyModifiers.Control:
                Close();
                break;
            case Key.F when modifiers == KeyModifiers.Control:
            case Key.OemQuestion or Key.Divide when plain && !typing:
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.Left when modifiers == KeyModifiers.Alt:
                Navigate(shell.Navigator.GoBack);
                break;
            case Key.Right when modifiers == KeyModifiers.Alt:
                Navigate(shell.Navigator.GoForward);
                break;
            case Key.Right when modifiers == KeyModifiers.Control && !typing:
            case Key.N when modifiers == KeyModifiers.Shift && !typing:
                player.NextCommand.Execute(null);
                break;
            case Key.Left when modifiers == KeyModifiers.Control && !typing:
            case Key.P when modifiers == KeyModifiers.Shift && !typing:
                player.PreviousCommand.Execute(null);
                break;
            case Key.D1 or Key.NumPad1 when modifiers == KeyModifiers.Control:
                Navigate(shell.GoHome);
                break;
            case Key.D2 or Key.NumPad2 when modifiers == KeyModifiers.Control:
                Navigate(shell.GoExplore);
                break;
            case Key.D3 or Key.NumPad3 when modifiers == KeyModifiers.Control:
                Navigate(shell.GoSongs);
                break;
            case Key.D4 or Key.NumPad4 when modifiers == KeyModifiers.Control:
                Navigate(shell.GoAlbums);
                break;
            case Key.D5 or Key.NumPad5 when modifiers == KeyModifiers.Control:
                Navigate(shell.GoArtists);
                break;
            case Key.D6 or Key.NumPad6 when modifiers == KeyModifiers.Control:
                Navigate(shell.GoLiked);
                break;
            case Key.D7 or Key.NumPad7 when modifiers == KeyModifiers.Control:
                Navigate(shell.GoHistory);
                break;
            case Key.OemComma when modifiers == KeyModifiers.Control:
                Navigate(shell.GoSettings);
                break;
            case Key.Up when modifiers == KeyModifiers.Control:
                player.ChangeVolume(0.05);
                break;
            case Key.Down when modifiers == KeyModifiers.Control:
                player.ChangeVolume(-0.05);
                break;
            case Key.Escape when shell.IsNowPlayingOpen:
                shell.IsNowPlayingOpen = false;
                break;
            default:
                handled = !typing && plain && HandlePlainKey(e.Key, model);
                break;
        }
        if (handled)
        {
            e.Handled = true;
            shortcutKey = e.Key;
        }
    }

    /// <summary>
    /// A key used as a shortcut is not also delivered on release: a focused button (say Next, after
    /// being clicked) would otherwise act on Space as well.
    /// </summary>
    private void OnShortcutReleased(object? sender, KeyEventArgs e)
    {
        if (e.Key != shortcutKey)
            return;
        shortcutKey = null;
        e.Handled = true;
    }

    /// <summary>Keyboard navigation leaves the search box, so single-key shortcuts work on the new page.</summary>
    private void Navigate(Action navigate)
    {
        LeaveSearchBox();
        navigate();
    }

    /// <summary>Moves keyboard focus from the search box to the page, so single-key shortcuts work again.</summary>
    private void LeaveSearchBox()
    {
        if (SearchBox.IsKeyboardFocusWithin)
            PageHost.Focus();
    }

    /// <summary>Single-key shortcuts, active whenever no text field has focus.</summary>
    private static bool HandlePlainKey(Key key, MainViewModel model)
    {
        var player = model.Player;
        switch (key)
        {
            case Key.Space or Key.K:
                player.PlayPauseCommand.Execute(null);
                return true;
            case Key.J:
                player.SeekBy(TimeSpan.FromSeconds(-10));
                return true;
            case Key.L:
                player.SeekBy(TimeSpan.FromSeconds(10));
                return true;
            case Key.Left:
                player.SeekBy(TimeSpan.FromSeconds(-5));
                return true;
            case Key.Right:
                player.SeekBy(TimeSpan.FromSeconds(5));
                return true;
            case Key.M:
                player.ToggleMuteCommand.Execute(null);
                return true;
            case Key.S:
                player.ToggleShuffleCommand.Execute(null);
                return true;
            case Key.R:
                player.CycleRepeatCommand.Execute(null);
                return true;
            case Key.F:
                player.ToggleLikeCommand.Execute(null);
                return true;
            case Key.Q:
                player.ToggleNowPlayingCommand.Execute(null);
                return true;
            default:
                return false;
        }
    }

    private static void CancelDialog(DialogViewModel dialog)
    {
        switch (dialog)
        {
            case PromptDialog prompt:
                prompt.CancelCommand.Execute(null);
                break;
            case ConfirmDialog confirm:
                confirm.CancelCommand.Execute(null);
                break;
            case TrackInfoDialog info:
                info.CloseCommand.Execute(null);
                break;
        }
    }

    /// <summary>
    /// The back and forward buttons of a mouse navigate pages. Clicking outside a text field leaves it,
    /// as on the web, so single-key shortcuts work again.
    /// </summary>
    private void OnAnyPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (FocusManager?.GetFocusedElement() is TextBox field && Model.Shell.Dialog is null
            && e.Source is Visual target && target != field && !field.IsVisualAncestorOf(target))
        {
            PageHost.Focus();
        }
        var properties = e.GetCurrentPoint(this).Properties;
        if (properties.IsXButton1Pressed)
        {
            Model.Shell.Navigator.GoBack();
            e.Handled = true;
        }
        else if (properties.IsXButton2Pressed)
        {
            Model.Shell.Navigator.GoForward();
            e.Handled = true;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;

    /// <summary>
    /// Songs dragged from a list onto a playlist in the sidebar are added to it. Drag events come from
    /// whichever part of a row is under the pointer, so the row is found from the pointer's position.
    /// </summary>
    private void OnPlaylistDragOver(object? sender, DragEventArgs e)
    {
        var item = PlaylistDropTarget(e);
        MarkDropTarget(item);
        e.DragEffects = item is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Moving between the parts of a row also raises this; only leaving the list clears the highlight.</summary>
    private void OnPlaylistDragLeave(object? sender, DragEventArgs e)
    {
        if (!new Rect(PlaylistList.Bounds.Size).Contains(e.GetPosition(PlaylistList)))
            MarkDropTarget(null);
    }

    private void OnPlaylistDrop(object? sender, DragEventArgs e)
    {
        MarkDropTarget(null);
        if (PlaylistDropTarget(e)?.DataContext is Playlist playlist && e.DataTransfer.TryGetValue(RowDragging.Format) is { } songs)
        {
            Model.Shell.AddToPlaylist(playlist, songs.Tracks);
            e.Handled = true;
        }
    }

    private ListBoxItem? PlaylistDropTarget(DragEventArgs e) =>
        e.DataTransfer.Contains(RowDragging.Format) ? RowDragging.ContainerAt(PlaylistList, e.GetPosition(PlaylistList)) : null;

    private void MarkDropTarget(ListBoxItem? item)
    {
        if (item == dropTarget)
            return;
        dropTarget?.Classes.Remove("drop");
        dropTarget = item;
        dropTarget?.Classes.Add("drop");
    }

    /// <summary>Dropping files or folders from a file manager plays them.</summary>
    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is null)
            return;
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            App.OpenFiles(Model.Shell.Services, Model.Shell, paths);
    }
}
