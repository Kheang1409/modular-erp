using Erp.Modules.Hcm;
using Erp.Modules.Hcm.Contracts;

namespace Erp.Hcm.UnitTests;

public sealed class PayrollRecalculationEngineTests
{
    [Fact]
    public void SalaryCorrectionReplaysOriginalRulesAndDerivesNetDelta()
    {
        var snapshot = Snapshot();
        var result = PayrollRecalculationEngine.Recalculate(snapshot, EmployeeId,
            PayrollAdjustmentCategory.SalaryCorrection, WorkDate,
            new(CorrectedBasePayForPeriod: 3600m));

        Assert.Equal(600m, result.Amount);
        Assert.Equal(3700m, result.CorrectedCalculation.GrossPay);
        Assert.Equal(370m, result.CorrectedCalculation.TaxWithheld);
        Assert.Equal(3280m, result.CorrectedCalculation.NetPay);
        Assert.Equal(3600m, Assert.Single(result.CorrectedSegments).Compensation.BasePayForPeriod);
    }

    [Fact]
    public void RecalculatedSalaryJournalSplitsExpenseTaxLiabilityAndNetPayable()
    {
        var snapshot = Snapshot();
        var corrected = PayrollRecalculationEngine.Recalculate(snapshot, EmployeeId,
            PayrollAdjustmentCategory.SalaryCorrection, WorkDate,
            new(CorrectedBasePayForPeriod: 3600m)).CorrectedCalculation;
        var mappings = new Dictionary<PayrollAccountMappingType, Guid>
        {
            [PayrollAccountMappingType.SalaryExpense] = Guid.NewGuid(),
            [PayrollAccountMappingType.TaxLiability] = Guid.NewGuid(),
            [PayrollAccountMappingType.NetSalaryPayable] = Guid.NewGuid()
        };

        var lines = PayrollAdjustmentJournalBuilder.BuildCalculationDelta(
            Assert.Single(snapshot.Employees).Calculation, corrected, 2, mappings);

        Assert.Equal(600m, lines.Sum(x => x.Debit));
        Assert.Equal(600m, lines.Sum(x => x.Credit));
        Assert.Contains(lines, x => x.AccountId == mappings[PayrollAccountMappingType.SalaryExpense] && x.Debit == 600m);
        Assert.Contains(lines, x => x.AccountId == mappings[PayrollAccountMappingType.TaxLiability] && x.Credit == 60m);
        Assert.Contains(lines, x => x.AccountId == mappings[PayrollAccountMappingType.NetSalaryPayable] && x.Credit == 540m);
    }

    [Fact]
    public void AllowanceCorrectionChangesGrossTaxAndNetUsingFrozenRules()
    {
        var snapshot = Snapshot();
        var result = PayrollRecalculationEngine.Recalculate(snapshot, EmployeeId,
            PayrollAdjustmentCategory.AllowanceCorrection, WorkDate,
            new(ComponentCode: "housing", CorrectedComponentAmount: 150m));

        Assert.Equal(50m, result.Amount);
        Assert.Equal(50m, result.CorrectedCalculation.Allowances - Assert.Single(snapshot.Employees).Calculation.Allowances);
        Assert.Equal(5m, result.CorrectedCalculation.TaxWithheld - Assert.Single(snapshot.Employees).Calculation.TaxWithheld);
        Assert.Equal(45m, result.CorrectedCalculation.NetPay - Assert.Single(snapshot.Employees).Calculation.NetPay);
    }

    [Fact]
    public void RecalculationRejectsMissingHistoricalSegmentsAndWrongInputShape()
    {
        var snapshot = Snapshot();
        var employee = Assert.Single(snapshot.Employees);
        var noSegments = snapshot with { Employees = [employee with { Segments = null }] };

        Assert.Throws<ArgumentException>(() => PayrollRecalculationEngine.Recalculate(noSegments,
            EmployeeId, PayrollAdjustmentCategory.SalaryCorrection, WorkDate, new(CorrectedBasePayForPeriod: 1m)));
        Assert.Throws<ArgumentException>(() => PayrollRecalculationEngine.Recalculate(snapshot,
            EmployeeId, PayrollAdjustmentCategory.SalaryCorrection, WorkDate, new(CorrectedOvertimeHours: 1m)));
        Assert.Throws<ArgumentException>(() => PayrollRecalculationEngine.Recalculate(snapshot,
            EmployeeId, PayrollAdjustmentCategory.TaxWithholdingCorrection, WorkDate, new()));
    }

    private static readonly Guid EmployeeId = Guid.NewGuid();
    private static readonly DateOnly WorkDate = new(2026, 4, 15);

    private static PayrollRunCalculationSnapshot Snapshot()
    {
        var compensation = new EmployeeCompensationDto(Guid.NewGuid(), EmployeeId, "USD", new(2026, 4, 1),
            new(2026, 4, 30), 3000m, [new("housing", 100m)], [new("pension", 50m)],
            PayrollPeriodType.Monthly, 160m);
        var rules = new PayrollRuleSet("TEST", "frozen-1", true, 160m, 1.5m, [new(100000m, 0.1m)]);
        var segment = new PayrollCalculationSegment(3000m, 160m, 160m, 0m,
            [new("housing", 100m)], [new("pension", 50m)]);
        var calculation = PayrollCalculationEngine.Calculate(rules, new(0m, 160m, [], [], [segment]));
        var employee = new PayrollEmployeeCalculationSnapshot(EmployeeId, "E-1", "Example", compensation.Id,
            compensation, [], calculation,
            [new(WorkDate, WorkDate, 1m, compensation.Id, compensation, 160m, 160m, 0m,
                [new("housing", 100m)], [new("pension", 50m)])]);
        var period = new PayrollPeriodDto(Guid.NewGuid(), "2026-04", PayrollPeriodType.Monthly,
            new(2026, 4, 1), new(2026, 4, 30), new(2026, 5, 5), "TEST", PayrollPeriodStatus.Closed,
            [WorkDate], DateTimeOffset.UtcNow);
        var ruleDto = new PayrollRuleConfigurationDto(Guid.NewGuid(), "TEST", "frozen-1", true,
            new(2026, 1, 1), null, 160m, 1.5m, [new(100000m, 0.1m)], PayrollPeriodType.Monthly,
            [], PayrollProrationMethod.ScheduledWorkingDays, PayrollDateBoundaryTreatment.Inclusive,
            PayrollDateBoundaryTreatment.Inclusive, PayrollUnpaidLeaveTreatment.ContinueProration, 2,
            PayrollRoundingMode.MidpointAwayFromZero);
        return new("2", DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(), period,
            Guid.NewGuid(), ruleDto, [employee]);
    }
}
