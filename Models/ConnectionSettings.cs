using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;

namespace Configurator
{
    public class ConnectionSettings
    {
        public string Server { get; set; } = "";
        public string User { get; set; } = "";
        public string Password { get; set; } = "";
        public string Database { get; set; } = "";
        public List<string> KnownDatabases { get; set; } = new();
        public List<string> ConnectedDatabases { get; set; } = new();

        private static readonly string SettingsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Configurator");

        private static readonly string SettingsPath = Path.Combine(SettingsDir, "settings.json");

        public bool IsValid() =>
            !string.IsNullOrWhiteSpace(Server) &&
            (string.IsNullOrWhiteSpace(User) || !string.IsNullOrWhiteSpace(Password));

        public string ToConnectionString(string databaseName)
        {
            var localServer = LocalServerName(Server);
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = localServer == null ? Server : "lpc:" + localServer,
                InitialCatalog = databaseName,
                IntegratedSecurity = string.IsNullOrWhiteSpace(User),
                Encrypt = true,
                TrustServerCertificate = localServer != null,
                ConnectTimeout = 15
            };
            if (!builder.IntegratedSecurity) { builder.UserID = User; builder.Password = Password; }
            return builder.ConnectionString;
        }

        private static string LocalServerName(string server)
        {
            var name = server?.Trim() ?? "";
            if (name.StartsWith("lpc:", StringComparison.OrdinalIgnoreCase))
                name = name[4..];

            var machine = Environment.MachineName;
            if (name.Equals(machine, StringComparison.OrdinalIgnoreCase)) return machine;
            if (!name.StartsWith(machine + "\\", StringComparison.OrdinalIgnoreCase)) return null;

            var instance = name[(machine.Length + 1)..];
            return instance.Length is > 0 and <= 16 &&
                instance.All(c => char.IsLetterOrDigit(c) || c == '_')
                ? machine + "\\" + instance : null;
        }

        internal static string ProjectServerName(string server) =>
            LocalServerName(server) ?? (server?.Trim() ?? "");

        // === P1-11: Async-версия TestConnection ===
        public async Task<bool> TestConnectionAsync(string databaseName, System.Threading.CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(databaseName) || !IsValid()) return false;
            using var timeout = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                using var conn = Configurator.Core.Projects.SqlProviderGuard.CreateConnection(ToConnectionString(databaseName));
                await Configurator.Core.Projects.SqlProviderGuard.OpenConnectionAsync(conn, timeout.Token).ConfigureAwait(false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        public void RemoveDatabase(string dbName)
        {
            ConnectedDatabases.Remove(dbName);
            KnownDatabases.Remove(dbName);
        }

        // === P1-10: Пароль шифруется DPAPI, в JSON не хранится открыто ===
        public void SaveToFile()
        {
            try
            {
                if (!Directory.Exists(SettingsDir))
                    Directory.CreateDirectory(SettingsDir);

                var data = new PersistData
                {
                    Server = Server,
                    User = User,
                    // Шифруем пароль через DPAPI (CurrentUser scope)
                    EncryptedPassword = EncryptPassword(Password),
                    KnownDatabases = KnownDatabases
                };

                var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });

                // === P2-12: Атомарная запись через временный файл ===
                var tempPath = SettingsPath + ".tmp";
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, SettingsPath, overwrite: true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectionSettings] Save failed: {ex.Message}");
                // Не глотаем молча — пробрасываем, вызывающий код покажет пользователю
                throw new Exception($"Не удалось сохранить настройки: {ex.Message}");
            }
        }

        public void LoadFromFile()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;

                var json = File.ReadAllText(SettingsPath);
                var data = JsonSerializer.Deserialize<PersistData>(json);
                if (data == null) return;

                Server = data.Server ?? "";
                User = data.User ?? "";

                // Расшифровываем пароль
                Password = !string.IsNullOrEmpty(data.EncryptedPassword)
                    ? DecryptPassword(data.EncryptedPassword)
                    : "";

                // Миграция: если есть старое поле Password (plain text) — используем его
                if (string.IsNullOrEmpty(Password) && !string.IsNullOrEmpty(data.Password))
                    Password = data.Password;

                KnownDatabases = data.KnownDatabases ?? new List<string>();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[ConnectionSettings] Load failed: {ex.Message}");
                // Показываем пользователю, но не роняем приложение
                throw new Exception($"Не удалось загрузить настройки: {ex.Message}");
            }
        }

        // DPAPI шифрование
        private static string EncryptPassword(string plainText)
        {
            if (string.IsNullOrEmpty(plainText)) return "";
            byte[] plainBytes = Encoding.UTF8.GetBytes(plainText);
            byte[] encrypted = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(encrypted);
        }

        private static string DecryptPassword(string base64Encrypted)
        {
            try
            {
                byte[] encrypted = Convert.FromBase64String(base64Encrypted);
                byte[] decrypted = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decrypted);
            }
            catch
            {
                // Пароль не расшифровался (другой пользователь, повреждён) — возвращаем пустой
                return "";
            }
        }

        private class PersistData
        {
            public string Server { get; set; } = "";
            public string User { get; set; } = "";
            // Зашифрованный пароль (Base64, DPAPI)
            public string EncryptedPassword { get; set; } = "";
            // Устаревшее поле для обратной совместимости — будет удалено в будущих версиях
            public string Password { get; set; } = "";
            public List<string> KnownDatabases { get; set; } = new();
        }
    }
}
