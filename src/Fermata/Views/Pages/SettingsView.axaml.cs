using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Fermata.ViewModels.Pages;

namespace Fermata.Views.Pages;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        BrowseButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage || DataContext is not SettingsViewModel model)
                return;
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a music folder", AllowMultiple = true });
            foreach (var folder in folders)
            {
                if (folder.TryGetLocalPath() is { } path)
                    model.AddFolder(path);
            }
        };
        ImportButton.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage || DataContext is not SettingsViewModel model)
                return;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Import playlists",
                AllowMultiple = true,
                FileTypeFilter = [PlaylistView.M3uFiles],
            });
            var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0)
                model.ImportPlaylists(paths);
        };
        FolderBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is SettingsViewModel model)
            {
                model.AddFolder(model.NewFolder);
                e.Handled = true;
            }
        };
    }
}
