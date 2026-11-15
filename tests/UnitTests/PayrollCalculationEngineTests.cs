using Erp.Modules.Hcm;
using Erp.Modules.Hcm.Contracts;

namespace Erp.UnitTests;

public sealed class PayrollCalculationEngineTests
{
    [Fact]
    public void CalculatesAttendanceOvertimeAllowancesProgressiveTaxAndDeductionsDeterministically()
    {
        var rules = new PayrollRuleSet("TEST", "2026.1", true, 160, 1.5m,
            [new(1000, 0.1m), new(2000, 0.2m)]);
        var input = new PayrollCalculationInput(3200, 170,
            [new("housing", 100)], [new("pension", 50)]);

        var result = PayrollCalculationEngine.Calculate(rules, input);

        Assert.Equal(3200, result.BasePay);
        Assert.Equal(3200, result.AttendancePay);
        Assert.Equal(10, result.OvertimeHours);
        Assert.Equal(300, result.OvertimePay);
        Assert.Equal(100, result.Allowances);
        Assert.Equal(3600, result.GrossPay);
        Assert.Equal(620, result.TaxWithheld);
        Assert.Equal(50, result.OtherDeductions);
        Assert.Equal(2930, result.NetPay);
        var replay = PayrollCalculationEngine.Calculate(rules, input);
        Assert.Equal(result.NetPay, replay.NetPay);
        Assert.Equal(result.Details, replay.Details);
    }

    [Fact]
    public void SupportsExplicitlyConfiguredZeroTaxRules()
    {
        var result = PayrollCalculationEngine.Calculate(new("ZZ", "0", true, 160, 1.5m, []),
            new(1600, 160, [], []));

        Assert.Equal(1600, result.GrossPay);
        Assert.Equal(0, result.TaxWithheld);
    }

    [Fact]
    public void CalculatesConfiguredFixedAndPercentageEmployerContributionsWithCaps()
    {
        var rules = new PayrollRuleSet("TEST", "contrib-1", true, 160, 1.5m, [],
        [
            new("fixed", false, 25),
            new("retirement", true, 10, PayrollContributionBasis.BasePay, 120)
        ]);
        var result = PayrollCalculationEngine.Calculate(rules,
            new PayrollCalculationInput(1000, 160, [], []));
        Assert.Equal(125m, result.EmployerContributions);
        Assert.Contains(result.Details, x => x.Code == "employer-contribution:fixed" && x.Amount == 25m);
        Assert.Contains(result.Details, x => x.Code == "employer-contribution:retirement" && x.Amount == 100m);
    }

    [Fact]
    public void RejectsUnsupportedJurisdictionInsteadOfTreatingItAsZeroTax()
    {
        var exception = Assert.Throws<UnsupportedPayrollJurisdictionException>(() =>
            PayrollCalculationEngine.Calculate(new("XX", "missing", false, 160, 1.5m, []),
                new(1600, 160, [], [])));

        Assert.Contains("XX", exception.Message);
    }

    [Fact]
    public void RejectsTaxAndDeductionsThatExceedGrossPay()
    {
        var rules = new PayrollRuleSet("TEST", "1", true, 160, 1.5m, [new(10000, 0.5m)]);
        var input = new PayrollCalculationInput(100, 160, [], [new("other", 60)]);

        Assert.Throws<ArgumentException>(() => PayrollCalculationEngine.Calculate(rules, input));
    }

    [Fact]
    public void RejectsInvalidTaxBandOrdering()
    {
        var rules = new PayrollRuleSet("TEST", "1", true, 160, 1.5m,
            [new(2000, 0.1m), new(1000, 0.2m)]);

        Assert.Throws<ArgumentException>(() => PayrollCalculationEngine.Calculate(rules,
            new(1000, 160, [], [])));
    }
}
