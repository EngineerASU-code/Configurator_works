using Configurator;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class FreeSqlPolicyTests
{
    [Theory]
    [InlineData("USE master")]
    [InlineData("/* comment */ USE master")]
    [InlineData("-- comment before command\nUSE [master]")]
    [InlineData("SELECT 1; DELETE FROM dbo.Users")]
    [InlineData("SELECT 1\nGO\nDELETE FROM dbo.Users")]
    [InlineData("SELECT * FROM OtherDb.dbo.Users")]
    [InlineData("SELECT * FROM [OtherDb].[dbo].[Users]")]
    [InlineData("EXEC dbo.DoSomething")]
    [InlineData("EXEC sys.sp_executesql N'SELECT 1'")]
    [InlineData("SELECT * INTO dbo.CopyOfUsers FROM dbo.Users")]
    [InlineData("SELECT * FROM OPENROWSET(BULK 'c:\\secret', SINGLE_CLOB) AS x")]
    [InlineData("SELECT * FROM OPENROWSET(BULK 'c:\\secret', SINGLE_BLOB) AS x")]
    [InlineData("SELECT * FROM OPENQUERY(RemoteServer, 'SELECT 1')")]
    public void RejectsContextSwitchingMultipleCommandsProceduresAndExternalSources(string sql)
    {
        Assert.Throws<InvalidOperationException>(() => FreeSqlPolicy.Validate(sql));
    }

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("SELECT * FROM dbo.Items WHERE Name = @name")]
    [InlineData("/* comment */ SELECT [Id] FROM [dbo].[Items] WHERE [Name] = N'USE master'")]
    [InlineData("WITH c AS (SELECT 1 AS Id) SELECT Id FROM c")]
    [InlineData("UPDATE dbo.Items SET Name = N'value' WHERE Id = 1")]
    [InlineData("INSERT INTO dbo.Items (Name) VALUES (N'value')")]
    [InlineData("DELETE FROM dbo.Items WHERE Id = 1")]
    public void AllowsSingleDataStatementAndCte(string sql)
    {
        FreeSqlPolicy.Validate(sql);
    }

    [Fact]
    public void RejectsMalformedSqlFailClosed()
    {
        Assert.Throws<InvalidOperationException>(() => FreeSqlPolicy.Validate("SELECT FROM"));
    }

    [Fact]
    public async Task RejectedStatementFailsBeforeOpeningConnectionOrExecutingAnything()
    {
        var db = new DatabaseService("not a valid connection string");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => db.ExecuteFreeSqlAsync("/* leading comment */ USE master"));
        Assert.Contains("Разрешены только одиночные", ex.Message);
    }

    [Fact]
    public void FreeSqlPermissionIsNotGrantedWhenRoleDoesNotContainIt()
    {
        var user = new UserAccount { UserName = "operator", RoleName = "Operator", Enabled = true };
        var role = new RoleDefinition { Name = "Operator", Permissions = new List<string> { Permission.ViewData.ToString() } };
        Assert.False(PermissionPolicy.Has(user, role, Permission.FreeSql));
    }
}
