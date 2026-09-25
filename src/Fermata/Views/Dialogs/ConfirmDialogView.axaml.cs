using Avalonia.Controls;
using Avalonia.Interactivity;
using Fermata.ViewModels;

namespace Fermata.Views;

public partial class ConfirmDialogView : UserControl
{
    public ConfirmDialogView()
    {
        InitializeComponent();
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is ConfirmDialog { IsDestructive: true })
            ConfirmButton.Classes.Set("danger", true);
        ConfirmButton.Focus();
    }
}
