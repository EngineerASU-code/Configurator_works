using Configurator;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class UserAuthorizationSecurityTests
{
    [Fact]
    public void NewPasswordMeetsSharedPolicyAndSupportsLongPassphrases()
    {
        UserAuthorizationService.ValidatePassword("abcde");
        UserAuthorizationService.ValidatePassword("abcde1234567");
        UserAuthorizationService.ValidatePassword(string.Join(' ', Enumerable.Repeat("correct-horse", 20)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("1234")]
    public void WeakPasswordIsRejectedBySharedPolicy(string password)
    {
        Assert.Throws<ArgumentException>(() => UserAuthorizationService.ValidatePassword(password));
        var user = new UserAccount { UserName = "test" };
        Assert.Throws<ArgumentException>(() => UserAuthorizationService.SetPassword(user, password));
    }

    [Theory]
    [InlineData("12345678901")]
    [InlineData("")]
    public void NewAdministratorPasswordRejectsShortValues(string password)
    {
        Assert.Throws<ArgumentException>(() => UserAuthorizationService.ValidateAdministratorPassword(password));
    }

    [Fact]
    public void AdministratorPolicyAcceptsTwelveCharactersAndLegacyPasswordsStillVerify()
    {
        UserAuthorizationService.ValidateAdministratorPassword("123456789012");
        var legacy = new UserAccount { UserName = "admin" };
        UserAuthorizationService.SetPassword(legacy, "abcde");
        Assert.True(UserAuthorizationService.VerifyPassword(legacy, "abcde"));
    }

    [Fact]
    public void AdministrativeSessionRequiresEnabledCurrentStoredAdminAndCompletedSetup()
    {
        var admin = new UserAccount { UserName = "admin", Enabled = true };
        Assert.True(UserAuthorizationService.HasValidAdministrativeSession(admin, new[] { admin }, false));
        Assert.False(UserAuthorizationService.HasValidAdministrativeSession(admin, new[] { new UserAccount { UserName = "admin", Enabled = true } }, false));
        admin.Enabled = false;
        Assert.False(UserAuthorizationService.HasValidAdministrativeSession(admin, new[] { admin }, false));
        Assert.Throws<UnauthorizedAccessException>(() => UserAuthorizationService.EnsureAdministrativeSession(admin, new[] { admin }, false));
        admin.Enabled = true;
        Assert.False(UserAuthorizationService.HasValidAdministrativeSession(admin, new[] { admin }, true));
        Assert.Throws<UnauthorizedAccessException>(() => UserAuthorizationService.EnsureAdministrativeSession(admin, new[] { admin }, true));
        Assert.False(UserAuthorizationService.HasValidAdministrativeSession(new UserAccount { UserName = "not-admin", Enabled = true }, new[] { admin }, false));
    }


    [Fact]
    public void LastAdministratorCannotBeDisabledButOtherAccountsCanBeDisabled()
    {
        UserAuthorizationService.EnsureAdministratorRemainsEnabled(isAdministratorAccount: false, enabled: false);
        Assert.Throws<InvalidOperationException>(() =>
            UserAuthorizationService.EnsureAdministratorRemainsEnabled(isAdministratorAccount: true, enabled: false));
        UserAuthorizationService.EnsureAdministratorRemainsEnabled(isAdministratorAccount: true, enabled: true);
    }

    [Fact]
    public void Mode4MigrationPreservesNavigationButDoesNotGrantViewOrManage()
    {
        var document = new UserStoreDocument
        {
            Roles = new List<RoleDefinition>
            {
                new() { Name = "Администратор", Permissions = new() { nameof(Permission.ManageUsers), nameof(Permission.Mode1Show), nameof(Permission.Mode2Show), nameof(Permission.Mode3Show) } },
                new() { Name = "Проектировщик", Permissions = new() { nameof(Permission.Mode2Manage) } },
                new() { Name = "Пользовательская", Permissions = new() { nameof(Permission.Mode1View) } },
                new() { Name = "Без доступа", Permissions = new() },
                new() { Name = "С явно заданным управлением", Permissions = new() { nameof(Permission.Mode4Manage) } }
            }
        };
        UserAuthorizationService.MigrateMode4Permissions(document, 5);
        Assert.All(document.Roles.Take(3), role => Assert.Contains(nameof(Permission.Mode4Show), role.Permissions));
        Assert.DoesNotContain(nameof(Permission.Mode4Show), document.Roles[3].Permissions);
        Assert.All(document.Roles.Take(4), role => Assert.DoesNotContain(nameof(Permission.Mode4View), role.Permissions));
        Assert.All(document.Roles.Take(4), role => Assert.DoesNotContain(nameof(Permission.Mode4Manage), role.Permissions));
        Assert.Contains(nameof(Permission.Mode4Manage), document.Roles[4].Permissions);
        Assert.Contains(nameof(Permission.ManageUsers), document.Roles[0].Permissions);
        Assert.Contains(nameof(Permission.Mode2Manage), document.Roles[1].Permissions);
        Assert.Contains(nameof(Permission.Mode1View), document.Roles[2].Permissions);
    }

    [Fact]
    public void FixedAccountsMigrateLegacyProfilesAndPreserveAdminCredential()
    {
        var directory = Directory.CreateTempSubdirectory();
        try
        {
            string path = Path.Combine(directory.FullName, "users.json");
            var fresh = UserAuthorizationService.LoadStore(path, out bool needsSetup);
            Assert.False(needsSetup);
            var admin = fresh.Users.Single(u => u.UserName == "admin");
            string hash = admin.PasswordHash, salt = admin.PasswordSalt;
            fresh.Users.Add(new UserAccount { UserName = "Инженер-1", RoleName = "Проектировщик" });
            File.WriteAllText(path, Newtonsoft.Json.JsonConvert.SerializeObject(fresh));
            var store = UserAuthorizationService.LoadStore(path, out needsSetup);
            Assert.Equal(new[] { "Инженер", "admin" }, LoginWindow.GetLoginChoices(store.Users).Select(u => u.UserName));
            Assert.Equal(hash, store.Users.Single(u => u.UserName == "admin").PasswordHash);
            Assert.Equal(salt, store.Users.Single(u => u.UserName == "admin").PasswordSalt);
            var engineer = store.Users.Single(u => u.UserName == "Инженер");
            var freshEngineer = fresh.Users.Single(u => u.UserName == "Инженер");
            Assert.Equal(freshEngineer.PasswordHash, engineer.PasswordHash);
            Assert.Equal(freshEngineer.PasswordSalt, engineer.PasswordSalt);
            Assert.Equal(32, Convert.FromBase64String(engineer.PasswordHash).Length);
            var role = store.Roles.Single(r => r.Name == engineer.RoleName);
            foreach (int mode in new[] { 1, 3, 4 }) Assert.True(PermissionPolicy.CanEnterMode(engineer, role, mode));
            Assert.False(PermissionPolicy.CanEnterMode(engineer, role, 2));
            Assert.False(PermissionPolicy.Has(engineer, role, Permission.ManageUsers));
            foreach (var permission in new[] { Permission.Mode1Add, Permission.Mode1Edit, Permission.Mode1Delete, Permission.Mode3Manage, Permission.Mode4Manage })
                Assert.True(PermissionPolicy.Has(engineer, role, permission));
            Assert.False(UserAuthorizationService.IsBuiltInUser("Инженер-1"));
        }
        finally { directory.Delete(recursive: true); }
    }

    [Fact]
    public void CommissioningEngineerGetsMode3AndMode4AccessByDefault()
    {
        var role = new RoleDefinition { Name = "Инженер ПНР" };

        Assert.True(UserAuthorizationService.EnsureCommissioningRolePermissions(role));
        Assert.False(UserAuthorizationService.EnsureCommissioningRolePermissions(role));
        Assert.True(role.BuiltIn);
        foreach (var permission in new[]
        {
            Permission.Mode3Show, Permission.Mode3View, Permission.Mode3Manage,
            Permission.Mode4Show, Permission.Mode4View, Permission.Mode4Manage,
            Permission.ManageObjects, Permission.ViewData, Permission.AddData,
            Permission.EditData, Permission.DeleteData
        })
            Assert.Contains(permission.ToString(), role.Permissions);
    }

    [Fact]
    public void LegacyPbkdf2HashVerifiesAndCanBeUpgradedToCurrentVersion()
    {
        const string password = "legacy-passphrase";
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 120_000, HashAlgorithmName.SHA256, 32);
        var user = new UserAccount { UserName = "legacy", PasswordSalt = Convert.ToBase64String(salt), PasswordHash = Convert.ToBase64String(hash) };

        Assert.True(UserAuthorizationService.VerifyPassword(user, password));
        Assert.Equal(0, user.PasswordHashVersion);
        UserAuthorizationService.SetPassword(user, password);
        Assert.Equal(1, user.PasswordHashVersion);
        Assert.Equal(600_000, user.PasswordIterations);
        Assert.True(UserAuthorizationService.VerifyPassword(user, password));
    }

    [Fact]
    public void VerifiedShortLegacyCredentialCanBeRehashedWithoutApplyingNewPasswordPolicy()
    {
        const string password = "old";
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 120_000, HashAlgorithmName.SHA256, 32);
        var user = new UserAccount { UserName = "admin", PasswordSalt = Convert.ToBase64String(salt), PasswordHash = Convert.ToBase64String(hash) };
        Assert.True(UserAuthorizationService.VerifyPassword(user, password));
        UserAuthorizationService.RehashVerifiedPassword(user, password);
        Assert.True(UserAuthorizationService.VerifyPassword(user, password));
        Assert.Throws<ArgumentException>(() => UserAuthorizationService.ValidateAdministratorPassword(password));
    }

    [Fact]
    public void WrongPasswordDoesNotChangeCredentialState()
    {
        var user = new UserAccount { UserName = "user" };
        UserAuthorizationService.SetPassword(user, "correct-password-12");
        string hash = user.PasswordHash;
        string salt = user.PasswordSalt;
        Assert.False(UserAuthorizationService.VerifyPassword(user, "incorrect-password"));
        Assert.Equal(hash, user.PasswordHash);
        Assert.Equal(salt, user.PasswordSalt);
    }

    [Fact]
    public void InvalidStorageIsRejectedWithoutChangingSourceFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ConfiguratorSecurityTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "users.json");
        const string invalid = "{ definitely not valid json";
        File.WriteAllText(path, invalid);
        try
        {
            Assert.ThrowsAny<Exception>(() => UserAuthorizationService.DeserializeDocument(File.ReadAllText(path)));
            Assert.Equal(invalid, File.ReadAllText(path));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void AtomicWriterReplacesExistingDocumentWithCompleteContent()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ConfiguratorSecurityTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "users.json");
        File.WriteAllText(path, "old-valid-document");
        try
        {
            UserAuthorizationService.WriteAtomically(path, "new-valid-document");
            Assert.Equal("new-valid-document", File.ReadAllText(path));
            Assert.Empty(Directory.GetFiles(dir, "*.tmp"));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void FailedAtomicWriteDoesNotDestroyExistingDirectoryOrContents()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ConfiguratorSecurityTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string destination = Path.Combine(dir, "users.json");
        Directory.CreateDirectory(destination); // forces the final file move to fail
        string sentinel = Path.Combine(destination, "sentinel.txt");
        File.WriteAllText(sentinel, "preserve-me");
        try
        {
            Assert.Throws<IOException>(() => UserAuthorizationService.WriteAtomically(destination, "replacement"));
            Assert.Equal("preserve-me", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
