using Launcher.Core;
using Launcher.Desktop.ViewModels;
using Xunit;

namespace Launcher.Tests;

/// <summary>The settings sheet's navigation: five tabs, and a search that cuts across them.
///
/// Worth testing rather than eyeballing because the two interact. Every row answers "am I visible"
/// from either the active tab or the query depending on which mode the sheet is in, and the failure
/// when that goes wrong is a setting that cannot be reached at all — invisible on its own tab, or
/// invisible to the search that should have found it.</summary>
public class SettingsSheetTests
{
    private static SettingsViewModel Sheet()
    {
        // A store pointed at a scratch directory. Nothing here saves, but the view model loads its
        // starting state through one and must not read the developer's real settings.json.
        var dir = Path.Combine(Path.GetTempPath(), "vortex-settings-" + Guid.NewGuid().ToString("N"));
        return new SettingsViewModel(new LauncherSettingsStore(dir), new LauncherSettings());
    }

    [Fact]
    public void Opens_on_the_first_tab_with_no_query()
    {
        var vm = Sheet();
        vm.Open(installedVersion: null);

        Assert.False(vm.Searching);
        Assert.True(vm.ShowChannel);
        Assert.False(vm.ShowGameUpdates);
        Assert.False(vm.ShowFolders);
    }

    [Fact]
    public void A_tab_shows_its_own_section_and_only_that_one()
    {
        var vm = Sheet();
        vm.ActiveTab = SettingsTabs.Notifications;

        Assert.True(vm.ShowNotifications);
        Assert.False(vm.ShowChannel);
        Assert.False(vm.ShowLauncherUpdates);

        // Not searching, so every row inside the visible section shows.
        Assert.True(vm.ShowReachRow);
        Assert.True(vm.ShowAutostartRow);
        Assert.True(vm.ShowIntervalRow);
    }

    /// <summary>The reason search exists: a player who does not know which tab a setting is filed
    /// under. "folder" is on the Folders tab, "how often" is on Notifications, and neither query
    /// should require knowing that.</summary>
    [Theory]
    [InlineData("folder")]
    [InlineData("disk")]
    [InlineData("where")]
    public void Searching_finds_the_install_folder_from_another_tab(string query)
    {
        var vm = Sheet();
        vm.ActiveTab = SettingsTabs.Channel; // deliberately the wrong tab
        vm.SearchText = query;

        Assert.True(vm.Searching);
        Assert.True(vm.ShowFolders);
        Assert.True(vm.ShowInstallRootRow);
    }

    [Theory]
    [InlineData("how often")]
    [InlineData("frequency")]
    [InlineData("interval")]
    public void Searching_finds_the_check_interval_by_what_a_player_would_call_it(string query)
    {
        var vm = Sheet();
        vm.SearchText = query;

        Assert.True(vm.ShowNotifications);
        Assert.True(vm.ShowIntervalRow);

        // And narrows within the section: the reach radios are not what was asked for.
        Assert.False(vm.ShowReachRow);
        Assert.False(vm.ShowAutostartRow);
    }

    /// <summary>A hit spanning two tabs is the case a TabControl could not have rendered, and the
    /// reason the sections are a flat list that hide themselves.</summary>
    [Fact]
    public void One_query_can_surface_rows_from_more_than_one_tab()
    {
        var vm = Sheet();
        vm.SearchText = "update";

        Assert.True(vm.ShowGameUpdates);
        Assert.True(vm.ShowLauncherUpdates);
        Assert.False(vm.NoResults);
    }

    /// <summary>Terms narrow rather than widen, or typing more would surface more.</summary>
    [Fact]
    public void Extra_terms_narrow_the_result()
    {
        var vm = Sheet();

        vm.SearchText = "game";
        Assert.True(vm.ShowGameUpdates);

        vm.SearchText = "game saves";
        Assert.False(vm.ShowGameUpdates);
        Assert.True(vm.ShowFolders);
        Assert.True(vm.ShowGameDataRow);
        Assert.False(vm.ShowInstallRootRow);
    }

    [Fact]
    public void A_query_matching_nothing_says_so_rather_than_showing_an_empty_sheet()
    {
        var vm = Sheet();
        vm.SearchText = "reticulating splines";

        Assert.True(vm.NoResults);
        Assert.False(vm.ShowChannel);
        Assert.False(vm.ShowFolders);
    }

    [Fact]
    public void Clearing_the_search_goes_back_to_the_active_tab()
    {
        var vm = Sheet();
        vm.ActiveTab = SettingsTabs.Folders;
        vm.SearchText = "channel";
        Assert.True(vm.ShowChannel);
        Assert.False(vm.ShowFolders);

        vm.ClearSearchCommand.Execute(null);

        Assert.False(vm.Searching);
        Assert.True(vm.ShowFolders);
        Assert.False(vm.ShowChannel);
    }

    /// <summary>Reopening must not inherit the last visit's query, which would look like most of
    /// the settings had gone missing.</summary>
    [Fact]
    public void Reopening_clears_the_query_and_returns_to_the_first_tab()
    {
        var vm = Sheet();
        vm.ActiveTab = SettingsTabs.Folders;
        vm.SearchText = "saves";

        vm.Open(installedVersion: null);

        Assert.Equal("", vm.SearchText);
        Assert.Equal(SettingsTabs.Channel, vm.ActiveTab);
        Assert.True(vm.ShowChannel);
    }

    [Fact]
    public void The_tab_radio_pairs_track_the_active_tab()
    {
        var vm = Sheet();

        vm.FoldersTab = true;

        Assert.Equal(SettingsTabs.Folders, vm.ActiveTab);
        Assert.True(vm.FoldersTab);
        Assert.False(vm.ChannelTab);
    }

    /// <summary>The game's user directory is computed, never stored, and has to be an absolute path
    /// naming the project — the Open button in the Folders tab is pointed straight at it.</summary>
    [Fact]
    public void The_game_data_path_is_absolute_and_names_the_project()
    {
        var vm = Sheet();

        Assert.True(Path.IsPathFullyQualified(vm.GameDataPath));
        Assert.EndsWith(GameUserData.ProjectName, vm.GameDataPath, StringComparison.Ordinal);
        Assert.Contains("app_userdata", vm.GameDataPath, StringComparison.Ordinal);
    }
}

/// <summary>Check and update used to be separate buttons, plus a third for the swap. They are one
/// button now, so the label is the only thing telling the player which of the three it will do.
///
/// Only the label is covered. MainWindowViewModel cannot be constructed here — its constructor
/// reads the real per-user settings file and starts the background update loop — so the dispatch
/// that follows the same precedence is checked by reading it, not by running it.</summary>
public class PrimaryActionLabelTests
{
    [Theory]
    [InlineData(false, false, "Check for Updates")] // nothing looked for yet
    [InlineData(false, true, "Update Now")]         // found, not fetched
    [InlineData(true, false, "Update Now")]         // downloaded and waiting to be swapped in
    [InlineData(true, true, "Update Now")]
    public void The_label_names_what_the_press_will_do(
        bool stagedReady, bool updateAvailable, string expected)
    {
        Assert.Equal(expected, MainWindowViewModel.LabelFor(stagedReady, updateAvailable));
    }
}
