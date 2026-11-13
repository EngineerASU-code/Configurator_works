#nullable enable
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Configurator.Core.Synchronization;

public enum ConfiguratorWorkMode { Online, Offline }
public enum SyncOperationKind { Insert, Update, Delete }
public enum SyncConflictKind { RemoteChanged, KeyCollision, MissingDependency, RemoteDelete, UnsupportedOperation }
public enum SyncPlanStatus { Draft, RequiresReview, Confirmed, Applying, Applied, Rejected, Failed, Stale }

public sealed class SyncConflict
{
    public string TableName { get; init; } = string.Empty;
    public string RecordKey { get; init; } = string.Empty;
    public SyncConflictKind Kind { get; init; }
    public string Message { get; init; } = string.Empty;
    public JsonObject BaselineValues { get; init; } = new();
    public JsonObject LocalValues { get; init; } = new();
    public JsonObject RemoteValues { get; init; } = new();
}

public sealed class SyncOperation
{
    public string TableName { get; init; } = string.Empty;
    public string RecordKey { get; init; } = string.Empty;
    public SyncOperationKind Kind { get; init; }
    public JsonObject? BaselineValues { get; init; }
    public JsonObject? LocalValues { get; init; }
    public JsonObject? RemoteValuesAtPreview { get; init; }
    public string? RemoteVersionAtPreview { get; init; }
    public bool IsAllowed { get; init; }
    public string? BlockReason { get; init; }
}

/// <summary>Previewed plan. Its digest is always derived from actual mutable content, never trusted from caller input.</summary>
public sealed class SyncPlan
{
    public Guid PlanId { get; init; } = Guid.NewGuid();
    public string ProjectId { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string RemoteIdentity { get; init; } = string.Empty;
    // Compatibility field only; validation compares it with a freshly calculated digest.
    public string ContentFingerprint { get; set; } = string.Empty;
    public string ConfirmedFingerprint { get; private set; } = string.Empty;
    public string ConfirmedBy { get; private set; } = string.Empty;
    public SyncPlanStatus Status { get; private set; } = SyncPlanStatus.Draft;
    public List<SyncOperation> Operations { get; init; } = new();
    public List<SyncConflict> Conflicts { get; init; } = new();
    public bool HasBlockedOperations => Operations.Any(x => !x.IsAllowed);
    public bool CanBeConfirmed => Status == SyncPlanStatus.RequiresReview && Operations.Count > 0 && !HasBlockedOperations && Conflicts.Count == 0;

    public string ComputeContentFingerprint()
    {
        var canonical = new JsonObject
        {
            ["planId"] = PlanId.ToString("D"), ["projectId"] = ProjectId, ["remoteIdentity"] = RemoteIdentity,
            ["createdUtc"] = CreatedUtc.ToUniversalTime().ToString("O"),
            ["operations"] = new JsonArray(Operations.Select(o => (JsonNode?)new JsonObject
            {
                ["table"] = o.TableName, ["key"] = o.RecordKey, ["kind"] = o.Kind.ToString(),
                ["baseline"] = Canonicalize(o.BaselineValues), ["local"] = Canonicalize(o.LocalValues),
                ["remote"] = Canonicalize(o.RemoteValuesAtPreview), ["remoteVersion"] = o.RemoteVersionAtPreview,
                ["allowed"] = o.IsAllowed, ["blockReason"] = o.BlockReason
            }).ToArray()),
            ["conflicts"] = new JsonArray(Conflicts.Select(c => (JsonNode?)new JsonObject
            {
                ["table"] = c.TableName, ["key"] = c.RecordKey, ["kind"] = c.Kind.ToString(), ["message"] = c.Message,
                ["baseline"] = Canonicalize(c.BaselineValues), ["local"] = Canonicalize(c.LocalValues), ["remote"] = Canonicalize(c.RemoteValues)
            }).ToArray())
        };
        byte[] bytes = Encoding.UTF8.GetBytes(canonical.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    public void MarkRequiresReview()
    {
        RequireStatus(SyncPlanStatus.Draft);
        Status = SyncPlanStatus.RequiresReview;
        ContentFingerprint = ComputeContentFingerprint();
    }

    public SyncConfirmation Confirm(string confirmedBy)
    {
        if (string.IsNullOrWhiteSpace(confirmedBy)) throw new InvalidOperationException("Не удалось определить пользователя, подтверждающего план.");
        if (!CanBeConfirmed) throw new InvalidOperationException("План содержит конфликты, заблокированные операции или не готов к подтверждению.");
        string current = ComputeContentFingerprint();
        if (!string.Equals(ContentFingerprint, current, StringComparison.OrdinalIgnoreCase)) { Status = SyncPlanStatus.Stale; throw new InvalidOperationException("Содержимое плана изменилось после preview. Требуется новый preview."); }
        ConfirmedFingerprint = current;
        ConfirmedBy = confirmedBy.Trim();
        Status = SyncPlanStatus.Confirmed;
        return new SyncConfirmation { PlanId = PlanId, ContentFingerprint = current, ConfirmedBy = ConfirmedBy };
    }

    public void BeginApply(SyncConfirmation confirmation, string currentUser)
    {
        if (Status != SyncPlanStatus.Confirmed) throw new InvalidOperationException("План не находится в подтверждённом состоянии.");
        if (string.IsNullOrWhiteSpace(currentUser) || !string.Equals(currentUser, ConfirmedBy, StringComparison.Ordinal))
            throw new InvalidOperationException("Пользователь применения не совпадает с подтвердившим план.");
        if (!string.Equals(ComputeContentFingerprint(), ConfirmedFingerprint, StringComparison.OrdinalIgnoreCase))
        { Status = SyncPlanStatus.Stale; throw new InvalidOperationException("План изменился после подтверждения; требуется новый preview."); }
        if (confirmation.PlanId != PlanId || !string.Equals(confirmation.ContentFingerprint, ConfirmedFingerprint, StringComparison.OrdinalIgnoreCase) || confirmation.ConfirmedBy != ConfirmedBy)
            throw new InvalidOperationException("Подтверждение не соответствует этому плану и пользователю.");
        Status = SyncPlanStatus.Applying;
    }

    public void CompleteApply(bool succeeded)
    {
        if (Status != SyncPlanStatus.Applying) throw new InvalidOperationException("Недопустимый переход состояния плана.");
        Status = succeeded ? SyncPlanStatus.Applied : SyncPlanStatus.Failed;
    }

    public void Reject()
    {
        if (Status is not (SyncPlanStatus.Draft or SyncPlanStatus.RequiresReview or SyncPlanStatus.Confirmed))
            throw new InvalidOperationException("Нельзя отклонить план в текущем состоянии.");
        Status = SyncPlanStatus.Rejected;
    }

    private void RequireStatus(SyncPlanStatus expected)
    { if (Status != expected) throw new InvalidOperationException($"Недопустимый переход состояния {Status} → {expected}."); }

    private static JsonNode? Canonicalize(JsonNode? node)
    {
        if (node is null) return null;
        if (node is JsonObject obj)
        {
            var result = new JsonObject();
            foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal)) result[pair.Key] = Canonicalize(pair.Value);
            return result;
        }
        if (node is JsonArray array) return new JsonArray(array.Select(Canonicalize).ToArray());
        return node.DeepClone();
    }
}

public sealed class SyncConfirmation
{
    public Guid PlanId { get; init; }
    public string ContentFingerprint { get; init; } = string.Empty;
    public DateTimeOffset ConfirmedUtc { get; init; } = DateTimeOffset.UtcNow;
    public string ConfirmedBy { get; init; } = string.Empty;
}

public sealed class SyncSafetyPolicy
{
    public bool AllowOnlineWrites { get; init; }
    public bool RequirePreviewAndConfirmation { get; init; } = true;
    public int MaximumOperationsPerPlan { get; init; } = 250;

    public void ValidateForApply(SyncPlan plan, SyncConfirmation confirmation)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(confirmation);
        if (!AllowOnlineWrites) throw new InvalidOperationException("Запись в Online-БД отключена политикой безопасности.");
        if (!RequirePreviewAndConfirmation) throw new InvalidOperationException("Политика должна требовать предварительное сравнение и подтверждение.");
        if (plan.Status != SyncPlanStatus.Confirmed) throw new InvalidOperationException("План не подтверждён или уже завершён.");
        if (plan.Conflicts.Count != 0 || plan.HasBlockedOperations || plan.Operations.Count == 0)
            throw new InvalidOperationException("План содержит конфликты, заблокированные операции или не готов к подтверждению.");
        string actual = plan.ComputeContentFingerprint();
        if (!actual.Equals(plan.ContentFingerprint, StringComparison.OrdinalIgnoreCase) || !actual.Equals(plan.ConfirmedFingerprint, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Содержимое плана изменилось после preview/подтверждения.");
        if (confirmation.PlanId != plan.PlanId || !actual.Equals(confirmation.ContentFingerprint, StringComparison.OrdinalIgnoreCase) || confirmation.ConfirmedBy != plan.ConfirmedBy || string.IsNullOrWhiteSpace(confirmation.ConfirmedBy))
            throw new InvalidOperationException("Подтверждение не относится к текущему плану или пользователю.");
        if (plan.Operations.Count > MaximumOperationsPerPlan) throw new InvalidOperationException("Количество операций превышает лимит политики безопасности.");
    }
}
