using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Launcher.Desktop.ViewModels;

namespace Launcher.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // The folder dialog needs the TopLevel that owns this control, which only the view can reach —
        // so the view hands the picker to the view model rather than the view model importing storage.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsViewModel vm)
                vm.PickFolderAsync = PickFolderAsync;
        };
    }

    private async Task<string?> PickFolderAsync(string? startAt)
    {
        if (TopLevel.GetTopLevel(this) is not { } top)
            return null;

        var options = new FolderPickerOpenOptions
        {
            Title = "Choose where Vortex Arena installs",
            AllowMultiple = false,
        };
        if (!string.IsNullOrWhiteSpace(startAt) && Directory.Exists(startAt))
            options.SuggestedStartLocation = await top.StorageProvider.TryGetFolderFromPathAsync(startAt);

        var picked = await top.StorageProvider.OpenFolderPickerAsync(options);
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }
}
