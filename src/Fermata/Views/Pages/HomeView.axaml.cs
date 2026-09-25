using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Fermata.Library;
using Fermata.ViewModels.Pages;

namespace Fermata.Views.Pages;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        // Quick picks: clicking a song plays the picks from it.
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || e.Source is not Control source)
                return;
            var pick = source.FindAncestorOfType<Border>(includeSelf: true);
            while (pick is not null && !pick.Classes.Contains("pick"))
                pick = pick.FindAncestorOfType<Border>();
            if (pick?.DataContext is Track track && FindSection(pick) is { } section && DataContext is HomeViewModel model)
            {
                model.PlayQuickPick(section, track);
                e.Handled = true;
            }
        });
        AddHandler(ContextRequestedEvent, (_, e) =>
        {
            if (e.Source is Control source && source.FindAncestorOfType<Border>(includeSelf: true) is { DataContext: Track track } border
                && border.Classes.Contains("pick") && App.Shell is { } shell)
            {
                TrackMenu.Build(shell, [track]).Open(border);
                e.Handled = true;
            }
        });
    }

    private static HomeSectionViewModel? FindSection(Control control)
    {
        foreach (var ancestor in control.GetVisualAncestors())
        {
            if (ancestor is Control { DataContext: HomeSectionViewModel section })
                return section;
        }
        return null;
    }
}
