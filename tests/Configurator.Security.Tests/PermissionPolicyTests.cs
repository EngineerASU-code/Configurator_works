using Configurator;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class PermissionPolicyTests
{
    private static UserAccount Designer() => new() { UserName = "Инженер-ПНР", RoleName = "Проектировщик", Enabled = true };
    private static RoleDefinition Role(params Permission[] permissions) => new()
    {
        Name = "Проектировщик",
        Permissions = permissions.Select(p => p.ToString()).ToList()
    };

    [Fact]
    public void Mode2SubmodesAndMode3VisibilityAreExplicitEvenForAdministrator()
    {
        var admin = new UserAccount { UserName = "admin", RoleName = "Администратор" };
        var role = Role(Permission.Mode2Show, Permission.Mode2GraphShow, Permission.Mode3Show);
        Assert.True(PermissionPolicy.Has(admin, role, Permission.Mode2GraphShow));
        Assert.False(PermissionPolicy.Has(admin, role, Permission.Mode2RelationsShow));
        Assert.False(PermissionPolicy.Has(admin, role, Permission.Mode2FieldsShow));
        Assert.True(PermissionPolicy.Has(admin, role, Permission.Mode3Show));
        Assert.False(PermissionPolicy.Has(admin, role, Permission.Mode4Show));
    }

    [Fact]
    public void Mode3StructureManagementDoesNotGrantInstanceEditOrDelete()
    {
        var user = Designer();
        var role = Role(Permission.Mode3View, Permission.Mode3Manage, Permission.ManageObjects);
        Assert.True(PermissionPolicy.Has(user, role, Permission.Mode3Manage));
        Assert.True(PermissionPolicy.Has(user, role, Permission.ManageObjects));
        Assert.False(PermissionPolicy.Has(user, role, Permission.EditData));
        Assert.False(PermissionPolicy.Has(user, role, Permission.DeleteData));
        Assert.False(PermissionPolicy.Has(user, role, Permission.AddData));
    }

    [Fact]
    public void InstancePermissionsAreIndependent()
    {
        var user = Designer();
        var role = Role(Permission.Mode3View, Permission.EditData);
        Assert.True(PermissionPolicy.Has(user, role, Permission.EditData));
        Assert.False(PermissionPolicy.Has(user, role, Permission.DeleteData));
        Assert.False(PermissionPolicy.Has(user, role, Permission.Mode3Manage));
    }

    [Fact]
    public void AnonymousUserHasNoPermissions()
    {
        Assert.False(PermissionPolicy.Has(null, Role(Permission.Mode3Manage), Permission.Mode3Manage));
    }

    [Fact]
    public void UnverifiedAdminNameDoesNotBypassRolePermissions()
    {
        var admin = new UserAccount { UserName = "admin", RoleName = "Администратор" };
        Assert.False(PermissionPolicy.Has(admin, Role(), Permission.DeleteData));
    }

    [Fact]
    public void AdministratorMustRespectExplicitModeVisibilityPermissions()
    {
        var admin = new UserAccount { UserName = "admin", RoleName = "Администратор" };
        var role = new RoleDefinition
        {
            Name = "Администратор",
            Permissions = new List<string> { Permission.Mode2Show.ToString(), Permission.Mode4Show.ToString() }
        };

        Assert.False(PermissionPolicy.Has(admin, role, Permission.Mode1Show));
        Assert.True(PermissionPolicy.Has(admin, role, Permission.Mode2Show));
        Assert.True(PermissionPolicy.Has(admin, role, Permission.Mode4Show));
        // An admin-named object without a verified active session has no implicit privileges.
        Assert.False(PermissionPolicy.Has(admin, role, Permission.DeleteData));
    }
    [Fact]
    public void Mode1PermissionsAreIndependentAndDoNotRequireAdministratorRole()
    {
        var user = new UserAccount { UserName = "operator", RoleName = "Оператор", Enabled = true };
        var role = Role(Permission.Mode1View, Permission.Mode1Add, Permission.Mode1Edit);

        Assert.True(PermissionPolicy.Has(user, role, Permission.Mode1View));
        Assert.True(PermissionPolicy.Has(user, role, Permission.Mode1Add));
        Assert.True(PermissionPolicy.Has(user, role, Permission.Mode1Edit));
        Assert.False(PermissionPolicy.Has(user, role, Permission.Mode1Delete));
    }

    [Fact]
    public void LegacyMode1PermissionsRemainCompatibleWhenNoModeSpecificPermissionsExist()
    {
        var user = new UserAccount { UserName = "operator", RoleName = "Оператор", Enabled = true };
        var role = Role(Permission.ViewData, Permission.AddData, Permission.EditData);

        Assert.True(PermissionPolicy.Has(user, role, Permission.Mode1View));
        Assert.True(PermissionPolicy.Has(user, role, Permission.Mode1Add));
        Assert.True(PermissionPolicy.Has(user, role, Permission.Mode1Edit));
        Assert.False(PermissionPolicy.Has(user, role, Permission.Mode1Delete));
    }

    [Fact]
    public void ModeVisibilityPermissionsAreIndependentFromViewAndEditPermissions()
    {
        var user = new UserAccount { UserName = "operator", RoleName = "Оператор", Enabled = true };
        var role = Role(Permission.Mode1Show, Permission.Mode2Show, Permission.Mode1Add);

        Assert.True(PermissionPolicy.Has(user, role, Permission.Mode1Show));
        Assert.True(PermissionPolicy.Has(user, role, Permission.Mode2Show));
        Assert.True(PermissionPolicy.Has(user, role, Permission.Mode1Add));
        Assert.False(PermissionPolicy.Has(user, role, Permission.Mode1View));
        Assert.False(PermissionPolicy.Has(user, role, Permission.Mode2View));
    }

    [Fact]
    public void Mode4RequiresExplicitViewPermissionAsWellAsVisibility()
    {
        var user = new UserAccount { UserName = "operator", RoleName = "Оператор", Enabled = true };
        var visibleOnly = Role(Permission.Mode4Show);
        Assert.True(PermissionPolicy.CanShowMode(user, visibleOnly, 4));
        Assert.False(PermissionPolicy.CanViewMode(user, visibleOnly, 4));
        Assert.False(PermissionPolicy.CanEnterMode(user, visibleOnly, 4));

        var viewer = Role(Permission.Mode4Show, Permission.Mode4View);
        Assert.True(PermissionPolicy.CanEnterMode(user, viewer, 4));
    }

    [Fact]
    public void Mode2ManageAuthorizesArchitectureHandlersWithoutGrantingMode3InstanceRights()
    {
        var user = new UserAccount { UserName = "operator", RoleName = "Оператор", Enabled = true };
        var role = Role(Permission.Mode2Manage);
        Assert.True(PermissionPolicy.Has(user, role, Permission.ManageClasses));
        Assert.True(PermissionPolicy.Has(user, role, Permission.ManageRelations));
        Assert.False(PermissionPolicy.Has(user, role, Permission.AddData));
        Assert.False(PermissionPolicy.Has(user, role, Permission.EditData));
        Assert.False(PermissionPolicy.Has(user, role, Permission.DeleteData));
    }

    [Fact]
    public void Mode3ManageAuthorizesObjectArchitectureButNotInstanceCrud()
    {
        var user = new UserAccount { UserName = "operator", RoleName = "Оператор", Enabled = true };
        var role = Role(Permission.Mode3Manage);
        Assert.True(PermissionPolicy.Has(user, role, Permission.ManageObjects));
        Assert.False(PermissionPolicy.Has(user, role, Permission.AddData));
        Assert.False(PermissionPolicy.Has(user, role, Permission.EditData));
        Assert.False(PermissionPolicy.Has(user, role, Permission.DeleteData));
    }

    [Theory]
    [InlineData(1, Permission.Mode1Show, Permission.Mode1View)]
    [InlineData(3, Permission.Mode3Show, Permission.Mode3View)]
    [InlineData(4, Permission.Mode4Show, Permission.Mode4View)]
    public void ModeEntryRequiresBothVisibilityAndView(int mode, Permission show, Permission view)
    {
        var user = new UserAccount { UserName = "operator", RoleName = "Оператор", Enabled = true };
        var onlyShow = Role(show);
        Assert.False(PermissionPolicy.CanEnterMode(user, onlyShow, mode));
        var both = Role(show, view);
        Assert.True(PermissionPolicy.CanEnterMode(user, both, mode));
    }

}
