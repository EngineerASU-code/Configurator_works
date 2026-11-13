using System;
using System.Linq;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class PermissionCatalogTests
{
    [Fact]
    public void EveryExistingPermissionAppearsExactlyOnceInRoleEditorCatalog()
    {
        var entries = PermissionCatalog.AllEntries;
        Assert.Equal(Enum.GetValues<Permission>().Length, entries.Count);
        Assert.Equal(Enum.GetValues<Permission>().Length, entries.Select(x => x.Permission).Distinct().Count());
        Assert.Equal(Enum.GetValues<Permission>().OrderBy(x => x), entries.Select(x => x.Permission).OrderBy(x => x));
        Assert.All(entries, entry =>
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.False(string.IsNullOrWhiteSpace(entry.Description));
        });
    }

    [Fact]
    public void CheckToolsDescribesExactlyTheFourExistingCheckCommandsAndTheirOnlineDependency()
    {
        var entry = PermissionCatalog.Get(Permission.CheckTools);
        Assert.Contains("свободных полей", entry.Details.ToLowerInvariant());
        Assert.Contains("свободных адресов", entry.Details.ToLowerInvariant());
        Assert.Contains("дублирования адресов", entry.Details.ToLowerInvariant());
        Assert.Contains("дублирования записей", entry.Details.ToLowerInvariant());
        Assert.Contains("online-бд", entry.Details.ToLowerInvariant());
        Assert.Contains("свободный sql", entry.Details.ToLowerInvariant());
        var user = new UserAccount { UserName = "operator", RoleName = "Checker", Enabled = true };
        var role = new RoleDefinition { Name = "Checker", Permissions = { Permission.CheckTools.ToString() } };
        Assert.True(PermissionPolicy.Has(user, role, Permission.CheckTools));
        Assert.False(PermissionPolicy.Has(user, role, Permission.FreeSql));
    }

    [Fact]
    public void PermissionCatalogKeepsCrudRightsIndependent()
    {
        var crud = new[] { Permission.Mode1Add, Permission.Mode1Edit, Permission.Mode1Delete };
        Assert.All(crud, permission => Assert.Single(PermissionCatalog.AllEntries, x => x.Permission == permission));
        Assert.NotEqual(Permission.Mode1Add, Permission.Mode1Edit);
        Assert.NotEqual(Permission.Mode1Edit, Permission.Mode1Delete);
        Assert.NotEqual(Permission.Mode1Add, Permission.Mode1Delete);
    }
}
