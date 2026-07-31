using Avalonia.Controls;
using Avalonia.Input;
using Launcher.Desktop.ViewModels;

namespace Launcher.Desktop.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    /// <summary>Clicking the dimmed area around the settings sheet closes it.
    ///
    /// The guard is <c>e.Source</c>: pointer events bubble, so a press anywhere inside the sheet
    /// arrives here too, and acting on every one of them would close the sheet the moment somebody
    /// clicked a radio button. Only a press whose source IS the backdrop is a click on the
    /// backdrop.
    ///
    /// Routed through <see cref="SettingsViewModel.CancelCommand"/> rather than setting IsOpen, for
    /// two reasons. Cancel reverts the edits, which is what clicking away from a dialog means
    /// everywhere else. And the command already refuses while a relocation is in flight, so the one
    /// case where dismissing the sheet would be destructive is covered without restating the rule
    /// here — <c>CanExecute</c> false means Execute does nothing.
    ///
    /// Deliberately not wired to the first-run sheet. That one asks the question that gates every
    /// notification, and dismissing it by missing the panel would answer it by accident.</summary>
    private void OnSettingsBackdropPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, SettingsBackdrop))
            return;

        if (DataContext is MainWindowViewModel { Settings: { } settings }
            && settings.CancelCommand.CanExecute(null))
            settings.CancelCommand.Execute(null);

        e.Handled = true;
    }
}
