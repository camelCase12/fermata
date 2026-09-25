using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Fermata.ViewModels.Pages;

namespace Fermata.Views.Pages;

public partial class PlaylistView : UserControl
{
    public PlaylistView()
    {
        InitializeComponent();
        ExportButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage
                || DataContext is not PlaylistViewModel { Playlist: { } playlist } model)
            {
                return;
            }
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export playlist",
                SuggestedFileName = playlist.Name + ".m3u8",
                DefaultExtension = "m3u8",
                FileTypeChoices = [M3uFiles],
                ShowOverwritePrompt = true,
            });
            if (file?.TryGetLocalPath() is { } path)
                model.Export(path);
        };
    }

    internal static readonly FilePickerFileType M3uFiles = new("M3U playlists")
    {
        Patterns = ["*.m3u8", "*.m3u"],
        MimeTypes = ["audio/x-mpegurl", "application/vnd.apple.mpegurl"],
    };
}
