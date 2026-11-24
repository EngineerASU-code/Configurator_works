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
        var settings = new ConnectionSettings { Server = "lpc:" + Environment.MachineName };
        Assert.True(new SqlConnectionStringBuilder(settings.ToConnectionString("master")).TrustServerCertificate);
        foreach (var endpoint in new[] { "HOME", "lpc:remote", "lpc:" + Environment.MachineName + "\\instance", "tcp:" + Environment.MachineName, Environment.MachineName, "lpc:localhost" })
        {
            settings.Server = endpoint;
            Assert.False(new SqlConnectionStringBuilder(settings.ToConnectionString("master")).TrustServerCertificate);
        }
    }
}
