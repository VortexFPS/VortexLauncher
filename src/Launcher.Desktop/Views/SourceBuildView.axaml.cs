using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Launcher.Desktop.ViewModels;

namespace Launcher.Desktop.Views;

public partial class SourceBuildView : UserControl
{
    /// <summary>The view model currently subscribed to, so a second DataContextChanged does not leave
    /// the first subscription attached. Avalonia raises that event more than once over a control's
    /// life, and a duplicate handler means every log update scrolls twice — harmless until the view
    /// model is replaced, at which point the old one is kept alive by this view.</summary>
    private SourceBuildViewModel? _watching;

    public SourceBuildView()
    {
        InitializeComponent();

        DataContextChanged += (_, _) =>
        {
            if (_watching is not null)
                _watching.PropertyChanged -= OnViewModelChanged;

            _watching = DataContext as SourceBuildViewModel;

            if (_watching is null)
                return;

            _watching.PropertyChanged += OnViewModelChanged;
            // The clipboard hangs off the TopLevel, which only the view can reach — so the view hands
            // the view model a way to copy rather than the view model importing the window.
            _watching.CopyToClipboard = CopyAsync;
        };
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(SourceBuildViewModel.LogText))
            return;

        // Posted at Background priority rather than called straight through: the text that triggered
        // this has not been measured yet when the property changes, so scrolling now scrolls to where
        // the end was one update ago and the view sits permanently behind.
        Dispatcher.UIThread.Post(() => LogScroller.ScrollToEnd(), DispatcherPriority.Background);
    }

    private async Task CopyAsync(string text)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }
}
