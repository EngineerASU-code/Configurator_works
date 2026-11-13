using Configurator.Core.Workspace;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class WorkspaceSwitchPolicyTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void Offline_package_is_saved_only_when_dirty(bool projectDirty, bool gridDirty, bool expected)
        => Assert.Equal(expected, WorkspaceSwitchPolicy.ShouldPersistOfflineProject(projectDirty, gridDirty));

    [Fact]
    public void Resolve_online_database_preserves_selected_database_case_insensitively()
        => Assert.Equal("EK_Omsk_Juice_del", WorkspaceSwitchPolicy.ResolveOnlineDatabase("ek_omsk_juice_del", new[] { "EK_Omsk_Juice_del", "Other" }));

    [Fact]
    public void Resolve_online_database_falls_back_when_selection_missing()
        => Assert.Equal("First", WorkspaceSwitchPolicy.ResolveOnlineDatabase("", new[] { "First", "Second" }));

    [Fact]
    public void Resolve_online_database_falls_back_when_selection_is_not_available()
        => Assert.Equal("First", WorkspaceSwitchPolicy.ResolveOnlineDatabase("Stale", new[] { "First", "Second" }));

    [Fact]
    public void Resolve_online_database_returns_empty_when_no_database_is_available()
        => Assert.Equal(string.Empty, WorkspaceSwitchPolicy.ResolveOnlineDatabase("Stale", Array.Empty<string>()));

    [Theory]
    [InlineData(true, true, 2, true)]
    [InlineData(true, true, 3, true)]
    [InlineData(false, true, 3, false)]
    [InlineData(true, false, 3, false)]
    [InlineData(true, true, 1, false)]
    [InlineData(true, true, 4, false)]
    public void Offline_workspace_transition_explicitly_rebinds_only_supported_active_editors(
        bool isOfflineWorkspace, bool hasProject, int mode, bool expected)
    {
        Assert.Equal(expected, WorkspaceSwitchPolicy.ShouldConfigureOfflineEditor(isOfflineWorkspace, hasProject, mode));
    }
}
