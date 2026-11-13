using Configurator;
using Xunit;

namespace Configurator.Security.Tests;

/// <summary>Matrix-style regression tests: a grant for one capability must not imply unrelated capabilities.</summary>
public sealed class PermissionMatrixSecurityTests
{
    public static IEnumerable<object[]> ModeShowAndViewPairs()
    {
        yield return new object[] { 1, Permission.Mode1Show, Permission.Mode1View };
        yield return new object[] { 2, Permission.Mode2Show, Permission.Mode2View };
        yield return new object[] { 3, Permission.Mode3Show, Permission.Mode3View };
        yield return new object[] { 4, Permission.Mode4Show, Permission.Mode4View };
    }

    [Theory]
    [MemberData(nameof(ModeShowAndViewPairs))]
    public void ShowPermissionAloneNeverGrantsModeEntry(int mode, Permission show, Permission view)
    {
        var user = User();
        var role = mode == 2 ? Role(show, Permission.Mode2GraphShow) : Role(show);
        Assert.True(PermissionPolicy.Has(user, role, show));
        Assert.False(PermissionPolicy.Has(user, role, view));
        Assert.True(PermissionPolicy.CanShowMode(user, role, mode));
        Assert.False(PermissionPolicy.CanViewMode(user, role, mode));
        Assert.False(PermissionPolicy.CanEnterMode(user, role, mode));
    }

    [Theory]
    [MemberData(nameof(ModeShowAndViewPairs))]
    public void ViewPermissionAloneNeverGrantsModeVisibilityOrEntry(int mode, Permission show, Permission view)
    {
        var user = User();
        var role = Role(view);
        Assert.True(PermissionPolicy.Has(user, role, view));
        Assert.False(PermissionPolicy.Has(user, role, show));
        Assert.False(PermissionPolicy.CanShowMode(user, role, mode));
        Assert.True(PermissionPolicy.CanViewMode(user, role, mode));
        Assert.False(PermissionPolicy.CanEnterMode(user, role, mode));
    }

    [Fact]
    public void DisabledAccountCannotUsePermissionsEvenWhenRoleGrantsThem()
    {
        var user = User();
        user.Enabled = false;
        var role = Role(Permission.Mode1Show, Permission.Mode1View, Permission.Mode1Add,
            Permission.Mode1Edit, Permission.Mode1Delete, Permission.FreeSql);

        Assert.False(PermissionPolicy.Has(user, role, Permission.Mode1Add));
        Assert.False(PermissionPolicy.Has(user, role, Permission.Mode1Edit));
        Assert.False(PermissionPolicy.Has(user, role, Permission.Mode1Delete));
        Assert.False(PermissionPolicy.Has(user, role, Permission.FreeSql));
        Assert.False(PermissionPolicy.CanEnterMode(user, role, 1));
    }

    [Fact]
    public void Mode1CrudPermissionsDoNotGrantEachOther()
    {
        AssertIndependent(Permission.Mode1Add, Permission.Mode1View, Permission.Mode1Edit, Permission.Mode1Delete);
        AssertIndependent(Permission.Mode1Edit, Permission.Mode1View, Permission.Mode1Add, Permission.Mode1Delete);
        AssertIndependent(Permission.Mode1Delete, Permission.Mode1View, Permission.Mode1Add, Permission.Mode1Edit);
    }

    [Fact]
    public void Mode2SubmodeVisibilityPermissionsAreIndependent()
    {
        var user = User();
        var role = Role(Permission.Mode2Show, Permission.Mode2View, Permission.Mode2GraphShow);
        Assert.True(PermissionPolicy.Has(user, role, Permission.Mode2GraphShow));
        Assert.False(PermissionPolicy.Has(user, role, Permission.Mode2RelationsShow));
        Assert.False(PermissionPolicy.Has(user, role, Permission.Mode2FieldsShow));
        Assert.False(PermissionPolicy.Has(user, role, Permission.Mode2Manage));
    }

    [Fact]
    public void Mode2ArchitectureRightsDoNotGrantMode1OrMode3Crud()
    {
        var user = User();
        var role = Role(Permission.Mode2Manage);
        foreach (var permission in new[] { Permission.Mode1Add, Permission.Mode1Edit, Permission.Mode1Delete,
                     Permission.AddData, Permission.EditData, Permission.DeleteData, Permission.Mode3Manage })
            Assert.False(PermissionPolicy.Has(user, role, permission));
    }

    [Fact]
    public void Mode4ManageDoesNotImplicitlyGrantViewOrVisibility()
    {
        var user = User();
        var role = Role(Permission.Mode4Manage);
        Assert.False(PermissionPolicy.CanShowMode(user, role, 4));
        Assert.False(PermissionPolicy.CanViewMode(user, role, 4));
        Assert.False(PermissionPolicy.CanEnterMode(user, role, 4));
    }

    [Fact]
    public void UnknownModeIsDeniedByAllModeEntryPolicies()
    {
        var user = User();
        var role = Role(Permission.Mode1Show, Permission.Mode1View, Permission.Mode2Show, Permission.Mode2View,
            Permission.Mode3Show, Permission.Mode3View, Permission.Mode4Show, Permission.Mode4View);
        Assert.False(PermissionPolicy.CanShowMode(user, role, 0));
        Assert.False(PermissionPolicy.CanViewMode(user, role, 0));
        Assert.False(PermissionPolicy.CanEnterMode(user, role, 0));
        Assert.False(PermissionPolicy.CanShowMode(user, role, -1));
        Assert.False(PermissionPolicy.CanEnterMode(user, role, 5));
    }

    [Fact]
    public void PermissionNamesAreCaseInsensitiveButUnknownPermissionNamesGrantNothing()
    {
        var user = User();
        var role = new RoleDefinition { Name = "Operator", Permissions = new() { "mode1add", "TotallyMadeUpPermission" } };
        Assert.True(PermissionPolicy.Has(user, role, Permission.Mode1Add));
        Assert.False(PermissionPolicy.Has(user, role, Permission.Mode1Delete));
    }

    private static void AssertIndependent(Permission granted, params Permission[] denied)
    {
        var user = User();
        var role = Role(granted);
        Assert.True(PermissionPolicy.Has(user, role, granted));
        foreach (var permission in denied) Assert.False(PermissionPolicy.Has(user, role, permission));
    }

    private static UserAccount User() => new() { UserName = "operator", RoleName = "Operator", Enabled = true };
    private static RoleDefinition Role(params Permission[] permissions) => new()
    {
        Name = "Operator",
        Permissions = permissions.Select(x => x.ToString()).ToList()
    };
}
