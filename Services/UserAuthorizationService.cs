using System.Windows;
using System.IO;
using Newtonsoft.Json;
using System.Security.Cryptography;
using System.Text;

namespace Configurator;

public static class UserAuthorizationService
{
    private static readonly object Sync = new();
    private static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Configurator");
    private static readonly string FilePath = Path.Combine(DirectoryPath, "users.json");
    private static UserStoreDocument _document;
    private const int LegacyHashVersion = 0;
    private const int CurrentHashVersion = 1;
    private const int LegacyIterations = 120_000;
    // PBKDF2-HMAC-SHA256 current policy. Existing records keep their stored/legacy parameters until successful login.
    private const int CurrentIterations = 600_000;
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int MinimumPasswordLength = 5;
    private const int MaximumPasswordLength = 1024;
    private static readonly Dictionary<string, LoginAttemptState> LoginAttempts = new(StringComparer.OrdinalIgnoreCase);
    private sealed class LoginAttemptState { public int Failures; public DateTime BlockedUntilUtc; }
    public static UserAccount CurrentUser { get; private set; }
    public static RoleDefinition CurrentRole => CurrentUser == null ? null : _document?.Roles.FirstOrDefault(r => r.Name.Equals(CurrentUser.RoleName, StringComparison.OrdinalIgnoreCase));
    public static event EventHandler Changed;

    public static bool NeedsInitialSetup { get; private set; }
    private static bool _isNewInstallation;
    public static bool HasAdministratorAccount => _document?.Users?.Any(u => u.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase)) == true;

    public static void Initialize()
    {
        lock (Sync)
        {
            Directory.CreateDirectory(DirectoryPath);
            bool storeExists = File.Exists(FilePath);
            _isNewInstallation = !storeExists;
            if (storeExists)
            {
                try
                {
                    _document = DeserializeDocument(File.ReadAllText(FilePath));
                    if (_document == null || _document.Users == null || _document.Roles == null)
                        throw new InvalidDataException("Хранилище пользователей не содержит обязательные разделы.");
                }
                catch (Exception ex)
                {
                    _document = null;
                    throw new InvalidDataException($"Не удалось прочитать хранилище учётных записей '{FilePath}'. Оно не было перезаписано. Восстановите файл из резервной копии или обратитесь к администратору.", ex);
                }
            }
            else
            {
                _document = new UserStoreDocument();
            }

            int loadedSchemaVersion = _document.SchemaVersion;
            _document.Users ??= new List<UserAccount>();
            _document.Roles ??= new List<RoleDefinition>();
            foreach (var role in _document.Roles)
            {
                if (role == null || string.IsNullOrWhiteSpace(role.Name))
                    throw new InvalidDataException("Хранилище пользователей содержит роль без имени. Файл не был перезаписан.");
                role.Permissions ??= new List<string>();
            }
            if (_document.Users.Any(u => u == null || string.IsNullOrWhiteSpace(u.UserName)))
                throw new InvalidDataException("Хранилище пользователей содержит некорректную учётную запись. Файл не был перезаписан.");

            if (!_document.Roles.Any(r => r.Name.Equals("Администратор", StringComparison.OrdinalIgnoreCase)))
                _document.Roles.Add(new RoleDefinition { Name = "Администратор", Description = "Полный доступ, включая управление пользователями", BuiltIn = true, Permissions = Enum.GetValues<Permission>().Select(x => x.ToString()).ToList() });
            if (!_document.Roles.Any(r => r.Name.Equals("Проектировщик", StringComparison.OrdinalIgnoreCase)))
            {
                var engineerPermissions = Enum.GetValues<Permission>()
                    .Where(p => p is not (Permission.Mode1Add or Permission.Mode1Edit or Permission.Mode1Delete or Permission.AddData or Permission.EditData or Permission.DeleteData or Permission.ManageUsers or Permission.RestoreJournal or Permission.FreeSql))
                    .Select(x => x.ToString()).ToList();
                _document.Roles.Add(new RoleDefinition { Name = "Проектировщик", Description = "Просмотр и управление объектной структурой; права на изменение экземпляров назначаются отдельно", BuiltIn = true, Permissions = engineerPermissions });
            }
            if (!_document.Roles.Any(r => r.Name.Equals("Гость", StringComparison.OrdinalIgnoreCase)))
                _document.Roles.Add(new RoleDefinition { Name = "Гость", Description = "Только просмотр; без изменения данных и структуры", BuiltIn = true, Permissions = new List<string> { Permission.ViewData.ToString(), Permission.Mode1View.ToString(), Permission.Mode2View.ToString(), Permission.Mode3View.ToString() } });
            // The commissioning engineer is intended to operate Mode 3 fully.
            // Keep elevated user administration, unrestricted SQL and journal restore separate.
            bool commissioningRoleNeedsSave = false;
            if (!_document.Roles.Any(r => r.Name.Equals("Инженер ПНР", StringComparison.OrdinalIgnoreCase)))
            {
                commissioningRoleNeedsSave = true;
                var commissioningPermissions = new[]
                {
                    Permission.Mode3Show, Permission.Mode3View, Permission.Mode3Manage,
                    Permission.ManageObjects, Permission.ViewData, Permission.AddData,
                    Permission.EditData, Permission.DeleteData
                }.Select(x => x.ToString()).ToList();
                _document.Roles.Add(new RoleDefinition
                {
                    Name = "Инженер ПНР",
                    Description = "Полная работа с объектами в режиме 3: создание, просмотр, изменение, дублирование и удаление",
                    BuiltIn = true,
                    Permissions = commissioningPermissions
                });
            }

            var commissioningRole = _document.Roles.First(r => r.Name.Equals("Инженер ПНР", StringComparison.OrdinalIgnoreCase));
            commissioningRole.BuiltIn = true;
            foreach (var permission in new[] { Permission.Mode3Show, Permission.Mode3View, Permission.Mode3Manage, Permission.ManageObjects, Permission.ViewData, Permission.AddData, Permission.EditData, Permission.DeleteData })
                if (!commissioningRole.Permissions.Contains(permission.ToString(), StringComparer.OrdinalIgnoreCase))
                {
                    commissioningRole.Permissions.Add(permission.ToString());
                    commissioningRoleNeedsSave = true;
                }

            var administratorRole = _document.Roles.First(r => r.Name.Equals("Администратор", StringComparison.OrdinalIgnoreCase));
            administratorRole.BuiltIn = true;
            var designerRole = _document.Roles.First(r => r.Name.Equals("Проектировщик", StringComparison.OrdinalIgnoreCase));
            designerRole.BuiltIn = true;
            // Keep existing customized permissions intact, but ensure the two explicitly
            // required capabilities are present even in older user stores that already
            // contain a Project Designer role with an incomplete permission list.
            foreach (var requiredPermission in new[] { Permission.Mode1Show, Permission.Mode1View, Permission.ManageDatabases })
                if (!designerRole.Permissions.Contains(requiredPermission.ToString(), StringComparer.OrdinalIgnoreCase))
                    designerRole.Permissions.Add(requiredPermission.ToString());

            // One-time schema migration: keep instance edit/delete permissions separate from object-structure management.
            // Do not add EditData/DeleteData to the built-in designer role.
            if (_document.SchemaVersion < 4)
            {
                designerRole.Permissions.RemoveAll(x => string.Equals(x, Permission.AddData.ToString(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x, Permission.EditData.ToString(), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(x, Permission.DeleteData.ToString(), StringComparison.OrdinalIgnoreCase));
                _document.SchemaVersion = 4;
            }

            // Never manufacture a known-password administrator or silently repair a missing password.
            var admin = _document.Users.FirstOrDefault(u => u.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase));
            if (storeExists && admin == null)
                throw new InvalidDataException("В существующем хранилище отсутствует учётная запись администратора. Файл не перезаписан; восстановите резервную копию users.json.");
            if (admin != null && !HasValidCredentialEncoding(admin))
                throw new InvalidDataException("Данные пароля административной учётной записи повреждены. Файл не перезаписан; восстановите резервную копию users.json.");
            NeedsInitialSetup = admin == null || !admin.Enabled || IsDefaultAdminPassword(admin);
            if (admin != null)
            {
                // Preserve the stored enabled state. Initialization must never silently reactivate
                // a disabled administrator account. Its explicit recovery path is described in docs.
                admin.DisplayName = "Администратор";
                admin.RoleName = "Администратор";
            }

            // Existing accounts are preserved. A missing engineer account is created disabled, with an unknown random secret;
            // the administrator must explicitly set its password before enabling it.
            var engineer = _document.Users.FirstOrDefault(u => u.UserName.Equals("Инженер-ПНР", StringComparison.OrdinalIgnoreCase));
            if (engineer == null && !NeedsInitialSetup)
            {
                engineer = new UserAccount { UserName = "Инженер-ПНР", DisplayName = "Инженер-ПНР", RoleName = "Проектировщик", Enabled = false };
                SetPassword(engineer, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                _document.Users.Add(engineer);
            }
            if (engineer != null)
            {
                engineer.DisplayName = "Инженер-ПНР";
                if (!_document.Roles.Any(r => r.Name.Equals(engineer.RoleName, StringComparison.OrdinalIgnoreCase))) engineer.RoleName = "Проектировщик";
            }

            // Compatibility migration: preserve the visibility of modes for existing roles.
            // New visibility rights remain independent from view/edit permissions after migration.
            if (loadedSchemaVersion < 5)
            foreach (var role in _document.Roles)
            {
                bool hasMode1Visibility = role.Permissions.Contains(nameof(Permission.Mode1Show), StringComparer.OrdinalIgnoreCase);
                bool hasMode2Visibility = role.Permissions.Contains(nameof(Permission.Mode2Show), StringComparer.OrdinalIgnoreCase);
                bool hadMode1Access = role.Permissions.Any(x => x.Equals(nameof(Permission.Mode1View), StringComparison.OrdinalIgnoreCase)
                    || x.Equals(nameof(Permission.Mode1Add), StringComparison.OrdinalIgnoreCase)
                    || x.Equals(nameof(Permission.Mode1Edit), StringComparison.OrdinalIgnoreCase)
                    || x.Equals(nameof(Permission.Mode1Delete), StringComparison.OrdinalIgnoreCase)
                    || x.Equals(nameof(Permission.ViewData), StringComparison.OrdinalIgnoreCase)
                    || x.Equals(nameof(Permission.AddData), StringComparison.OrdinalIgnoreCase)
                    || x.Equals(nameof(Permission.EditData), StringComparison.OrdinalIgnoreCase)
                    || x.Equals(nameof(Permission.DeleteData), StringComparison.OrdinalIgnoreCase));
                bool hadMode2Access = role.Permissions.Any(x => x.Equals(nameof(Permission.Mode2View), StringComparison.OrdinalIgnoreCase)
                    || x.Equals(nameof(Permission.Mode2Manage), StringComparison.OrdinalIgnoreCase)
                    || x.Equals(nameof(Permission.ManageClasses), StringComparison.OrdinalIgnoreCase)
                    || x.Equals(nameof(Permission.ManageRelations), StringComparison.OrdinalIgnoreCase));
                if (!hasMode1Visibility && hadMode1Access) role.Permissions.Add(nameof(Permission.Mode1Show));
                if (!hasMode2Visibility && hadMode2Access) role.Permissions.Add(nameof(Permission.Mode2Show));
            }
            // Schema 6: preserve legacy navigation without inventing data/management rights.
            // Mode 4 is still a stub; legacy roles receive visibility only, never View/Manage.
            if (loadedSchemaVersion < 6)
            {
                MigrateMode4Permissions(_document, loadedSchemaVersion);
                _document.SchemaVersion = 6;
            }
            // Schema 7: make each Mode 2 submode and Mode 3 navigation explicitly visible.
            // Preserve the previous UI for existing roles by granting the new visibility flags
            // only during migration; subsequent user changes remain authoritative.
            if (loadedSchemaVersion < 7)
            {
                foreach (var role in _document.Roles)
                {
                    bool hadMode2 = role.Permissions.Contains(nameof(Permission.Mode2Show), StringComparer.OrdinalIgnoreCase)
                        || role.Permissions.Any(x => x.Equals(nameof(Permission.Mode2View), StringComparison.OrdinalIgnoreCase)
                            || x.Equals(nameof(Permission.Mode2Manage), StringComparison.OrdinalIgnoreCase)
                            || x.Equals(nameof(Permission.ManageClasses), StringComparison.OrdinalIgnoreCase)
                            || x.Equals(nameof(Permission.ManageRelations), StringComparison.OrdinalIgnoreCase));
                    if (hadMode2)
                        foreach (var permission in new[] { nameof(Permission.Mode2GraphShow), nameof(Permission.Mode2RelationsShow), nameof(Permission.Mode2FieldsShow) })
                            if (!role.Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase)) role.Permissions.Add(permission);
                    if (!role.Permissions.Contains(nameof(Permission.Mode3Show), StringComparer.OrdinalIgnoreCase))
                        role.Permissions.Add(nameof(Permission.Mode3Show));
                }
                _document.SchemaVersion = 7;
            }
            _document.SchemaVersion = Math.Max(_document.SchemaVersion, 7);
            if (!NeedsInitialSetup && (!storeExists || loadedSchemaVersion < 7 || commissioningRoleNeedsSave)) Save();
        }
    }

    internal static UserStoreDocument DeserializeDocument(string json)
    {
        var document = JsonConvert.DeserializeObject<UserStoreDocument>(json);
        if (document == null || document.Users == null || document.Roles == null)
            throw new InvalidDataException("Хранилище пользователей не содержит обязательные разделы.");
        return document;
    }

    private static bool HasValidCredentialEncoding(UserAccount user)
    {
        if (user == null || string.IsNullOrWhiteSpace(user.PasswordSalt) || string.IsNullOrWhiteSpace(user.PasswordHash)) return false;
        try
        {
            var salt = Convert.FromBase64String(user.PasswordSalt);
            var hash = Convert.FromBase64String(user.PasswordHash);
            int version = user.PasswordHashVersion;
            int iterations = user.PasswordIterations <= 0 ? LegacyIterations : user.PasswordIterations;
            return salt.Length >= SaltSize && hash.Length == HashSize
                && (version == LegacyHashVersion || version == CurrentHashVersion)
                && iterations >= LegacyIterations && iterations <= CurrentIterations;
        }
        catch { return false; }
    }

    private static bool IsDefaultAdminPassword(UserAccount user)
    {
        if (user == null || string.IsNullOrWhiteSpace(user.PasswordSalt) || string.IsNullOrWhiteSpace(user.PasswordHash)) return false;
        try
        {
            var salt = Convert.FromBase64String(user.PasswordSalt);
            var expected = Convert.FromBase64String(user.PasswordHash);
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes("admin"), salt, GetIterations(user), HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }

    public static void CompleteInitialSetup(string currentAdminPassword, string newAdminPassword)
    {
        lock (Sync)
        {
            ValidatePassword(newAdminPassword, "Новый пароль администратора");

            string originalDocument = JsonConvert.SerializeObject(_document);
            bool originalNeedsSetup = NeedsInitialSetup;
            try
            {
                var admin = _document.Users.FirstOrDefault(u => u.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase));
                if (admin != null)
                {
                    if (!VerifyPassword(admin, currentAdminPassword ?? ""))
                        throw new UnauthorizedAccessException("Текущий пароль администратора не подтверждён. Учётная запись не изменена.");
                    SetPassword(admin, newAdminPassword);
                    admin.DisplayName = "Администратор";
                    admin.RoleName = "Администратор";
                    admin.Enabled = true;
                }
                else
                {
                    if (!_isNewInstallation)
                        throw new InvalidOperationException("В существующем хранилище отсутствует учётная запись администратора. Для безопасного восстановления верните резервную копию users.json; новая административная учётная запись не создана.");
                    admin = new UserAccount { UserName = "admin", DisplayName = "Администратор", RoleName = "Администратор", Enabled = true };
                    SetPassword(admin, newAdminPassword);
                    _document.Users.Add(admin);
                }

                // Keep existing accounts. On a clean install the engineer remains disabled until an administrator assigns a password.
                var engineer = _document.Users.FirstOrDefault(u => u.UserName.Equals("Инженер-ПНР", StringComparison.OrdinalIgnoreCase));
                if (engineer == null)
                {
                    engineer = new UserAccount { UserName = "Инженер-ПНР", DisplayName = "Инженер-ПНР", RoleName = "Проектировщик", Enabled = false };
                    SetPassword(engineer, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
                    _document.Users.Add(engineer);
                }
                _document.SchemaVersion = Math.Max(_document.SchemaVersion, 5);
                Save();
                NeedsInitialSetup = false;
                CurrentUser = admin;
                Changed?.Invoke(null, EventArgs.Empty);
            }
            catch
            {
                _document = JsonConvert.DeserializeObject<UserStoreDocument>(originalDocument);
                NeedsInitialSetup = originalNeedsSetup;
                throw;
            }
        }
    }

    private static void CreateUserInternal(string name, string display, string role, string password, bool enabled)
    {
        ValidatePassword(password, "Пароль");
        var user = new UserAccount { UserName = name.Trim(), DisplayName = string.IsNullOrWhiteSpace(display) ? name.Trim() : display.Trim(), RoleName = role, Enabled = enabled };
        SetPassword(user, password);
        _document.Users.Add(user);
    }

    [Obsolete("Автоматический вход отключён. Используйте явную авторизацию.")]
    public static void StartAsDefaultUser() => throw new InvalidOperationException("Автоматический вход отключён. Требуется явная авторизация.");

    public static void EnterGuest()
    {
        InitializeIfNeeded();
        var guestRole = _document.Roles.FirstOrDefault(r => r.Name.Equals("Гость", StringComparison.OrdinalIgnoreCase));
        if (guestRole == null) throw new InvalidOperationException("Не найдена встроенная роль «Гость».");
        CurrentUser = new UserAccount { UserName = "guest", DisplayName = "Гость", RoleName = guestRole.Name, Enabled = true };
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Logout()
    {
        CurrentUser = null;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static bool Login(string userName, string password, out string error)
    {
        error = null;
        InitializeIfNeeded();
        if (NeedsInitialSetup) { error = "Сначала завершите настройку пароля администратора."; return false; }
        if (string.IsNullOrEmpty(password)) { error = "Пустой пароль запрещён."; return false; }
        string key = (userName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(key)) key = "<empty>";
        lock (Sync)
        {
            if ((LoginAttempts.TryGetValue(key, out var attempt) || LoginAttempts.TryGetValue("<overflow>", out attempt)) && attempt.BlockedUntilUtc > DateTime.UtcNow)
            {
                int seconds = Math.Max(1, (int)Math.Ceiling((attempt.BlockedUntilUtc - DateTime.UtcNow).TotalSeconds));
                error = $"Слишком много неудачных попыток. Повторите вход через {seconds} сек.";
                return false;
            }
            var user = _document.Users.FirstOrDefault(u => u.UserName.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (user == null || !user.Enabled || !VerifyPassword(user, password))
            {
                RegisterFailedLogin(key);
                error = "Неверный логин/пароль или учётная запись отключена.";
                return false;
            }

            bool needsRehash = user.PasswordHashVersion != CurrentHashVersion || GetIterations(user) != CurrentIterations;
            if (needsRehash)
            {
                string oldHash = user.PasswordHash, oldSalt = user.PasswordSalt;
                int oldVersion = user.PasswordHashVersion, oldIterations = user.PasswordIterations;
                SetPassword(user, password);
                try { Save(); }
                catch
                {
                    user.PasswordHash = oldHash; user.PasswordSalt = oldSalt;
                    user.PasswordHashVersion = oldVersion; user.PasswordIterations = oldIterations;
                    error = "Пароль проверен, но обновить параметры его хранения не удалось. Повторите попытку позже.";
                    return false;
                }
            }
            LoginAttempts.Remove(key);
            LoginAttempts.Remove("<overflow>");
            CurrentUser = user;
        }
        Changed?.Invoke(null, EventArgs.Empty);
        return true;
    }

    private static void RegisterFailedLogin(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) key = "<empty>";
        if (!LoginAttempts.TryGetValue(key, out var state))
        {
            if (LoginAttempts.Count >= 4096)
            {
                DateTime now = DateTime.UtcNow;
                foreach (var expired in LoginAttempts.Where(x => x.Value.BlockedUntilUtc < now && x.Value.Failures == 0).Select(x => x.Key).ToArray()) LoginAttempts.Remove(expired);
                if (LoginAttempts.Count >= 4096) key = "<overflow>";
            }
            if (!LoginAttempts.TryGetValue(key, out state)) LoginAttempts[key] = state = new LoginAttemptState();
        }
        state.Failures = Math.Min(state.Failures + 1, 10);
        if (state.Failures >= 3)
        {
            int seconds = Math.Min(30, 1 << Math.Min(state.Failures - 3, 5));
            state.BlockedUntilUtc = DateTime.UtcNow.AddSeconds(seconds);
        }
    }

    internal static bool VerifyPassword(UserAccount user, string password)
    {
        if (user == null || string.IsNullOrWhiteSpace(user.PasswordSalt) || string.IsNullOrWhiteSpace(user.PasswordHash)) return false;
        try
        {
            var salt = Convert.FromBase64String(user.PasswordSalt);
            var expected = Convert.FromBase64String(user.PasswordHash);
            if (salt.Length < SaltSize || expected.Length != HashSize) return false;
            int iterations = GetIterations(user);
            if (iterations < LegacyIterations || iterations > CurrentIterations) return false;
            int version = user.PasswordHashVersion;
            if (version != LegacyHashVersion && version != CurrentHashVersion) return false;
            var actual = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password ?? ""), salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch { return false; }
    }

    private static int GetIterations(UserAccount user) => user.PasswordIterations <= 0 ? LegacyIterations : user.PasswordIterations;

    public static void ValidatePassword(string password, string fieldName = "Пароль")
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrWhiteSpace(password))
            throw new ArgumentException($"{fieldName}: пароль не должен быть пустым или состоять только из пробелов.");
        if (password.Length < MinimumPasswordLength)
            throw new ArgumentException($"{fieldName}: используйте не менее {MinimumPasswordLength} символов.");
        if (password.Length > MaximumPasswordLength)
            throw new ArgumentException($"{fieldName}: максимальная длина — {MaximumPasswordLength} символов.");
    }

    public static bool IsSuperAdmin => HasValidAdministrativeSession();
    public static bool IsAdministrator => IsSuperAdmin;

    internal static bool HasValidAdministrativeSession(UserAccount currentUser, IReadOnlyCollection<UserAccount> accounts, bool needsInitialSetup)
    {
        if (needsInitialSetup || currentUser is null || !currentUser.Enabled ||
            !string.Equals(currentUser.UserName, "admin", StringComparison.OrdinalIgnoreCase)) return false;
        var stored = accounts?.FirstOrDefault(u => u is not null && u.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase));
        return stored is not null && stored.Enabled && ReferenceEquals(stored, currentUser);
    }

    private static bool HasValidAdministrativeSession() =>
        HasValidAdministrativeSession(CurrentUser, _document?.Users, NeedsInitialSetup);

    private static void RequireAdministrativeSession() =>
        EnsureAdministrativeSession(CurrentUser, _document?.Users, NeedsInitialSetup);

    internal static void EnsureAdministrativeSession(UserAccount currentUser, IReadOnlyCollection<UserAccount> accounts, bool needsInitialSetup)
    {
        if (!HasValidAdministrativeSession(currentUser, accounts, needsInitialSetup))
            throw new UnauthorizedAccessException("Административная сессия недействительна. Выполните повторный вход под активной учётной записью администратора.");
    }

    internal static void MigrateMode4Permissions(UserStoreDocument document, int loadedSchemaVersion)
    {
        if (document is null || loadedSchemaVersion >= 6) return;
        foreach (var role in document.Roles ?? new List<RoleDefinition>())
        {
            role.Permissions ??= new List<string>();
            bool hadAnyModeAccess = role.Permissions.Any(p => new[]
            {
                nameof(Permission.Mode1Show), nameof(Permission.Mode1View), nameof(Permission.Mode1Add),
                nameof(Permission.Mode1Edit), nameof(Permission.Mode1Delete), nameof(Permission.ViewData),
                nameof(Permission.AddData), nameof(Permission.EditData), nameof(Permission.DeleteData),
                nameof(Permission.Mode2Show), nameof(Permission.Mode2View), nameof(Permission.Mode2Manage),
                nameof(Permission.ManageClasses), nameof(Permission.ManageRelations),
                nameof(Permission.Mode3Show), nameof(Permission.Mode3View), nameof(Permission.Mode3Manage),
                nameof(Permission.ManageObjects)
            }.Contains(p, StringComparer.OrdinalIgnoreCase));
            if (hadAnyModeAccess && !role.Permissions.Contains(nameof(Permission.Mode4Show), StringComparer.OrdinalIgnoreCase))
                role.Permissions.Add(nameof(Permission.Mode4Show));
            // Preserve explicit Mode4View/Mode4Manage values from the source role, but never
            // synthesize them. Roles without any prior mode access remain without Mode 4 access.
        }
    }
    public static bool Has(Permission permission) => PermissionPolicy.Has(CurrentUser, CurrentRole, permission);
    public static bool Require(Permission permission, bool showMessage = true)
    {
        if (Has(permission)) return true;
        if (showMessage) Configurator.Infrastructure.ModernDialog.Show($"У пользователя «{CurrentUser?.DisplayName}» нет права: {PermissionDisplay(permission)}.", "Доступ запрещён", MessageBoxButton.OK, MessageBoxImage.Warning);
        return false;
    }

    public static string PermissionDisplay(Permission p) => PermissionCatalog.Get(p).Title;

    public static IReadOnlyList<UserAccount> Users => _document.Users;
    public static IReadOnlyList<RoleDefinition> Roles => _document.Roles;
    public static bool IsBuiltInUser(string name) => string.Equals(name, "admin", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "Инженер-ПНР", StringComparison.OrdinalIgnoreCase);

    public static void SaveUser(string originalName, string userName, string displayName, string role, bool enabled, string newPassword)
    {
        RequireAdministrativeSession();
        string beforeMutation = JsonConvert.SerializeObject(_document);
        string sessionUserName = CurrentUser?.UserName;
        userName = (userName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(userName)) throw new ArgumentException("Укажите логин пользователя.");
        if (_document.Users.Any(u => !u.UserName.Equals(originalName ?? "", StringComparison.OrdinalIgnoreCase) && u.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("Такой пользователь уже существует.");
        bool creating = string.IsNullOrWhiteSpace(originalName);
        var user = creating
            ? null
            : _document.Users.FirstOrDefault(u => u.UserName.Equals(originalName, StringComparison.OrdinalIgnoreCase));
        if (!creating && user == null) throw new InvalidOperationException("Пользователь не найден.");
        if (!creating && IsBuiltInUser(originalName) && !userName.Equals(originalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Логины системных пользователей нельзя изменять.");
        if (creating)
        {
            if (userName.Equals("admin", StringComparison.OrdinalIgnoreCase) || userName.Equals("Инженер-ПНР", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Этот логин зарезервирован для системной учётной записи.");
            user = new UserAccount { UserName = userName, DisplayName = displayName?.Trim() ?? userName, Enabled = false };
        }

        bool isAdmin = user.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase);
        string requestedRole = isAdmin ? "Администратор" : (role ?? "Проектировщик").Trim();
        if (!_document.Roles.Any(r => r.Name.Equals(requestedRole, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Выбранная роль не существует. Обновите список ролей и повторите попытку.");
        if (!isAdmin && requestedRole.Equals("Администратор", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Роль «Администратор» зарезервирована для системной учётной записи admin.");

        bool wasEnabled = user.Enabled;
        EnsureAdministratorRemainsEnabled(isAdmin, enabled);
        if (creating && string.IsNullOrEmpty(newPassword))
            throw new InvalidOperationException("Для нового пользователя задайте пароль длиной не менее 5 символов.");
        if (!wasEnabled && enabled && string.IsNullOrEmpty(newPassword))
            throw new InvalidOperationException("Перед включением пользователя задайте новый пароль.");
        if (!string.IsNullOrEmpty(newPassword)) ValidatePassword(newPassword);
        if (string.IsNullOrEmpty(newPassword) && (string.IsNullOrWhiteSpace(user.PasswordSalt) || string.IsNullOrWhiteSpace(user.PasswordHash)))
            throw new InvalidOperationException("Для учётной записи необходимо задать пароль перед включением.");

        if (creating) _document.Users.Add(user);
        user.DisplayName = isAdmin ? "Администратор" : (string.IsNullOrWhiteSpace(displayName) ? user.UserName : displayName.Trim());
        user.RoleName = requestedRole;
        user.Enabled = enabled;
        if (!string.IsNullOrEmpty(newPassword)) SetPassword(user, newPassword);
        try { Save(); }
        catch
        {
            RestoreDocumentAfterFailedMutation(beforeMutation, sessionUserName);
            throw;
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static void EnsureAdministratorRemainsEnabled(bool isAdministratorAccount, bool enabled)
    {
        if (isAdministratorAccount && !enabled)
            throw new InvalidOperationException("Нельзя отключить последнюю административную учётную запись. Для восстановления доступа используйте процедуру восстановления администратора.");
    }

    internal static void SetPassword(UserAccount user, string password)
    {
        ValidatePassword(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, CurrentIterations, HashAlgorithmName.SHA256, HashSize);
        user.PasswordSalt = Convert.ToBase64String(salt);
        user.PasswordHash = Convert.ToBase64String(hash);
        user.PasswordHashVersion = CurrentHashVersion;
        user.PasswordIterations = CurrentIterations;
        CryptographicOperations.ZeroMemory(hash);
    }

    public static void DeleteUser(string userName)
    {
        RequireAdministrativeSession();
        string beforeMutation = JsonConvert.SerializeObject(_document);
        string sessionUserName = CurrentUser?.UserName;
        var user = _document.Users.FirstOrDefault(u => u.UserName.Equals(userName ?? "", StringComparison.OrdinalIgnoreCase));
        if (user == null) throw new InvalidOperationException("Пользователь не найден.");
        if (IsBuiltInUser(user.UserName)) throw new InvalidOperationException("Системные учётные записи Администратор и Инженер-ПНР нельзя удалить.");
        if (CurrentUser != null && CurrentUser.UserName.Equals(user.UserName, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Нельзя удалить текущую учётную запись.");
        _document.Users.Remove(user);
        try { Save(); }
        catch
        {
            RestoreDocumentAfterFailedMutation(beforeMutation, sessionUserName);
            throw;
        }
    }

    public static void SaveRole(string originalName, string name, string description, IEnumerable<Permission> permissions)
    {
        RequireAdministrativeSession();
        string beforeMutation = JsonConvert.SerializeObject(_document);
        string sessionUserName = CurrentUser?.UserName;
        name = (name ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("Укажите название роли.");
        var selectedPermissions = (permissions ?? Enumerable.Empty<Permission>()).Select(x => x.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var role = string.IsNullOrWhiteSpace(originalName)
            ? null
            : _document.Roles.FirstOrDefault(r => r.Name.Equals(originalName, StringComparison.OrdinalIgnoreCase));

        if (role == null && !string.IsNullOrWhiteSpace(originalName))
            throw new InvalidOperationException("Роль не найдена. Обновите список и повторите попытку.");
        if (_document.Roles.Any(r => !ReferenceEquals(r, role) && r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Роль с таким названием уже существует.");

        if (role != null && role.BuiltIn && !role.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Название системной роли нельзя менять, но её права можно настраивать.");
        if (name.Equals("Администратор", StringComparison.OrdinalIgnoreCase) && (role == null || !role.BuiltIn))
            throw new InvalidOperationException("Название «Администратор» зарезервировано для системной роли.");

        if (role == null)
        {
            role = new RoleDefinition { Name = name, BuiltIn = false };
            _document.Roles.Add(role);
        }
        else if (!role.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            string oldName = role.Name;
            foreach (var user in _document.Users.Where(u => u.RoleName.Equals(oldName, StringComparison.OrdinalIgnoreCase)))
                user.RoleName = name;
        }
        role.Name = name;
        role.Description = description ?? "";
        role.Permissions = selectedPermissions;
        try { Save(); }
        catch
        {
            RestoreDocumentAfterFailedMutation(beforeMutation, sessionUserName);
            throw;
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void DeleteRole(string name)
    {
        RequireAdministrativeSession();
        string beforeMutation = JsonConvert.SerializeObject(_document);
        string sessionUserName = CurrentUser?.UserName;
        var role = _document.Roles.FirstOrDefault(r => r.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase));
        if (role == null) return;
        if (role.BuiltIn) throw new InvalidOperationException("Системные роли «Администратор» и «Проектировщик» нельзя удалить.");
        if (_document.Users.Any(u => u.RoleName.Equals(role.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Эта роль назначена пользователю. Сначала назначьте ему другую роль.");
        _document.Roles.Remove(role);
        try { Save(); }
        catch
        {
            RestoreDocumentAfterFailedMutation(beforeMutation, sessionUserName);
            throw;
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static void RestoreDocumentAfterFailedMutation(string serializedSnapshot, string sessionUserName)
    {
        _document = JsonConvert.DeserializeObject<UserStoreDocument>(serializedSnapshot)
            ?? throw new InvalidDataException("Не удалось восстановить состояние хранилища пользователей после ошибки записи.");
        if (!string.IsNullOrWhiteSpace(sessionUserName))
            CurrentUser = _document.Users.FirstOrDefault(u => u.UserName.Equals(sessionUserName, StringComparison.OrdinalIgnoreCase));
    }

    public static void InitializeIfNeeded() { if (_document == null) Initialize(); }


    /// <summary>Exports the complete user/role store, including password hashes required for account portability.</summary>
    public static void ExportSettings(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Не указан путь для экспорта.", nameof(destinationPath));
        lock (Sync)
        {
            InitializeIfNeeded();
            string json = JsonConvert.SerializeObject(_document, Formatting.Indented);
            string fullPath = Path.GetFullPath(destinationPath);
            string directory = Path.GetDirectoryName(fullPath) ?? throw new IOException("Не удалось определить папку для экспорта.");
            Directory.CreateDirectory(directory);
            string tempPath = Path.Combine(directory, $"{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                if (File.Exists(fullPath)) File.Replace(tempPath, fullPath, null, ignoreMetadataErrors: true);
                else File.Move(tempPath, fullPath);
            }
            catch (Exception ex)
            {
                throw new IOException("Не удалось экспортировать настройки пользователей. Проверьте доступ к выбранной папке.", ex);
            }
            finally { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }
        }
    }

    /// <summary>Validates a complete portable user store and replaces the current store transactionally.</summary>
    public static string ImportSettings(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Не указан файл для импорта.", nameof(sourcePath));
        lock (Sync)
        {
            InitializeIfNeeded();
            UserStoreDocument imported;
            try { imported = DeserializeDocument(File.ReadAllText(sourcePath)); }
            catch (Exception ex) { throw new InvalidDataException("Файл настроек пользователей повреждён или имеет неподдерживаемый формат. Текущие настройки не изменены.", ex); }

            ValidateImportedDocument(imported);
            imported.SchemaVersion = Math.Max(imported.SchemaVersion, 7);
            string json = JsonConvert.SerializeObject(imported, Formatting.Indented);
            string backupPath = null;
            if (File.Exists(FilePath))
            {
                backupPath = Path.Combine(DirectoryPath, $"users.before-import.{DateTime.Now:yyyyMMdd_HHmmss_fff}.json");
                File.Copy(FilePath, backupPath, overwrite: false);
            }

            // Persist first. The in-memory authorization state is swapped only after the file write succeeds.
            WriteAtomically(FilePath, json);
            _document = imported;
            _isNewInstallation = false;
            NeedsInitialSetup = false;
            var previousUserName = CurrentUser?.UserName;
            CurrentUser = string.IsNullOrWhiteSpace(previousUserName)
                ? null
                : _document.Users.FirstOrDefault(u => u.Enabled && u.UserName.Equals(previousUserName, StringComparison.OrdinalIgnoreCase));
            Changed?.Invoke(null, EventArgs.Empty);
            return backupPath;
        }
    }

    private static void ValidateImportedDocument(UserStoreDocument document)
    {
        if (document.Users == null || document.Roles == null || document.Roles.Count == 0)
            throw new InvalidDataException("В файле должны присутствовать разделы пользователей и ролей. Текущие настройки не изменены.");
        if (document.Roles.Any(r => r == null || string.IsNullOrWhiteSpace(r.Name) || r.Permissions == null
                || r.Permissions.Any(p => string.IsNullOrWhiteSpace(p) || !Enum.TryParse<Permission>(p, true, out _)))
            || document.Roles.Select(r => r.Name.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Roles.Count)
            throw new InvalidDataException("Файл содержит пустые, повторяющиеся роли или неизвестные права доступа. Текущие настройки не изменены.");
        if (document.Users.Any(u => u == null || string.IsNullOrWhiteSpace(u.UserName) || string.IsNullOrWhiteSpace(u.RoleName))
            || document.Users.Select(u => u.UserName.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != document.Users.Count)
            throw new InvalidDataException("Файл содержит пустые или повторяющиеся учётные записи. Текущие настройки не изменены.");

        var roleNames = document.Roles.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var user in document.Users)
        {
            if (!roleNames.Contains(user.RoleName))
                throw new InvalidDataException($"Для пользователя '{user.UserName}' не найдена роль '{user.RoleName}'. Текущие настройки не изменены.");
            if (!HasValidCredentialEncoding(user))
                throw new InvalidDataException($"Данные пароля пользователя '{user.UserName}' имеют неверный формат. Текущие настройки не изменены.");
        }

        var admin = document.Users.FirstOrDefault(u => u.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase));
        if (admin == null || !admin.Enabled || !HasValidCredentialEncoding(admin))
            throw new InvalidDataException("Импорт отменён: файл должен содержать включённую учётную запись admin с корректными данными пароля, чтобы не потерять административный доступ.");
        if (!roleNames.Contains("Администратор"))
            throw new InvalidDataException("Импорт отменён: в файле отсутствует роль «Администратор».");
    }

    private static void Save()
    {
        // Serialize completely before touching the current file; credentials are never logged.
        string json = JsonConvert.SerializeObject(_document, Formatting.Indented);
        WriteAtomically(FilePath, json);
    }

    internal static void WriteAtomically(string destinationPath, string content)
    {
        string fullPath = Path.GetFullPath(destinationPath);
        string directory = Path.GetDirectoryName(fullPath) ?? throw new IOException("Не удалось определить каталог хранилища.");
        Directory.CreateDirectory(directory);
        string tempPath = Path.Combine(directory, $"users.{Guid.NewGuid():N}.tmp");
        string backupPath = Path.Combine(directory, $"users.{Guid.NewGuid():N}.bak");
        bool backupCreated = false;
        byte[] bytes = new UTF8Encoding(false).GetBytes(content);
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            if (!File.Exists(fullPath))
            {
                File.Move(tempPath, fullPath);
            }
            else
            {
                try
                {
                    File.Replace(tempPath, fullPath, backupPath, ignoreMetadataErrors: true);
                    backupCreated = true;
                }
                catch (PlatformNotSupportedException)
                {
                    ReplaceWithRecoverableMove(tempPath, fullPath, backupPath);
                    backupCreated = true;
                }
                catch (IOException) when (File.Exists(fullPath) && File.Exists(tempPath))
                {
                    ReplaceWithRecoverableMove(tempPath, fullPath, backupPath);
                    backupCreated = true;
                }
            }
        }
        catch (Exception ex)
        {
            throw new IOException("Не удалось безопасно сохранить хранилище учётных записей. Предыдущий users.json сохранён; изменения не подтверждены.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            if (backupCreated) { try { if (File.Exists(backupPath)) File.Delete(backupPath); } catch { } }
        }
    }

    private static void ReplaceWithRecoverableMove(string tempPath, string fullPath, string backupPath)
    {
        if (File.Exists(backupPath)) File.Delete(backupPath);
        File.Move(fullPath, backupPath);
        try
        {
            File.Move(tempPath, fullPath);
        }
        catch
        {
            if (!File.Exists(fullPath) && File.Exists(backupPath)) File.Move(backupPath, fullPath);
            throw;
        }
    }
}
