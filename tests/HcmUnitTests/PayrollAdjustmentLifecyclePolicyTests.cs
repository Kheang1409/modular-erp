using Erp.Modules.Hcm;
using Erp.Modules.Hcm.Contracts;

namespace Erp.Hcm.UnitTests;

public sealed class PayrollAdjustmentLifecyclePolicyTests
{
    [Theory]
    [InlineData(PayrollAdjustmentStatus.Draft, PayrollAdjustmentStatus.Submitted, true)]
    [InlineData(PayrollAdjustmentStatus.Rejected, PayrollAdjustmentStatus.Submitted, true)]
    [InlineData(PayrollAdjustmentStatus.Submitted, PayrollAdjustmentStatus.Approved, true)]
    [InlineData(PayrollAdjustmentStatus.Submitted, PayrollAdjustmentStatus.Rejected, true)]
    [InlineData(PayrollAdjustmentStatus.Approved, PayrollAdjustmentStatus.Posted, true)]
    [InlineData(PayrollAdjustmentStatus.Posted, PayrollAdjustmentStatus.Reversed, true)]
    [InlineData(PayrollAdjustmentStatus.Draft, PayrollAdjustmentStatus.Approved, false)]
    [InlineData(PayrollAdjustmentStatus.Rejected, PayrollAdjustmentStatus.Posted, false)]
    [InlineData(PayrollAdjustmentStatus.Posted, PayrollAdjustmentStatus.Approved, false)]
    [InlineData(PayrollAdjustmentStatus.Reversed, PayrollAdjustmentStatus.Posted, false)]
    public void OnlyDeclaredTransitionsAreAllowed(PayrollAdjustmentStatus current,
        PayrollAdjustmentStatus target, bool expected) =>
        Assert.Equal(expected, PayrollAdjustmentLifecyclePolicy.CanTransition(current, target));

    [Fact]
    public void CreatorCannotApproveAndAnonymousApprovalIsRejected()
    {
        var creator = Guid.NewGuid();

        Assert.False(PayrollAdjustmentLifecyclePolicy.CanApprove(creator, creator));
        Assert.False(PayrollAdjustmentLifecyclePolicy.CanApprove(null, creator));
        Assert.True(PayrollAdjustmentLifecyclePolicy.CanApprove(Guid.NewGuid(), creator));
    }

    [Fact]
    public void SnapshotRevisionDetectsAnySerializedInputChange()
    {
        const string snapshot = "{\"amount\":125.00,\"category\":\"SalaryCorrection\"}";
        var revision = PayrollAdjustmentSnapshotIntegrity.ComputeRevision(snapshot);

        Assert.True(PayrollAdjustmentSnapshotIntegrity.Matches(snapshot, revision));
        Assert.False(PayrollAdjustmentSnapshotIntegrity.Matches(snapshot.Replace("125.00", "126.00", StringComparison.Ordinal), revision));
        Assert.False(PayrollAdjustmentSnapshotIntegrity.Matches(snapshot, string.Empty));
    }
}
