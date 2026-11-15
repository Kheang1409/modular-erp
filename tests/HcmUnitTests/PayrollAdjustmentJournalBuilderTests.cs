using Erp.Modules.Accounting.Contracts;
using Erp.Modules.Hcm;
using Erp.Modules.Hcm.Contracts;

namespace Erp.Hcm.UnitTests;

public sealed class PayrollAdjustmentJournalBuilderTests
{
    [Theory]
    [InlineData(PayrollAdjustmentCategory.SalaryCorrection, PayrollAccountMappingType.SalaryExpense, 1)]
    [InlineData(PayrollAdjustmentCategory.AttendanceCorrection, PayrollAccountMappingType.SalaryExpense, 1)]
    [InlineData(PayrollAdjustmentCategory.RetroactiveCompensationCorrection, PayrollAccountMappingType.SalaryExpense, 1)]
    [InlineData(PayrollAdjustmentCategory.UnderpaymentCorrection, PayrollAccountMappingType.SalaryExpense, 1)]
    [InlineData(PayrollAdjustmentCategory.OvertimeCorrection, PayrollAccountMappingType.OvertimeExpense, 1)]
    [InlineData(PayrollAdjustmentCategory.AllowanceCorrection, PayrollAccountMappingType.AllowanceExpense, 1)]
    [InlineData(PayrollAdjustmentCategory.DeductionCorrection, PayrollAccountMappingType.EmployeeDeductionLiability, -1)]
    [InlineData(PayrollAdjustmentCategory.TaxWithholdingCorrection, PayrollAccountMappingType.TaxLiability, -1)]
    public void SupportedCorrectionProducesBalancedMappedJournal(PayrollAdjustmentCategory category,
        PayrollAccountMappingType expectedMapping, int firstSide)
    {
        var plan = PayrollAdjustmentJournalBuilder.Build(category, 25m, 2, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(expectedMapping, plan.ComponentMapping);
        Assert.Equal(2, plan.Lines.Count);
        Assert.Equal(plan.Lines.Sum(x => x.Debit), plan.Lines.Sum(x => x.Credit));
        Assert.Equal(firstSide > 0 ? 25m : 0m, plan.Lines[0].Debit);
        Assert.Equal(firstSide > 0 ? 0m : 25m, plan.Lines[0].Credit);
    }

    [Theory]
    [InlineData(PayrollAdjustmentCategory.SalaryCorrection, false)]
    [InlineData(PayrollAdjustmentCategory.DeductionCorrection, true)]
    public void NegativeCorrectionReversesDebitAndCreditSides(PayrollAdjustmentCategory category, bool componentDebit)
    {
        var plan = PayrollAdjustmentJournalBuilder.Build(category, -12.5m, 2, Guid.NewGuid(), Guid.NewGuid());

        Assert.Equal(componentDebit ? 12.5m : 0m, plan.Lines[0].Debit);
        Assert.Equal(componentDebit ? 0m : 12.5m, plan.Lines[0].Credit);
        Assert.Equal(componentDebit ? 0m : 12.5m, plan.Lines[1].Debit);
        Assert.Equal(componentDebit ? 12.5m : 0m, plan.Lines[1].Credit);
        Assert.Equal(plan.Lines.Sum(x => x.Debit), plan.Lines.Sum(x => x.Credit));
    }

    [Fact]
    public void OverpaymentRecoveryBooksReceivableWithoutChangingEmployeePayable()
    {
        var receivable = Guid.NewGuid();
        var salaryExpense = Guid.NewGuid();
        var plan = PayrollAdjustmentJournalBuilder.Build(PayrollAdjustmentCategory.OverpaymentRecovery,
            20m, 2, receivable, salaryExpense);

        Assert.Equal(PayrollAccountMappingType.EmployeeReceivable, plan.ComponentMapping);
        Assert.Equal(20m, plan.Lines[0].Debit);
        Assert.Equal(20m, plan.Lines[1].Credit);
        Assert.Equal(0m, plan.Lines.Sum(x => x.Debit) - plan.Lines.Sum(x => x.Credit));
        Assert.All(plan.Lines, line => Assert.NotEqual(Guid.Empty, line.AccountId));
        Assert.Throws<ArgumentException>(() => PayrollAdjustmentJournalBuilder.Build(
            PayrollAdjustmentCategory.OverpaymentRecovery, -20m, 2, receivable, salaryExpense));
    }

    [Fact]
    public void AmountMustAlreadyMatchCurrencyPrecision()
    {
        Assert.Throws<ArgumentException>(() => PayrollAdjustmentJournalBuilder.Build(
            PayrollAdjustmentCategory.SalaryCorrection, 1.234m, 2, Guid.NewGuid(), Guid.NewGuid()));
    }
}
