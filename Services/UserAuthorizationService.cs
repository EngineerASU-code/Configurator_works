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
    private const string FixedAdminSalt = "VbwGHwR9sLxOtK6B+wrUig==";
    private const string FixedAdminHash = "QYx6esuZDl8WHNpEicNTVoWMkyp6y1uuCIEsHGlGT5o=";
    private const string ProtectedStorePrefix = "DPAPI:";
    private const int MinimumPasswordLength = 5;
    private const int MaximumPasswordLength = 1024;
    private static readonly Dictionary<string, LoginAttemptState> LoginAttempts = new(StringComparer.OrdinalIgnoreCase);
    private sealed class LoginAttemptState { public int Failures; public DateTime BlockedUntilUtc; }
    public static UserAccount CurrentUser { get; private set; }
    public static RoleDefinition CurrentRole => CurrentUser == null ? null : _document?.Roles.FirstOrDefault(r => r.Name.Equals(CurrentUser.RoleName, StringComparison.OrdinalIgnoreCase));
    public static event EventHandler Changed;

    public static bool NeedsInitialSetup { get; private set; }
    private static readonly string[] EngineerNames = { "Инженер" };
    public static bool HasAdministratorAccount => _document?.Users?.Any(u => u.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase)) == true;

    public static void Initialize()
    {
        lock (Sync)
        {
            _document = LoadStore(FilePath, out bool needsSetup);
            NeedsInitialSetup = needsSetup;
        }
    }

    internal static UserStoreDocument LoadStore(string filePath, out bool needsSetup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        UserStoreDocument document;
        bool storeExists = File.Exists(filePath);
        bool protectedStore = false;
        if (storeExists)
        {
            try
            {
                string content = File.ReadAllText(filePath);
                protectedStore = content.StartsWith(ProtectedStorePrefix, StringComparison.Ordinal);
                if (protectedStore)
                {
                    byte[] encrypted = Convert.FromBase64String(content[ProtectedStorePrefix.Length..]);
                    content = Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser));
                }
                document = DeserializeDocument(content);
                if (document == null || document.Users == null || document.Roles == null)
                    throw new InvalidDataException("Хранилище пользователей не содержит обязательные разделы.");
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Не удалось прочитать хранилище учётных записей '{filePath}'. Оно не было перезаписано. Восстановите файл из резервной копии или обратитесь к администратору.", ex);
            }
        }
        else
        {
            document = new UserStoreDocument
            {
                Users = { new UserAccount
                {
                    UserName = "admin", DisplayName = "Администратор", RoleName = "Администратор",
                    PasswordHashVersion = 1, PasswordIterations = 600_000,
                    PasswordSalt = FixedAdminSalt,
                    PasswordHash = FixedAdminHash
                } }
            };
        }

        document.Users ??= new List<UserAccount>();
        document.Roles ??= new List<RoleDefinition>();
        foreach (var role in document.Roles)
        {
            if (role == null || string.IsNullOrWhiteSpace(role.Name))
                throw new InvalidDataException("Хранилище пользователей содержит роль без имени. Файл не был перезаписан.");
            role.Permissions ??= new List<string>();
        }
        if (document.Users.Any(u => u == null || string.IsNullOrWhiteSpace(u.UserName)))
            throw new InvalidDataException("Хранилище пользователей содержит некорректную учётную запись. Файл не был перезаписан.");

        var admin = document.Users.FirstOrDefault(u => u.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase));
        if (admin == null)
            throw new InvalidDataException("В хранилище отсутствует учётная запись администратора. Файл не перезаписан; восстановите резервную копию users.json.");
        if (!HasValidCredentialEncoding(admin))
            throw new InvalidDataException("Данные пароля административной учётной записи повреждены. Файл не перезаписан; восстановите резервную копию users.json.");
        needsSetup = false;
        string beforeNormalization = JsonConvert.SerializeObject(document, Formatting.Indented);
        NormalizeAccounts(document);
        string normalized = JsonConvert.SerializeObject(document, Formatting.Indented);
        if (!protectedStore || beforeNormalization != normalized)
            WriteProtectedStore(filePath, normalized);
        return document;
    }

    internal static void AddEngineerProfiles(UserStoreDocument document)
    {
        var permissions = new[]
        {
            Permission.Mode1Show, Permission.Mode1View, Permission.Mode1Add, Permission.Mode1Edit, Permission.Mode1Delete,
            Permission.Mode3Show, Permission.Mode3View, Permission.Mode3Manage,
            Permission.Mode4Show, Permission.Mode4View, Permission.Mode4Manage,
            Permission.ViewData, Permission.AddData, Permission.EditData, Permission.DeleteData, Permission.ManageObjects,
            Permission.ManageTextLists, Permission.ManageDatabases, Permission.CheckTools, Permission.ExportData,
            Permission.FreeSql, Permission.RestoreJournal
        };
        foreach (string name in EngineerNames)
        {
            var user = document.Users.FirstOrDefault(u => u.UserName.Equals(name, StringComparison.OrdinalIgnoreCase));
            var oldRole = document.Roles.FirstOrDefault(r => r.Name.Equals(user?.RoleName, StringComparison.OrdinalIgnoreCase));
            bool hasSharedRole = oldRole?.BuiltIn == true && (oldRole.Name == "Инженер" || oldRole.Name.StartsWith("Инженер (", StringComparison.Ordinal));
            var role = document.Roles.FirstOrDefault(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (role == null)
            {
                role = new RoleDefinition
                {
                    Name = name, BuiltIn = true,
                    Permissions = hasSharedRole ? oldRole.Permissions.ToList() : permissions.Select(p => p.ToString()).ToList()
                };
                document.Roles.Add(role);
            }

            if (user == null)
                document.Users.Add(new UserAccount { UserName = name, DisplayName = name, RoleName = role.Name, Enabled = false });
            else if (hasSharedRole && role.BuiltIn)
                user.RoleName = role.Name;
        }
    }

    internal static void NormalizeAccounts(UserStoreDocument document)
    {
        var admin = document.Users.FirstOrDefault(u => u.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidDataException("В хранилище отсутствует учётная запись администратора; файл не изменён.");
        var engineer = document.Users.FirstOrDefault(u => u.UserName.Equals("Инженер", StringComparison.OrdinalIgnoreCase));
        engineer ??= new UserAccount { UserName = "Инженер" };
        engineer.PasswordHash = engineer.PasswordSalt = string.Empty;
        engineer.PasswordHashVersion = engineer.PasswordIterations = 0;
        engineer.DisplayName = engineer.RoleName = "Инженер";
        engineer.Enabled = true;
        admin.Enabled = true;
        admin.PasswordSalt = FixedAdminSalt;
        admin.PasswordHash = FixedAdminHash;
        admin.PasswordIterations = CurrentIterations;
        admin.PasswordHashVersion = CurrentHashVersion;
        admin.DisplayName = admin.RoleName = "Администратор";
        var engineerPermissions = Enum.GetValues<Permission>().Where(p => p is not
            (Permission.Mode2Show or Permission.Mode2View or Permission.Mode2Manage or Permission.Mode2GraphShow or
             Permission.Mode2RelationsShow or Permission.Mode2FieldsShow or Permission.ManageClasses or
             Permission.ManageRelations or Permission.ManageUsers)).Select(p => p.ToString()).ToList();
        document.Users = new List<UserAccount> { engineer, admin };
        document.Roles = new List<RoleDefinition>
        {
            new() { Name = "Инженер", BuiltIn = true, Permissions = engineerPermissions },
            new() { Name = "Администратор", BuiltIn = true, Permissions = Enum.GetValues<Permission>().Select(p => p.ToString()).ToList() }
        };
        document.SchemaVersion = 10;
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

    public static void CompleteInitialSetup(string currentAdminPassword, string newAdminPassword) =>
        throw new InvalidOperationException("Пароль системного администратора фиксирован.");

    private static void CreateUserInternal(string name, string display, string role, string password, bool enabled)
    {
        ValidatePassword(password, "Пароль");
        var user = new UserAccount { UserName = name.Trim(), DisplayName = string.IsNullOrWhiteSpace(display) ? name.Trim() : display.Trim(), RoleName = role, Enabled = enabled };
        SetPassword(user, password);
        _document.Users.Add(user);
    }

    [Obsolete("Автоматический вход отключён. Используйте явную авторизацию.")]
    public static void StartAsDefaultUser() => throw new InvalidOperationException("Автоматический вход отключён. Требуется явная авторизация.");

    public static void Logout()
    {
        CurrentUser = null;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    internal static bool RequiresPassword(UserAccount user) =>
        !string.Equals(user?.UserName, "Инженер", StringComparison.OrdinalIgnoreCase);

    public static bool Login(string userName, string password, out string error)
    {
        error = null;
        InitializeIfNeeded();
        if (NeedsInitialSetup) { error = "Сначала завершите настройку пароля администратора."; return false; }
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
            var user = _document.Users.FirstOrDefault(u => IsBuiltInUser(u.UserName) && u.UserName.Equals(key, StringComparison.OrdinalIgnoreCase));
            if (user == null || !user.Enabled || (RequiresPassword(user) && (string.IsNullOrEmpty(password) || !VerifyPassword(user, password))))
            {
                RegisterFailedLogin(key);
                error = "Неверный логин/пароль или учётная запись отключена.";
                return false;
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

    internal static void ValidateAdministratorPassword(string password)
    {
        ValidatePassword(password, "Новый пароль администратора");
        if (password.Length < 12)
            throw new ArgumentException("Новый пароль администратора: используйте не менее 12 символов.");
    }

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

    public static IReadOnlyList<UserAccount> Users => _document.Users.Where(u => IsBuiltInUser(u.UserName)).ToList();
    public static IReadOnlyList<RoleDefinition> Roles => _document.Roles;
    public static bool IsBuiltInUser(string name) => string.Equals(name, "admin", StringComparison.OrdinalIgnoreCase) || EngineerNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    internal static bool IsCommissioningEngineer(string name) => string.Equals(name, "Инженер-ПНР", StringComparison.OrdinalIgnoreCase);

    internal static bool EnsureCommissioningRolePermissions(RoleDefinition role)
    {
        bool changed = !role.BuiltIn;
        role.BuiltIn = true;
        foreach (var permission in new[]
        {
            Permission.Mode3Show, Permission.Mode3View, Permission.Mode3Manage,
            Permission.Mode4Show, Permission.Mode4View, Permission.Mode4Manage,
            Permission.ManageObjects, Permission.ViewData, Permission.AddData,
            Permission.EditData, Permission.DeleteData
        })
        {
            if (role.Permissions.Contains(permission.ToString(), StringComparer.OrdinalIgnoreCase)) continue;
            role.Permissions.Add(permission.ToString());
            changed = true;
        }
        return changed;
    }
    public static void SaveUser(string originalName, string userName, string displayName, string role, bool enabled, string newPassword)
    {
        RequireAdministrativeSession();
        string beforeMutation = JsonConvert.SerializeObject(_document);
        string sessionUserName = CurrentUser?.UserName;
        userName = (userName ?? "").Trim();
        if (!IsBuiltInUser(originalName) || !userName.Equals(originalName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Доступно редактирование только существующих профилей администратора и инженера.");
        var user = _document.Users.FirstOrDefault(u => u.UserName.Equals(originalName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("Пользователь не найден.");

        bool isAdmin = user.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase);
        string requestedRole = isAdmin ? "Администратор" : "Инженер";
        if (!enabled) throw new InvalidOperationException("Системную учётную запись нельзя отключить.");
        if (!_document.Roles.Any(r => r.Name.Equals(requestedRole, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Выбранная роль не существует. Обновите список ролей и повторите попытку.");
        if (!isAdmin && requestedRole.Equals("Администратор", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Роль «Администратор» зарезервирована для системной учётной записи admin.");

        bool wasEnabled = user.Enabled;
        EnsureAdministratorRemainsEnabled(isAdmin, enabled);
        if (!wasEnabled && enabled && string.IsNullOrEmpty(newPassword))
            throw new InvalidOperationException("Перед включением пользователя задайте новый пароль.");
        if (!string.IsNullOrEmpty(newPassword))
        {
            EnsurePasswordCanChange(user);
            ValidatePassword(newPassword);
        }
        if (enabled && string.IsNullOrEmpty(newPassword) && (string.IsNullOrWhiteSpace(user.PasswordSalt) || string.IsNullOrWhiteSpace(user.PasswordHash)))
            throw new InvalidOperationException("Для учётной записи необходимо задать пароль перед включением.");

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

    private static void EnsurePasswordCanChange(UserAccount user)
    {
        if (string.Equals(user.UserName, "admin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Пароль системного администратора фиксирован.");
    }

    internal static void SetPassword(UserAccount user, string password)
    {
        EnsurePasswordCanChange(user);
        ValidatePassword(password);
        RehashVerifiedPassword(user, password);
    }

    // This path is only used after verification: legacy credentials may predate the current policy.
    internal static void RehashVerifiedPassword(UserAccount user, string password)
    {
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
        if (IsBuiltInUser(user.UserName)) throw new InvalidOperationException("Учётную запись администратора нельзя удалить.");
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
        throw new InvalidOperationException("Права системных ролей фиксированы.");
    }

    public static void DeleteRole(string name)
    {
        RequireAdministrativeSession();
        string beforeMutation = JsonConvert.SerializeObject(_document);
        string sessionUserName = CurrentUser?.UserName;
        var role = _document.Roles.FirstOrDefault(r => r.Name.Equals(name ?? "", StringComparison.OrdinalIgnoreCase));
        if (role == null) return;
        if (role.BuiltIn) throw new InvalidOperationException("Системные роли «Администратор» и «Инженер» нельзя удалить.");
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
        RequireAdministrativeSession();
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
        RequireAdministrativeSession();
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("Не указан файл для импорта.", nameof(sourcePath));
        lock (Sync)
        {
            InitializeIfNeeded();
            UserStoreDocument imported;
            try { imported = DeserializeDocument(File.ReadAllText(sourcePath)); }
            catch (Exception ex) { throw new InvalidDataException("Файл настроек пользователей повреждён или имеет неподдерживаемый формат. Текущие настройки не изменены.", ex); }

            ValidateImportedDocument(imported);
            var existingAdmin = _document.Users.First(u => u.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase));
            imported.Users.RemoveAll(u => u.UserName.Equals("admin", StringComparison.OrdinalIgnoreCase));
            imported.Users.Add(existingAdmin);
            NormalizeAccounts(imported);
            string json = JsonConvert.SerializeObject(imported, Formatting.Indented);
            string backupPath = null;
            if (File.Exists(FilePath))
            {
                backupPath = Path.Combine(DirectoryPath, $"users.before-import.{DateTime.Now:yyyyMMdd_HHmmss_fff}.json");
                File.Copy(FilePath, backupPath, overwrite: false);
            }

            // Persist first. The in-memory authorization state is swapped only after the file write succeeds.
            WriteProtectedStore(FilePath, json);
            _document = imported;
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
            bool awaitingPassword = (!user.Enabled || !RequiresPassword(user)) && string.IsNullOrEmpty(user.PasswordSalt) && string.IsNullOrEmpty(user.PasswordHash);
            if (!awaitingPassword && !HasValidCredentialEncoding(user))
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
        WriteProtectedStore(FilePath, json);
    }

    private static void WriteProtectedStore(string path, string json)
    {
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(json), null, DataProtectionScope.CurrentUser);
        WriteAtomically(path, ProtectedStorePrefix + Convert.ToBase64String(encrypted));
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
