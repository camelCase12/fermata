using CommunityToolkit.Mvvm.ComponentModel;

namespace Fermata.ViewModels;

/// <summary>The page history, with back and forward.</summary>
public sealed partial class Navigator : ObservableObject
{
    private const int HistoryLimit = 50;
    private readonly List<PageViewModel> back = [];
    private readonly List<PageViewModel> forward = [];

    [ObservableProperty]
    public partial PageViewModel? Current { get; private set; }

    public bool CanGoBack => back.Count > 0;
    public bool CanGoForward => forward.Count > 0;

    public void Navigate(PageViewModel page)
    {
        if (page == Current)
        {
            page.Activate();
            return;
        }
        if (Current is { } previous)
        {
            previous.Deactivate();
            back.Add(previous);
            if (back.Count > HistoryLimit)
                back.RemoveAt(0);
        }
        forward.Clear();
        Show(page);
    }

    public void GoBack()
    {
        if (back.Count == 0)
            return;
        var page = back[^1];
        back.RemoveAt(back.Count - 1);
        if (Current is { } current)
        {
            current.Deactivate();
            forward.Add(current);
        }
        Show(page);
    }

    public void GoForward()
    {
        if (forward.Count == 0)
            return;
        var page = forward[^1];
        forward.RemoveAt(forward.Count - 1);
        if (Current is { } current)
        {
            current.Deactivate();
            back.Add(current);
        }
        Show(page);
    }

    /// <summary>Removes the pages that match <paramref name="predicate"/> from the history.</summary>
    public void Forget(Func<PageViewModel, bool> predicate)
    {
        back.RemoveAll(p => predicate(p));
        forward.RemoveAll(p => predicate(p));
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }

    private void Show(PageViewModel page)
    {
        Current = page;
        page.Activate();
        OnPropertyChanged(nameof(CanGoBack));
        OnPropertyChanged(nameof(CanGoForward));
    }
}
