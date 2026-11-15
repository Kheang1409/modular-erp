using Erp.Modules.Hcm.Contracts;

namespace Erp.Modules.Hcm;

public static class PayrollAdjustmentLifecyclePolicy
{
    public static bool CanTransition(PayrollAdjustmentStatus current, PayrollAdjustmentStatus target) =>
        (current, target) switch
        {
            (PayrollAdjustmentStatus.Draft or PayrollAdjustmentStatus.Rejected, PayrollAdjustmentStatus.Submitted) => true,
            (PayrollAdjustmentStatus.Submitted, PayrollAdjustmentStatus.Approved or PayrollAdjustmentStatus.Rejected) => true,
            (PayrollAdjustmentStatus.Approved, PayrollAdjustmentStatus.Posted) => true,
            (PayrollAdjustmentStatus.Posted, PayrollAdjustmentStatus.Reversed) => true,
            _ => false
        };

    public static bool CanApprove(Guid? actorUserId, Guid? creatorUserId) =>
        actorUserId is { } actor && actor != creatorUserId;
}
