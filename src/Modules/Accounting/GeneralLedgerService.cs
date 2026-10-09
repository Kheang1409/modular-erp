using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Events;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Erp.Modules.Accounting;

public interface IGeneralLedgerService
{
    Task<IReadOnlyList<JournalEntryDto>> ListAsync(CancellationToken cancellationToken);
    Task<JournalEntryDto> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<JournalEntryDto> CreateAsync(JournalEntryRequest request, CancellationToken cancellationToken);
    Task<JournalEntryDto> PostAsync(Guid id, PostJournalEntryRequest request, CancellationToken cancellationToken);
    Task<JournalEntryDto> ReverseAsync(Guid id, ReverseJournalEntryRequest request, CancellationToken cancellationToken);
}

public sealed class GeneralLedgerService(AccountingDbContext db, ITenantContext tenant, ICompanyContext company, IClock clock, IAuditLogWriter audit, IOutboxWriter outbox) : IGeneralLedgerService
{
    public async Task<IReadOnlyList<JournalEntryDto>> ListAsync(CancellationToken cancellationToken)
    {
        var entries = await db.JournalEntries.AsNoTracking().Include(x => x.Lines).OrderByDescending(x => x.BusinessDate).ThenByDescending(x => x.CreatedAtUtc).ToListAsync(cancellationToken);
        var reversals = await ReversalMap(cancellationToken);
        return entries.Select(x => Dto(x, reversals.GetValueOrDefault(x.Id))).ToList();
    }

    public async Task<JournalEntryDto> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var entry = await Find(id, cancellationToken);
        var reversedBy = await db.JournalEntries.AsNoTracking().Where(x => x.ReversesEntryId == id).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(cancellationToken);
        return Dto(entry, reversedBy);
    }

    public async Task<JournalEntryDto> CreateAsync(JournalEntryRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        var key = CleanKey(request.IdempotencyKey);
        if (key is not null)
        {
            var existing = await db.JournalEntries.Include(x => x.Lines).SingleOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
            if (existing is not null) return Dto(existing, null);
        }

        var journal = await db.Journals.AsNoTracking().SingleOrDefaultAsync(x => x.Id == request.JournalId && x.IsActive, cancellationToken) ?? throw Error("journalId", "An active journal is required.");
        var accountIds = request.Lines!.Select(x => x.AccountId).Distinct().ToArray();
        var accounts = await db.Accounts.AsNoTracking().Where(x => accountIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        if (accounts.Count != accountIds.Length || accounts.Values.Any(x => !x.IsActive || !x.AllowDirectPosting)) throw Error("lines", "Every line must reference an active direct-posting account.");
        var now = clock.UtcNow;
        var id = Guid.NewGuid();
        var entry = new JournalEntry { Id = id, TenantId = Tenant(), CompanyId = Company(), Number = $"JE-{now:yyyyMMdd}-{id.ToString("N")[..8].ToUpperInvariant()}", JournalId = journal.Id, BusinessDate = request.BusinessDate, Description = request.Description!.Trim(), CurrencyCode = request.CurrencyCode!.Trim().ToUpperInvariant(), ExchangeRate = request.ExchangeRate, State = JournalEntryState.Draft, IdempotencyKey = key, CreatedAtUtc = now, ConcurrencyStamp = Stamp() };
        var sequence = 0;
        foreach (var line in request.Lines!)
        {
            var account = accounts[line.AccountId];
            entry.Lines.Add(new JournalEntryLine { Id = Guid.NewGuid(), TenantId = entry.TenantId, CompanyId = entry.CompanyId, JournalEntryId = id, Sequence = ++sequence, AccountId = account.Id, AccountCode = account.Code, AccountName = account.Name, Description = line.Description?.Trim() ?? string.Empty, Debit = Money(line.Debit), Credit = Money(line.Credit), BaseDebit = Money(line.Debit * request.ExchangeRate), BaseCredit = Money(line.Credit * request.ExchangeRate) });
        }
        db.JournalEntries.Add(entry);
        await Save(cancellationToken);
        await Audit("accounting.entry.created", entry, cancellationToken);
        return Dto(entry, null);
    }

    public async Task<JournalEntryDto> PostAsync(Guid id, PostJournalEntryRequest request, CancellationToken cancellationToken)
    {
        var key = RequiredKey(request.IdempotencyKey);
        var prior = await db.JournalEntries.Include(x => x.Lines).SingleOrDefaultAsync(x => x.PostingKey == key, cancellationToken);
        if (prior is not null)
        {
            if (prior.Id != id) throw Error("idempotencyKey", "This posting key belongs to another journal entry.");
            return Dto(prior, null);
        }
        var entry = await Find(id, cancellationToken);
        if (entry.State == JournalEntryState.Posted) return Dto(entry, null);
        Check(entry.ConcurrencyStamp, request.ConcurrencyStamp);
        await EnsureOpen(entry.BusinessDate, cancellationToken);
        ValidateBalance(entry.Lines.Select(x => (x.Debit, x.Credit)));
        entry.State = JournalEntryState.Posted;
        entry.PostingKey = key;
        entry.PostedAtUtc = clock.UtcNow;
        entry.ConcurrencyStamp = Stamp();
        await Save(cancellationToken);
        outbox.Add(new JournalEntryPostedEvent(Guid.NewGuid(), clock.UtcNow, entry.Id, entry.Number, entry.BusinessDate, entry.CurrencyCode));
        await Audit("accounting.entry.posted", entry, cancellationToken);
        return Dto(entry, null);
    }

    public async Task<JournalEntryDto> ReverseAsync(Guid id, ReverseJournalEntryRequest request, CancellationToken cancellationToken)
    {
        var key = RequiredKey(request.IdempotencyKey);
        var prior = await db.JournalEntries.Include(x => x.Lines).SingleOrDefaultAsync(x => x.IdempotencyKey == key, cancellationToken);
        if (prior is not null)
        {
            if (prior.ReversesEntryId != id) throw Error("idempotencyKey", "This reversal key belongs to another journal entry.");
            return Dto(prior, null);
        }
        var original = await Find(id, cancellationToken);
        if (original.State != JournalEntryState.Posted) throw Error("state", "Only posted entries can be reversed.");
        if (await db.JournalEntries.AnyAsync(x => x.ReversesEntryId == id, cancellationToken)) throw Error("state", "This entry has already been reversed.");
        await EnsureOpen(request.BusinessDate, cancellationToken);
        var now = clock.UtcNow;
        var reversalId = Guid.NewGuid();
        var reversal = new JournalEntry { Id = reversalId, TenantId = Tenant(), CompanyId = Company(), Number = $"RV-{now:yyyyMMdd}-{reversalId.ToString("N")[..8].ToUpperInvariant()}", JournalId = original.JournalId, BusinessDate = request.BusinessDate, Description = string.IsNullOrWhiteSpace(request.Description) ? $"Reversal of {original.Number}" : request.Description.Trim(), CurrencyCode = original.CurrencyCode, ExchangeRate = original.ExchangeRate, State = JournalEntryState.Posted, IdempotencyKey = key, PostingKey = $"reverse:{key}", ReversesEntryId = original.Id, CreatedAtUtc = now, PostedAtUtc = now, ConcurrencyStamp = Stamp() };
        foreach (var line in original.Lines.OrderBy(x => x.Sequence)) reversal.Lines.Add(new JournalEntryLine { Id = Guid.NewGuid(), TenantId = reversal.TenantId, CompanyId = reversal.CompanyId, JournalEntryId = reversalId, Sequence = line.Sequence, AccountId = line.AccountId, AccountCode = line.AccountCode, AccountName = line.AccountName, Description = line.Description, Debit = line.Credit, Credit = line.Debit, BaseDebit = line.BaseCredit, BaseCredit = line.BaseDebit });
        db.JournalEntries.Add(reversal);
        await Save(cancellationToken);
        outbox.Add(new JournalEntryPostedEvent(Guid.NewGuid(), clock.UtcNow, reversal.Id, reversal.Number, reversal.BusinessDate, reversal.CurrencyCode));
        await Audit("accounting.entry.reversed", reversal, cancellationToken);
        return Dto(reversal, null);
    }

    public static void ValidateBalance(IEnumerable<(decimal Debit, decimal Credit)> lines)
    {
        var values = lines.ToArray();
        if (values.Length < 2 || values.Any(x => x.Debit < 0 || x.Credit < 0 || (x.Debit == 0) == (x.Credit == 0))) throw Error("lines", "At least two lines are required and each must contain either a positive debit or a positive credit.");
        if (Money(values.Sum(x => x.Debit)) != Money(values.Sum(x => x.Credit))) throw Error("lines", "Total debits must equal total credits.");
    }
    private static void ValidateRequest(JournalEntryRequest request) { if (request.BusinessDate == default) throw Error("businessDate", "Business date is required."); if (string.IsNullOrWhiteSpace(request.Description) || request.Description.Length > 500) throw Error("description", "Description is required and limited to 500 characters."); if (string.IsNullOrWhiteSpace(request.CurrencyCode) || request.CurrencyCode.Trim().Length != 3) throw Error("currencyCode", "A three-letter currency code is required."); if (request.ExchangeRate <= 0) throw Error("exchangeRate", "Exchange rate must be positive."); ValidateBalance((request.Lines ?? []).Select(x => (x.Debit, x.Credit))); }
    private async Task EnsureOpen(DateOnly date, CancellationToken cancellationToken) { if (!await db.FiscalPeriods.AsNoTracking().AnyAsync(x => x.State == FiscalPeriodState.Open && x.StartsOn <= date && x.EndsOn >= date, cancellationToken)) throw Error("businessDate", "The business date must be in an open fiscal period."); }
    private Task<JournalEntry> Find(Guid id, CancellationToken cancellationToken) => db.JournalEntries.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, cancellationToken).ContinueWith(task => task.Result ?? throw new KeyNotFoundException(), cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    private async Task<Dictionary<Guid, Guid>> ReversalMap(CancellationToken cancellationToken) => await db.JournalEntries.AsNoTracking().Where(x => x.ReversesEntryId != null).ToDictionaryAsync(x => x.ReversesEntryId!.Value, x => x.Id, cancellationToken);
    private async Task Save(CancellationToken cancellationToken) { try { await db.SaveChangesAsync(cancellationToken); } catch (DbUpdateConcurrencyException) { throw new AccountingConcurrencyException(); } catch (DbUpdateException exception) when (exception.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true) { throw Error("idempotencyKey", "The operation key or entry number already exists."); } }
    private async Task Audit(string action, JournalEntry entry, CancellationToken cancellationToken) { audit.Append(new(action, nameof(JournalEntry), entry.Id.ToString(), null, null, JsonSerializer.Serialize(Dto(entry, null)))); await audit.FlushAsync(cancellationToken); }
    private Guid Tenant() => tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."); private Guid Company() => company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
    private static decimal Money(decimal value) => decimal.Round(value, 4, MidpointRounding.ToEven); private static string Stamp() => Guid.NewGuid().ToString("N"); private static string? CleanKey(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim() is var key && key.Length <= 100 ? key : throw Error("idempotencyKey", "Operation key is limited to 100 characters."); private static string RequiredKey(string? value) => CleanKey(value) ?? throw Error("idempotencyKey", "Operation key is required."); private static void Check(string actual, string supplied) { if (!StringComparer.Ordinal.Equals(actual, supplied)) throw new AccountingConcurrencyException(); }
    private static AccountingValidationException Error(string key, string message) => new(new Dictionary<string, string[]> { { key, [message] } });
    private static JournalEntryDto Dto(JournalEntry entry, Guid? reversedBy) => new(entry.Id, entry.Number, entry.JournalId, entry.BusinessDate, entry.Description, entry.CurrencyCode, entry.ExchangeRate, entry.State, entry.ReversesEntryId, reversedBy, entry.CreatedAtUtc, entry.PostedAtUtc, entry.ConcurrencyStamp, entry.Lines.OrderBy(x => x.Sequence).Select(x => new JournalEntryLineDto(x.Id, x.Sequence, x.AccountId, x.AccountCode, x.AccountName, x.Description, x.Debit, x.Credit, x.BaseDebit, x.BaseCredit)).ToList());
}
