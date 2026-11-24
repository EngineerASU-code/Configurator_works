using System;
using System.Linq;

namespace Configurator;

internal static class PermissionPolicy
{
    internal static bool CanShowMode(UserAccount user, RoleDefinition role, int mode) => mode switch
    {
        1 => Has(user, role, Permission.Mode1Show),
        2 => Has(user, role, Permission.Mode2Show) && (Has(user, role, Permission.Mode2GraphShow) || Has(user, role, Permission.Mode2RelationsShow) || Has(user, role, Permission.Mode2FieldsShow)),
        3 => Has(user, role, Permission.Mode3Show),
        4 => Has(user, role, Permission.Mode4Show),
        _ => false
    };

    internal static bool CanViewMode(UserAccount user, RoleDefinition role, int mode) => mode switch
    {
        1 => Has(user, role, Permission.Mode1View),
        2 => Has(user, role, Permission.Mode2View),
        3 => Has(user, role, Permission.Mode3View),
        4 => Has(user, role, Permission.Mode4View),
        _ => false
    };

    internal static bool CanEnterMode(UserAccount user, RoleDefinition role, int mode) =>
        CanShowMode(user, role, mode) && CanViewMode(user, role, mode);

    internal static bool Has(UserAccount currentUser, RoleDefinition role, Permission permission)
    {
        if (currentUser == null || !currentUser.Enabled) return false;

        if (string.Equals(currentUser.UserName, "admin", StringComparison.OrdinalIgnoreCase) &&
            ReferenceEquals(currentUser, UserAuthorizationService.CurrentUser) &&
            UserAuthorizationService.IsSuperAdmin) return true;
        if (role == null) return false;
        if (role.Has(permission)) return true;

        // Backward compatibility for older users.json files: only translate legacy permissions
        // when the role has not opted into the corresponding mode-specific permission set.
        bool hasMode1 = role.Permissions.Any(x => Enum.TryParse<Permission>(x, out var p) && p is Permission.Mode1View or Permission.Mode1Add or Permission.Mode1Edit or Permission.Mode1Delete);
        bool useLegacyMode1 = !hasMode1 && !UserAuthorizationService.IsCommissioningEngineer(currentUser.UserName);
        bool hasMode2 = role.Permissions.Any(x => Enum.TryParse<Permission>(x, out var p) && p is Permission.Mode2View or Permission.Mode2Manage);
        bool hasMode3 = role.Permissions.Any(x => Enum.TryParse<Permission>(x, out var p) && p is Permission.Mode3View or Permission.Mode3Manage);
        return permission switch
        {
            Permission.Mode1View => useLegacyMode1 && role.Has(Permission.ViewData),
            Permission.Mode1Add => useLegacyMode1 && role.Has(Permission.AddData),
            Permission.Mode1Edit => useLegacyMode1 && !hasMode3 && role.Has(Permission.EditData),
            Permission.Mode1Delete => useLegacyMode1 && !hasMode3 && role.Has(Permission.DeleteData),
            Permission.Mode2View => !hasMode2 && (role.Has(Permission.ManageClasses) || role.Has(Permission.ManageRelations)),
            Permission.Mode2Manage => !hasMode2 && (role.Has(Permission.ManageClasses) || role.Has(Permission.ManageRelations)),
            Permission.Mode3View => !hasMode3 && role.Has(Permission.ManageObjects),
            Permission.Mode3Manage => !hasMode3 && role.Has(Permission.ManageObjects),
            Permission.ViewData => !hasMode1 && role.Has(Permission.Mode1View),
            Permission.AddData => !hasMode1 && role.Has(Permission.Mode1Add),
            Permission.EditData => !hasMode1 && role.Has(Permission.Mode1Edit),
            Permission.DeleteData => !hasMode1 && role.Has(Permission.Mode1Delete),
            Permission.ManageClasses or Permission.ManageRelations => role.Has(Permission.Mode2Manage),
            Permission.ManageObjects => role.Has(Permission.Mode3Manage),
            _ => false
        };
    }
}
