using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Fermata.ViewModels;

namespace Fermata.Views;

public partial class PromptDialogView : UserControl
{
    public PromptDialogView()
    {
        InitializeComponent();
        Input.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is PromptDialog dialog && dialog.ConfirmCommand.CanExecute(null))
            {
                dialog.ConfirmCommand.Execute(null);
                e.Handled = true;
            }
        };
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        Input.Focus();
        Input.SelectAll();
    }
}
