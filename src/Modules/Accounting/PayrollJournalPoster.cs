using Erp.Modules.Accounting.Contracts;

namespace Erp.Modules.Accounting;

public sealed class PayrollJournalPoster(IPayrollJournalResolver journalResolver, IGeneralLedgerService ledger) : IPayrollJournalPoster
{
    public async Task<PayrollJournalSnapshot?> GetPayrollJournalAsync(Guid journalEntryId,
        CancellationToken cancellationToken)
    {
        try
        {
            var entry = await ledger.GetAsync(journalEntryId, cancellationToken);
            return new(entry.Id, entry.Number, entry.State.ToString(), entry.CurrencyCode,
                entry.Lines.Sum(x => x.Debit), entry.Lines.Sum(x => x.Credit),
                entry.Lines.Select(x => new PayrollJournalLineSnapshot(x.AccountId, x.Debit, x.Credit)).ToArray());
        }
        catch (KeyNotFoundException) { return null; }
    }

    public async Task<PayrollJournalSnapshot> ReversePayrollAsync(Guid journalEntryId, DateOnly businessDate,
        string reason, string idempotencyKey, CancellationToken cancellationToken)
    {
        var reversed = await ledger.ReverseAsync(journalEntryId,
            new ReverseJournalEntryRequest(businessDate, reason, idempotencyKey), cancellationToken);
        return new(reversed.Id, reversed.Number, reversed.State.ToString(), reversed.CurrencyCode,
            reversed.Lines.Sum(x => x.Debit), reversed.Lines.Sum(x => x.Credit),
            reversed.Lines.Select(x => new PayrollJournalLineSnapshot(x.AccountId, x.Debit, x.Credit)).ToArray());
    }

    public async Task<PayrollJournalResult> PostPayrollAsync(PayrollJournalRequest request,
        CancellationToken cancellationToken)
    {
        if (request.PayrollRunId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.CalculationRevision) || string.IsNullOrWhiteSpace(request.IdempotencyKey))
            throw new AccountingValidationException(new Dictionary<string, string[]> { ["payroll"] = ["Payroll source, journal, revision, and idempotency key are required."] });
        var journalId = await journalResolver.FindActivePayrollJournalAsync(cancellationToken)
            ?? throw new AccountingValidationException(new Dictionary<string, string[]> { ["journal"] = ["Configure an active PAYROLL journal in Accounting."] });
        var entry = await ledger.CreateAsync(new JournalEntryRequest(journalId, request.BusinessDate,
            $"Payroll {request.PayrollRunNumber} revision {request.CalculationRevision}", request.CurrencyCode, 1,
            request.Lines.Select(x => new JournalEntryLineRequest(x.AccountId, x.Description, x.Debit, x.Credit)).ToArray(),
            $"payroll:{request.PayrollRunId:N}:{request.CalculationRevision}"), cancellationToken);
        if (entry.State != JournalEntryState.Posted)
            entry = await ledger.PostAsync(entry.Id,
                new PostJournalEntryRequest(entry.ConcurrencyStamp, request.IdempotencyKey), cancellationToken);
        return new PayrollJournalResult(entry.Id, entry.Number, entry.State.ToString());
    }
}
