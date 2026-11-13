#nullable enable
using Configurator.Core.Synchronization;
using Xunit;

namespace Configurator.Security.Tests;

public sealed class SynchronizationSafetyPolicyTests
{
    [Fact]
    public void OnlineWritesAreDisabledByDefault()
    {
        var policy = new SyncSafetyPolicy();
        var (plan, confirmation) = ConfirmablePlan();
        Assert.False(policy.AllowOnlineWrites);
        Assert.Throws<InvalidOperationException>(() => policy.ValidateForApply(plan, confirmation));
    }

    [Fact]
    public void RequiresPreviewAndConfirmationEvenWhenWritesAreExplicitlyEnabled()
    {
        var policy = new SyncSafetyPolicy { AllowOnlineWrites = true, RequirePreviewAndConfirmation = false };
        var (plan, confirmation) = ConfirmablePlan();
        Assert.Throws<InvalidOperationException>(() => policy.ValidateForApply(plan, confirmation));
    }

    [Fact]
    public void RejectsConfirmationForAnotherPlanOrFingerprint()
    {
        var policy = new SyncSafetyPolicy { AllowOnlineWrites = true };
        var (plan, confirmation) = ConfirmablePlan();
        Assert.Throws<InvalidOperationException>(() => policy.ValidateForApply(plan, new SyncConfirmation
        {
            PlanId = Guid.NewGuid(), ContentFingerprint = confirmation.ContentFingerprint, ConfirmedBy = "tester"
        }));
        Assert.Throws<InvalidOperationException>(() => policy.ValidateForApply(plan, new SyncConfirmation
        {
            PlanId = plan.PlanId, ContentFingerprint = "changed-fingerprint", ConfirmedBy = "tester"
        }));
    }

    [Fact]
    public void RejectsConflictedOrBlockedPlan()
    {
        var policy = new SyncSafetyPolicy { AllowOnlineWrites = true };
        var (plan, confirmation) = ConfirmablePlan();
        plan.Conflicts.Add(new SyncConflict { TableName = "dbo.Items", RecordKey = "1", Kind = SyncConflictKind.RemoteChanged });
        Assert.Throws<InvalidOperationException>(() => policy.ValidateForApply(plan, confirmation));

        var (blockedPlan, blockedConfirmation) = ConfirmablePlan();
        blockedPlan.Operations[0] = new SyncOperation { TableName = "dbo.Items", RecordKey = "1", Kind = SyncOperationKind.Update, IsAllowed = false, BlockReason = "test" };
        Assert.Throws<InvalidOperationException>(() => policy.ValidateForApply(blockedPlan, blockedConfirmation));
    }

    [Fact]
    public void RejectsPlansOverConfiguredOperationLimit()
    {
        var policy = new SyncSafetyPolicy { AllowOnlineWrites = true, MaximumOperationsPerPlan = 1 };
        var (plan, confirmation) = ConfirmablePlan();
        plan.Operations.Add(new SyncOperation { TableName = "dbo.Items", RecordKey = "2", Kind = SyncOperationKind.Insert, IsAllowed = true });
        Assert.Throws<InvalidOperationException>(() => policy.ValidateForApply(plan, confirmation));
    }

    [Fact]
    public void RejectsMissingConfirmingUser()
    {
        var policy = new SyncSafetyPolicy { AllowOnlineWrites = true };
        var (plan, confirmation) = ConfirmablePlan();
        Assert.Throws<InvalidOperationException>(() => policy.ValidateForApply(plan, new SyncConfirmation
        {
            PlanId = confirmation.PlanId, ContentFingerprint = confirmation.ContentFingerprint, ConfirmedBy = " "
        }));
    }

    [Fact]
    public void ContentMutationAfterConfirmationIsRejected()
    {
        var policy = new SyncSafetyPolicy { AllowOnlineWrites = true };
        var (plan, confirmation) = ConfirmablePlan();
        plan.Operations[0] = new SyncOperation { TableName = "dbo.Items", RecordKey = "1", Kind = SyncOperationKind.Update, IsAllowed = true, RemoteVersionAtPreview = "v2" };
        Assert.Throws<InvalidOperationException>(() => policy.ValidateForApply(plan, confirmation));
    }

    [Fact]
    public void CannotConfirmPlanWithConflictOrEmptyIdentity()
    {
        var plan = new SyncPlan { ProjectId = "p", RemoteIdentity = "r", Operations = { new SyncOperation { TableName = "dbo.T", RecordKey = "1", IsAllowed = true } } };
        plan.MarkRequiresReview();
        Assert.Throws<InvalidOperationException>(() => plan.Confirm(" "));
        plan.Conflicts.Add(new SyncConflict { TableName = "dbo.T", RecordKey = "1" });
        Assert.Throws<InvalidOperationException>(() => plan.Confirm("tester"));
    }

    [Fact]
    public void ApplyLifecycleCannotRepeatAndRejectsWrongUser()
    {
        var (plan, confirmation) = ConfirmablePlan();
        Assert.Throws<InvalidOperationException>(() => plan.BeginApply(confirmation, "other-user"));
        plan.BeginApply(confirmation, "isolated-test-user");
        plan.CompleteApply(true);
        Assert.Equal(SyncPlanStatus.Applied, plan.Status);
        Assert.Throws<InvalidOperationException>(() => plan.BeginApply(confirmation, "isolated-test-user"));
    }

    private static (SyncPlan Plan, SyncConfirmation Confirmation) ConfirmablePlan()
    {
        var plan = new SyncPlan
        {
            ProjectId = "isolated-test-project",
            RemoteIdentity = "test-adapter",
            Operations =
            {
                new SyncOperation
                {
                    TableName = "dbo.Items", RecordKey = "1", Kind = SyncOperationKind.Update,
                    IsAllowed = true, RemoteVersionAtPreview = "v1"
                }
            }
        };
        plan.MarkRequiresReview();
        return (plan, plan.Confirm("isolated-test-user"));
    }
}
