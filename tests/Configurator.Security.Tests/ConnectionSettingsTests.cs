using Microsoft.Data.SqlClient;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class ConnectionSettingsTests
{
    [Fact]
    public void WindowsAuthenticationIsSharedByMasterAndProjectAndRequiresCertificateValidation()
    {
        var settings = new ConnectionSettings { Server = "server", User = "", Password = "" };
        Assert.True(settings.IsValid());
        var master = new SqlConnectionStringBuilder(settings.ToConnectionString("master"));
        var project = new SqlConnectionStringBuilder(settings.ToConnectionString("project"));
        Assert.True(master.IntegratedSecurity);
        Assert.True(project.IntegratedSecurity);
        Assert.False(master.TrustServerCertificate);
        Assert.False(project.TrustServerCertificate);
        Assert.True((bool)master.Encrypt);
    }

    [Fact]
    public void SqlAuthenticationIsSharedByMasterAndProject()
    {
        var settings = new ConnectionSettings { Server = "server", User = "user", Password = "password" };
        Assert.True(settings.IsValid());
        foreach (var database in new[] { "master", "project" })
        {
            var builder = new SqlConnectionStringBuilder(settings.ToConnectionString(database));
            Assert.False(builder.IntegratedSecurity);
            Assert.Equal("user", builder.UserID);
            Assert.Equal("password", builder.Password);
            Assert.False(builder.TrustServerCertificate);
        }
    }
    [Fact]
    public void OnlyExactLocalSharedMemoryEndpointMayTrustServerCertificate()
    {
        var local = Environment.MachineName;
        var settings = new ConnectionSettings();
        foreach (var name in new[] { local, local + "\\WINCC" })
        {
            foreach (var endpoint in new[] { name, "lpc:" + name })
            {
                settings.Server = endpoint;
                var builder = new SqlConnectionStringBuilder(settings.ToConnectionString("master"));
                Assert.Equal("lpc:" + name, builder.DataSource);
                Assert.True(builder.TrustServerCertificate);
                Assert.True((bool)builder.Encrypt);
            }
        }
        foreach (var endpoint in new[] { local + ".example", "lpc:remote", "tcp:" + local, local + "\\WINCC,1433", local + "\\A\\B", "lpc:localhost" })
        {
            settings.Server = endpoint;
            Assert.False(new SqlConnectionStringBuilder(settings.ToConnectionString("master")).TrustServerCertificate);
        }
    }
}
