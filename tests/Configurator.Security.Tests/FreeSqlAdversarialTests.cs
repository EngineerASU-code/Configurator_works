using Configurator;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class FreeSqlAdversarialTests
{
    [Theory]
    [InlineData("SELECT 1; -- trailing statement\nUPDATE dbo.Items SET Name='x'")]
    [InlineData("SELECT 1 /* comment */; EXEC xp_cmdshell 'whoami'")]
    [InlineData("DECLARE @x int; SELECT @x")]
    [InlineData("BEGIN TRANSACTION; DELETE FROM dbo.Items; COMMIT")]
    [InlineData("DROP TABLE dbo.Items")]
    [InlineData("ALTER TABLE dbo.Items ADD X int")]
    [InlineData("CREATE LOGIN test WITH PASSWORD='abc'")]
    [InlineData("TRUNCATE TABLE dbo.Items")]
    [InlineData("BACKUP DATABASE master TO DISK='x'")]
    [InlineData("RESTORE DATABASE master FROM DISK='x'")]
    [InlineData("BULK INSERT dbo.Items FROM 'x'")]
    [InlineData("SELECT * FROM dbo.Items; GO")]
    [InlineData("SELECT * FROM dbo.Items\nGO\nSELECT 2")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', 'Server=host;Trusted_Connection=yes;', 'SELECT 1') AS x")]
    [InlineData("SELECT * FROM OPENQUERY(RemoteServer, 'SELECT * FROM Users')")]
    [InlineData("SELECT * FROM [server].[database].[dbo].[Users]")]
    [InlineData("SELECT * FROM db..Users")]
    [InlineData("EXECUTE AS LOGIN = 'sa'")]
    [InlineData("RECONFIGURE")]
    [InlineData("SHUTDOWN")]
    public void RejectsDangerousOrMultiStatementPayloads(string sql)
    {
        Assert.Throws<InvalidOperationException>(() => FreeSqlPolicy.Validate(sql));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("-- only a comment")]
    [InlineData("SELECT")]
    [InlineData("SELECT * FROM")]
    [InlineData("UPDATE dbo.Items SET")]
    [InlineData("INSERT INTO dbo.Items")]
    public void RejectsEmptyOrMalformedPayloadsFailClosed(string sql)
    {
        Assert.Throws<InvalidOperationException>(() => FreeSqlPolicy.Validate(sql));
    }

    [Theory]
    [InlineData("SELECT 1 -- harmless comment")]
    [InlineData("SELECT N'; DROP TABLE dbo.Items' AS [literal]")]
    [InlineData("SELECT * FROM dbo.Items WHERE Name = N'EXEC xp_cmdshell'")]
    [InlineData("WITH c AS (SELECT 1 AS Id) SELECT Id FROM c")]
    [InlineData("UPDATE dbo.Items SET Name = N'changed' WHERE Id = 1")]
    public void AllowsSingleStatementWhenDangerousTextIsOnlyLiteralOrComment(string sql)
    {
        FreeSqlPolicy.Validate(sql);
    }

    [Fact]
    public async Task RejectedSqlDoesNotRequireAUsableConnectionString()
    {
        var database = new DatabaseService("this is not a connection string");
        await Assert.ThrowsAsync<InvalidOperationException>(() => database.ExecuteFreeSqlAsync("SELECT 1; DELETE FROM dbo.Items"));
    }
}
