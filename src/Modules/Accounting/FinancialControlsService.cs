using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting;

public interface IFinancialControlsService
{
    Task<IReadOnlyList<TaxCodeDto>> TaxCodesAsync(CancellationToken cancellationToken);
    Task<TaxCodeDto> SaveTaxCodeAsync(Guid? id, TaxCodeRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<CurrencyRateDto>> CurrencyRatesAsync(CancellationToken cancellationToken);
    Task<CurrencyRateDto> AddCurrencyRateAsync(CurrencyRateRequest request, CancellationToken cancellationToken);
    Task<CurrencyRateDto> ResolveCurrencyRateAsync(string baseCurrency, string quoteCurrency, DateOnly effectiveDate, CancellationToken cancellationToken);
    Task<IReadOnlyList<BankReconciliationDto>> ReconciliationsAsync(CancellationToken cancellationToken);
    Task<BankReconciliationDto> CreateReconciliationAsync(CreateBankReconciliationRequest request, CancellationToken cancellationToken);
    Task<BankReconciliationDto> FinalizeReconciliationAsync(Guid id, AccountingActionRequest request, CancellationToken cancellationToken);
}

public sealed class FinancialControlsService(AccountingDbContext db, ITenantContext tenant, ICompanyContext company, IClock clock, IAuditLogWriter audit) : IFinancialControlsService
{
    public async Task<IReadOnlyList<TaxCodeDto>> TaxCodesAsync(CancellationToken cancellationToken) => (await db.TaxCodes.AsNoTracking().OrderBy(x => x.Code).ToListAsync(cancellationToken)).Select(Dto).ToList();
    public async Task<TaxCodeDto> SaveTaxCodeAsync(Guid? id, TaxCodeRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Code) || request.Code.Length > 50 || string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 200) throw Error("code", "Tax code and name are required within their field limits."); if (request.Rate < 0 || request.Rate > 100) throw Error("rate", "Tax rate must be between 0 and 100.");
        var accountIds = new[] { request.OutputAccountId, request.InputAccountId }.Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray(); if (accountIds.Length > 0 && await db.Accounts.AsNoTracking().CountAsync(x => accountIds.Contains(x.Id) && x.IsActive, cancellationToken) != accountIds.Length) throw Error("accounts", "Tax accounts must be active accounts in this company.");
        TaxCode tax; var now = clock.UtcNow;
        if (id is null) { tax = new TaxCode { Id = Guid.NewGuid(), TenantId = Tenant(), CompanyId = Company(), Code = request.Code.Trim().ToUpperInvariant(), Name = request.Name.Trim(), Rate = request.Rate, OutputAccountId = request.OutputAccountId, InputAccountId = request.InputAccountId, IsActive = request.IsActive, CreatedAtUtc = now, UpdatedAtUtc = now, ConcurrencyStamp = Stamp() }; db.TaxCodes.Add(tax); }
        else { tax = await db.TaxCodes.SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw new KeyNotFoundException(); Check(tax.ConcurrencyStamp, request.ConcurrencyStamp); tax.Code = request.Code.Trim().ToUpperInvariant(); tax.Name = request.Name.Trim(); tax.Rate = request.Rate; tax.OutputAccountId = request.OutputAccountId; tax.InputAccountId = request.InputAccountId; tax.IsActive = request.IsActive; tax.UpdatedAtUtc = now; tax.ConcurrencyStamp = Stamp(); }
        await Save(cancellationToken); await Audit("accounting.tax-code.saved", nameof(TaxCode), tax.Id, Dto(tax), cancellationToken); return Dto(tax);
    }

    public async Task<IReadOnlyList<CurrencyRateDto>> CurrencyRatesAsync(CancellationToken cancellationToken) => (await db.CurrencyRates.AsNoTracking().OrderByDescending(x => x.EffectiveDate).ThenBy(x => x.BaseCurrencyCode).ThenBy(x => x.QuoteCurrencyCode).ToListAsync(cancellationToken)).Select(Dto).ToList();
    public async Task<CurrencyRateDto> AddCurrencyRateAsync(CurrencyRateRequest request, CancellationToken cancellationToken)
    {
        var baseCode = Currency(request.BaseCurrencyCode, "baseCurrencyCode"); var quoteCode = Currency(request.QuoteCurrencyCode, "quoteCurrencyCode"); if (baseCode == quoteCode) throw Error("quoteCurrencyCode", "Base and quote currencies must differ."); if (request.EffectiveDate == default || request.Rate <= 0) throw Error("rate", "Effective date and a positive rate are required.");
        var rate = new CurrencyRate { Id = Guid.NewGuid(), TenantId = Tenant(), CompanyId = Company(), BaseCurrencyCode = baseCode, QuoteCurrencyCode = quoteCode, EffectiveDate = request.EffectiveDate, Rate = request.Rate, CreatedAtUtc = clock.UtcNow, ConcurrencyStamp = Stamp() }; db.CurrencyRates.Add(rate); await Save(cancellationToken); await Audit("accounting.currency-rate.added", nameof(CurrencyRate), rate.Id, Dto(rate), cancellationToken); return Dto(rate);
    }
    public async Task<CurrencyRateDto> ResolveCurrencyRateAsync(string baseCurrency, string quoteCurrency, DateOnly effectiveDate, CancellationToken cancellationToken)
    {
        var baseCode = Currency(baseCurrency, "baseCurrency"); var quoteCode = Currency(quoteCurrency, "quoteCurrency"); if (baseCode == quoteCode) return new(Guid.Empty, baseCode, quoteCode, effectiveDate, 1, string.Empty);
        var rate = await db.CurrencyRates.AsNoTracking().Where(x => x.BaseCurrencyCode == baseCode && x.QuoteCurrencyCode == quoteCode && x.EffectiveDate <= effectiveDate).OrderByDescending(x => x.EffectiveDate).FirstOrDefaultAsync(cancellationToken) ?? throw new KeyNotFoundException(); return Dto(rate);
    }

    public async Task<IReadOnlyList<BankReconciliationDto>> ReconciliationsAsync(CancellationToken cancellationToken) => (await db.BankReconciliations.AsNoTracking().Include(x => x.Lines).OrderByDescending(x => x.StatementDate).ToListAsync(cancellationToken)).Select(Dto).ToList();
    public async Task<BankReconciliationDto> CreateReconciliationAsync(CreateBankReconciliationRequest request, CancellationToken cancellationToken)
    {
        if (request.StatementDate == default) throw Error("statementDate", "Statement date is required."); var account = await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.BankAccountId && x.IsActive && x.Type == AccountType.Asset, cancellationToken) ?? throw Error("bankAccountId", "An active asset account is required."); var entryIds = request.JournalEntryIds?.Distinct().ToArray() ?? [];
        var entries = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).Where(x => entryIds.Contains(x.Id) && x.State == JournalEntryState.Posted && x.BusinessDate <= request.StatementDate).ToListAsync(cancellationToken); if (entries.Count != entryIds.Length) throw Error("journalEntryIds", "Every selected entry must be posted on or before the statement date.");
        var now = clock.UtcNow; var id = Guid.NewGuid(); var reconciliation = new BankReconciliation { Id = id, TenantId = Tenant(), CompanyId = Company(), Number = $"REC-{now:yyyyMMdd}-{id.ToString("N")[..8].ToUpperInvariant()}", BankAccountId = account.Id, StatementDate = request.StatementDate, OpeningBalance = Money(request.OpeningBalance), ClosingBalance = Money(request.ClosingBalance), State = BankReconciliationState.Draft, CreatedAtUtc = now, ConcurrencyStamp = Stamp() };
        foreach (var entry in entries) { var amount = Money(entry.Lines.Where(x => x.AccountId == account.Id).Sum(x => x.Debit - x.Credit)); if (amount == 0) throw Error("journalEntryIds", $"Entry {entry.Number} has no movement in the selected bank account."); reconciliation.Lines.Add(new BankReconciliationLine { Id = Guid.NewGuid(), TenantId = reconciliation.TenantId, CompanyId = reconciliation.CompanyId, BankReconciliationId = id, JournalEntryId = entry.Id, JournalEntryNumber = entry.Number, BusinessDate = entry.BusinessDate, Amount = amount }); }
        db.BankReconciliations.Add(reconciliation); await Save(cancellationToken); await Audit("accounting.reconciliation.created", nameof(BankReconciliation), reconciliation.Id, Dto(reconciliation), cancellationToken); return Dto(reconciliation);
    }
    public async Task<BankReconciliationDto> FinalizeReconciliationAsync(Guid id, AccountingActionRequest request, CancellationToken cancellationToken)
    {
        var reconciliation = await db.BankReconciliations.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, cancellationToken) ?? throw new KeyNotFoundException(); Check(reconciliation.ConcurrencyStamp, request.ConcurrencyStamp); if (reconciliation.State == BankReconciliationState.Reconciled) return Dto(reconciliation); if (Difference(reconciliation) != 0) throw Error("difference", "The reconciliation difference must be zero before finalizing."); reconciliation.State = BankReconciliationState.Reconciled; reconciliation.ReconciledAtUtc = clock.UtcNow; reconciliation.ConcurrencyStamp = Stamp(); await Save(cancellationToken); await Audit("accounting.reconciliation.finalized", nameof(BankReconciliation), reconciliation.Id, Dto(reconciliation), cancellationToken); return Dto(reconciliation);
    }

    private async Task Save(CancellationToken cancellationToken) { try { await db.SaveChangesAsync(cancellationToken); } catch (DbUpdateConcurrencyException) { throw new AccountingConcurrencyException(); } catch (DbUpdateException exception) when (exception.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true) { throw Error("duplicate", "A record with the same code, date, or matched entry already exists."); } }
    private async Task Audit(string action, string entity, Guid id, object value, CancellationToken cancellationToken) { audit.Append(new(action, entity, id.ToString(), null, null, JsonSerializer.Serialize(value))); await audit.FlushAsync(cancellationToken); }
    private Guid Tenant() => tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."); private Guid Company() => company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required."); private static decimal Money(decimal value) => decimal.Round(value, 4, MidpointRounding.ToEven); private static decimal Difference(BankReconciliation value) => Money(value.ClosingBalance - (value.OpeningBalance + value.Lines.Sum(x => x.Amount))); private static string Stamp() => Guid.NewGuid().ToString("N"); private static string Currency(string? value, string key) { if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != 3) throw Error(key, "A three-letter currency code is required."); return value.Trim().ToUpperInvariant(); }
    private static void Check(string actual, string? supplied) { if (!StringComparer.Ordinal.Equals(actual, supplied)) throw new AccountingConcurrencyException(); }
    private static AccountingValidationException Error(string key, string message) => new(new Dictionary<string, string[]> { { key, [message] } });
    private static TaxCodeDto Dto(TaxCode x) => new(x.Id, x.Code, x.Name, x.Rate, x.OutputAccountId, x.InputAccountId, x.IsActive, x.ConcurrencyStamp); private static CurrencyRateDto Dto(CurrencyRate x) => new(x.Id, x.BaseCurrencyCode, x.QuoteCurrencyCode, x.EffectiveDate, x.Rate, x.ConcurrencyStamp); private static BankReconciliationDto Dto(BankReconciliation x) { var change = Money(x.Lines.Sum(line => line.Amount)); return new(x.Id, x.Number, x.BankAccountId, x.StatementDate, x.OpeningBalance, x.ClosingBalance, change, Difference(x), x.State, x.CreatedAtUtc, x.ReconciledAtUtc, x.ConcurrencyStamp, x.Lines.OrderBy(line => line.BusinessDate).Select(line => new BankReconciliationLineDto(line.Id, line.JournalEntryId, line.JournalEntryNumber, line.BusinessDate, line.Amount)).ToList()); }
}
