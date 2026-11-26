#nullable enable
using System.Text.Json.Nodes;
using Configurator.Themes;

namespace Configurator.Core.Projects;

/// <summary>Portable project metadata. Live connection strings and plaintext credentials must never be stored here.</summary>
public sealed class ConfiguratorProject
{
    public ConfiguratorProjectManifest Manifest { get; set; } = new();
    public ProjectSettings Settings { get; set; } = new();
    public List<ProjectMapping> Mappings { get; set; } = new();
    public JsonObject Architecture { get; set; } = new();
    /// <summary>Portable graph rules and manual/suppressed relations. Stored inside the project, never in user profile files.</summary>
    public JsonObject GraphRelations { get; set; } = new();
    public JsonObject Filters { get; set; } = new();
    public List<ProjectTextList> TextLists { get; set; } = new();
    public List<ThemePreset> ThemePresets { get; set; } = new();
    /// <summary>Project-scoped accounts. Passwords are represented only as salted password hashes.</summary>
    public List<ProjectUserAccount> Users { get; set; } = new();
    public List<ProjectRecord> LocalRecords { get; set; } = new();
    public List<ProjectRecord> BaselineRecords { get; set; } = new();
    public List<ProjectTableSchema> TableSchemas { get; set; } = new();
}

public sealed class ProjectUserAccount
{
    public string UserName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string RoleName { get; set; } = "Проектировщик";
    public string PasswordHash { get; set; } = string.Empty;
    public string PasswordSalt { get; set; } = string.Empty;
    public int PasswordHashVersion { get; set; } = 1;
    public int PasswordIterations { get; set; } = 600_000;
    public bool Enabled { get; set; } = true;

    public override string ToString() => $"{(string.IsNullOrWhiteSpace(DisplayName) ? UserName : DisplayName)} · {UserName}";
}

public sealed class ConfiguratorProjectManifest
{
    public string Format { get; set; } = "ConfiguratorProject";
    public int FormatVersion { get; set; } = 1;
    public string ProjectId { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = "Новый проект";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ModifiedUtc { get; set; } = DateTimeOffset.UtcNow;
    public string ConfiguratorVersion { get; set; } = string.Empty;
}

public sealed class ProjectSettings
{
    public string DisplayName { get; set; } = "Новый проект";
    public bool UnifiedLanguage { get; set; }
    public string PreferredMode { get; set; } = "Online";
    public string DatabaseProvider { get; set; } = "SqlServer";
    public string DatabaseLabel { get; set; } = string.Empty;
    public bool RequirePreviewBeforeOnlineWrite { get; set; } = true;
    public bool AllowOnlineWrites { get; set; } = false;
}

public sealed class ProjectMapping
{
    public string EntityName { get; set; } = string.Empty;
    public string TableName { get; set; } = string.Empty;
    public string KeyColumn { get; set; } = string.Empty;
    public List<string> KeyColumns { get; set; } = new();
    public bool HasStableKey { get; set; }
    public Dictionary<string, string> FieldMappings { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool ReadEnabled { get; set; } = true;
    public bool WriteEnabled { get; set; } = false;
}

public sealed class ProjectTextList
{
    public string Name { get; set; } = string.Empty;
    public string SourceTable { get; set; } = string.Empty;
    public string KeyColumn { get; set; } = string.Empty;
    public string DisplayColumn { get; set; } = string.Empty;
    public DateTimeOffset? ImportedUtc { get; set; }
    public List<ProjectTextListItem> Items { get; set; } = new();
}

public sealed class ProjectTextListItem
{
    public string Key { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

/// <summary>Portable record representation used by snapshots and the first project format.</summary>
public sealed class ProjectRecord
{
    public string TableName { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public JsonObject Values { get; set; } = new();
    public string? RowVersion { get; set; }
}

/// <summary>Database schema captured with a snapshot; contains no credentials or connection string.</summary>
public sealed class ProjectTableSchema
{
    public string TableName { get; set; } = string.Empty;
    public List<string> KeyColumns { get; set; } = new();
    public bool HasStableKey { get; set; }
    public List<ProjectColumnSchema> Columns { get; set; } = new();
}

public sealed class ProjectColumnSchema
{
    public string Name { get; set; } = string.Empty;
    public string DataType { get; set; } = string.Empty;
    public bool IsNullable { get; set; }
    public bool IsIdentity { get; set; }
    public bool IsComputed { get; set; }
    public int MaxLength { get; set; }
    public byte Precision { get; set; }
    public byte Scale { get; set; }
    public bool HasDefault { get; set; }
    public bool IsReadOnly { get; set; }
}
