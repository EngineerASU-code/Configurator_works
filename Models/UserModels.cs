using System;
using System.Collections.Generic;
using System.Linq;

namespace Configurator;

public enum Permission
{
    ViewData, AddData, EditData, DeleteData,
    ManageClasses, ManageTextLists, ManageRelations,
    RestoreJournal, ManageUsers, ExportData, FreeSql,
    ManageDatabases, CheckTools, ManageObjects,
    Mode1View, Mode1Add, Mode1Edit, Mode1Delete,
    Mode2View, Mode2Manage, Mode3View, Mode3Manage,
    Mode1Show, Mode2Show, Mode2GraphShow, Mode2RelationsShow, Mode2FieldsShow, Mode3Show, Mode4Show, Mode4View, Mode4Manage
}

public sealed class UserAccount
{
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string RoleName { get; set; } = "Пользователь";
    public string PasswordHash { get; set; } = "";
    public string PasswordSalt { get; set; } = "";
    // 0 = legacy PBKDF2-HMAC-SHA256/120000; 1 = current versioned format.
    public int PasswordHashVersion { get; set; } = 0;
    public int PasswordIterations { get; set; } = 120000;
    public bool Enabled { get; set; } = true;

    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName;
}

public sealed class RoleDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Permissions { get; set; } = new();
    public bool BuiltIn { get; set; }

    public bool Has(Permission permission) => Permissions.Contains(permission.ToString(), StringComparer.OrdinalIgnoreCase);
}

public sealed class UserStoreDocument
{
    public int SchemaVersion { get; set; }
    public List<RoleDefinition> Roles { get; set; } = new();
    public List<UserAccount> Users { get; set; } = new();
}
