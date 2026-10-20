using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Events;
using Erp.Abstractions.Persistence;
using Erp.Modules.Accounting.Contracts;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;
namespace Erp.Modules.Accounting;

public interface IAccountingConfigurationService
{
    Task<IReadOnlyList<AccountDto>> AccountsAsync(CancellationToken t); Task<AccountDto> SaveAccountAsync(Guid? id, AccountRequest r, CancellationToken t);
    Task<IReadOnlyList<FiscalPeriodDto>> PeriodsAsync(CancellationToken t); Task<FiscalPeriodDto> SavePeriodAsync(Guid? id, FiscalPeriodRequest r, CancellationToken t); Task<FiscalPeriodDto> SetPeriodStateAsync(Guid id, FiscalPeriodState state, string stamp, CancellationToken t);
    Task<IReadOnlyList<JournalDto>> JournalsAsync(CancellationToken t); Task<JournalDto> SaveJournalAsync(Guid? id, JournalRequest r, CancellationToken t);
}
public sealed class AccountingPeriodStatus(AccountingDbContext db) : IAccountingPeriodStatus
{
    public Task<bool> IsOpenAsync(DateOnly businessDate, CancellationToken cancellationToken) =>
        db.FiscalPeriods.AsNoTracking().AnyAsync(
            period => period.State == FiscalPeriodState.Open &&
                      period.StartsOn <= businessDate &&
                      period.EndsOn >= businessDate,
            cancellationToken);
}
public sealed class AccountingConfigurationService(AccountingDbContext db, ITenantContext tenant, ICompanyContext company,
    IClock clock, IAuditLogWriter audit, IOutboxWriter outbox,
    IBusinessTransactionCoordinator transactions) : IAccountingConfigurationService
{
    public async Task<IReadOnlyList<AccountDto>> AccountsAsync(CancellationToken t) => (await db.Accounts.AsNoTracking().OrderBy(x => x.Code).ToListAsync(t)).Select(Dto).ToList();
    public async Task<AccountDto> SaveAccountAsync(Guid? id, AccountRequest r, CancellationToken t)
    {
        ValidateCodeName(r.Code, r.Name, r.ConcurrencyStamp, id.HasValue);
        await using var transaction = await transactions.BeginAsync(db, t);
        Account x; if (id is null) { x = new() { Id = Guid.NewGuid(), TenantId = Tenant(), CompanyId = Company(), Code = r.Code!.Trim().ToUpperInvariant(), Name = r.Name!.Trim(), Type = r.Type, NormalBalance = r.NormalBalance, AllowDirectPosting = r.AllowDirectPosting, IsActive = r.IsActive, CreatedAtUtc = clock.UtcNow, UpdatedAtUtc = clock.UtcNow, ConcurrencyStamp = Stamp() }; db.Add(x); } else { x = await db.Accounts.SingleOrDefaultAsync(v => v.Id == id, t) ?? throw new KeyNotFoundException(); Check(x.ConcurrencyStamp, r.ConcurrencyStamp!); x.Code = r.Code!.Trim().ToUpperInvariant(); x.Name = r.Name!.Trim(); x.Type = r.Type; x.NormalBalance = r.NormalBalance; x.AllowDirectPosting = r.AllowDirectPosting; x.IsActive = r.IsActive; x.UpdatedAtUtc = clock.UtcNow; x.ConcurrencyStamp = Stamp(); }
        await Save(t); await Audit("accounting.account.saved", x.Id, Dto(x), t);
        var result = Dto(x);
        await transaction.CommitAsync(t);
        return result;
    }
    public async Task<IReadOnlyList<FiscalPeriodDto>> PeriodsAsync(CancellationToken t) => (await db.FiscalPeriods.AsNoTracking().OrderByDescending(x => x.StartsOn).ToListAsync(t)).Select(Dto).ToList();
    public async Task<FiscalPeriodDto> SavePeriodAsync(Guid? id, FiscalPeriodRequest r, CancellationToken t)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || r.Name.Length > 100) throw Error("name", "Name is required and limited to 100 characters."); if (r.StartsOn == default || r.EndsOn == default || r.StartsOn > r.EndsOn) throw Error("dates", "A valid start and end date are required."); if (id.HasValue && string.IsNullOrWhiteSpace(r.ConcurrencyStamp)) throw Error("concurrencyStamp", "Concurrency stamp is required.");
        await using var transaction = await transactions.BeginAsync(db, t);
        if (await db.FiscalPeriods.AnyAsync(x => x.Id != id && x.StartsOn <= r.EndsOn && x.EndsOn >= r.StartsOn, t)) throw Error("dates", "Fiscal periods cannot overlap."); FiscalPeriod x; if (id is null) { x = new() { Id = Guid.NewGuid(), TenantId = Tenant(), CompanyId = Company(), Name = r.Name.Trim(), StartsOn = r.StartsOn, EndsOn = r.EndsOn, State = FiscalPeriodState.Open, CreatedAtUtc = clock.UtcNow, ConcurrencyStamp = Stamp() }; db.Add(x); } else { x = await db.FiscalPeriods.SingleOrDefaultAsync(v => v.Id == id, t) ?? throw new KeyNotFoundException(); Check(x.ConcurrencyStamp, r.ConcurrencyStamp!); if (x.State == FiscalPeriodState.Closed) throw Error("state", "Closed periods cannot be edited."); x.Name = r.Name.Trim(); x.StartsOn = r.StartsOn; x.EndsOn = r.EndsOn; x.ConcurrencyStamp = Stamp(); }
        await Save(t); await Audit("accounting.period.saved", x.Id, Dto(x), t);
        var result = Dto(x);
        await transaction.CommitAsync(t);
        return result;
    }
    public async Task<FiscalPeriodDto> SetPeriodStateAsync(Guid id, FiscalPeriodState state, string stamp, CancellationToken t)
    {
        await using var transaction = await transactions.BeginAsync(db, t);
        var x = await db.FiscalPeriods.SingleOrDefaultAsync(v => v.Id == id, t) ?? throw new KeyNotFoundException(); Check(x.ConcurrencyStamp, stamp);
        if (x.State == state)
        {
            var replay = Dto(x);
            await transaction.CommitAsync(t);
            return replay;
        }
        if (state == FiscalPeriodState.Closed) { var hasDrafts = await db.JournalEntries.AnyAsync(v => v.State == JournalEntryState.Draft && v.BusinessDate >= x.StartsOn && v.BusinessDate <= x.EndsOn, t) || await db.CustomerInvoices.AnyAsync(v => v.State == CustomerInvoiceState.Draft && v.InvoiceDate >= x.StartsOn && v.InvoiceDate <= x.EndsOn, t) || await db.SupplierBills.AnyAsync(v => v.State == SupplierBillState.Draft && v.BillDate >= x.StartsOn && v.BillDate <= x.EndsOn, t) || await db.Payments.AnyAsync(v => v.State == PaymentState.Draft && v.PaymentDate >= x.StartsOn && v.PaymentDate <= x.EndsOn, t); if (hasDrafts) throw Error("state", "Resolve all draft entries, invoices, bills, and payments in the period before closing it."); }
        x.State = state; x.ClosedAtUtc = state == FiscalPeriodState.Closed ? clock.UtcNow : null; x.ConcurrencyStamp = Stamp(); await Save(t); if (state == FiscalPeriodState.Closed) outbox.Add(new FiscalPeriodClosedEvent(Guid.NewGuid(), clock.UtcNow, x.Id, x.Name, x.StartsOn, x.EndsOn)); await Audit(state == FiscalPeriodState.Closed ? "accounting.period.closed" : "accounting.period.reopened", x.Id, Dto(x), t);
        var result = Dto(x);
        await transaction.CommitAsync(t);
        return result;
    }
    public async Task<IReadOnlyList<JournalDto>> JournalsAsync(CancellationToken t) => (await db.Journals.AsNoTracking().OrderBy(x => x.Code).ToListAsync(t)).Select(Dto).ToList();
    public async Task<JournalDto> SaveJournalAsync(Guid? id, JournalRequest r, CancellationToken t)
    {
        ValidateCodeName(r.Code, r.Name, r.ConcurrencyStamp, id.HasValue);
        await using var transaction = await transactions.BeginAsync(db, t);
        Journal x; if (id is null) { x = new() { Id = Guid.NewGuid(), TenantId = Tenant(), CompanyId = Company(), Code = r.Code!.Trim().ToUpperInvariant(), Name = r.Name!.Trim(), IsActive = r.IsActive, CreatedAtUtc = clock.UtcNow, UpdatedAtUtc = clock.UtcNow, ConcurrencyStamp = Stamp() }; db.Add(x); } else { x = await db.Journals.SingleOrDefaultAsync(v => v.Id == id, t) ?? throw new KeyNotFoundException(); Check(x.ConcurrencyStamp, r.ConcurrencyStamp!); x.Code = r.Code!.Trim().ToUpperInvariant(); x.Name = r.Name!.Trim(); x.IsActive = r.IsActive; x.UpdatedAtUtc = clock.UtcNow; x.ConcurrencyStamp = Stamp(); }
        await Save(t); await Audit("accounting.journal.saved", x.Id, Dto(x), t);
        var result = Dto(x);
        await transaction.CommitAsync(t);
        return result;
    }
    private async Task Save(CancellationToken t) { try { await db.SaveChangesAsync(t); } catch (DbUpdateConcurrencyException) { throw new AccountingConcurrencyException(); } catch (DbUpdateException ex) when (ex.InnerException?.Message.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true) { throw Error("code", "Code or date range already exists."); } }
    private async Task Audit(string action, Guid id, object value, CancellationToken t) { audit.Append(new(action, value.GetType().Name, id.ToString(), null, null, JsonSerializer.Serialize(value))); await audit.FlushAsync(t); }
    private Guid Tenant() => tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required."); private Guid Company() => company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
    private static void ValidateCodeName(string? code, string? name, string? stamp, bool update) { var e = new Dictionary<string, string[]>(); if (string.IsNullOrWhiteSpace(code) || code.Length > 50) e["code"] = ["Code is required and limited to 50 characters."]; if (string.IsNullOrWhiteSpace(name) || name.Length > 200) e["name"] = ["Name is required and limited to 200 characters."]; if (update && string.IsNullOrWhiteSpace(stamp)) e["concurrencyStamp"] = ["Concurrency stamp is required."]; if (e.Count > 0) throw new AccountingValidationException(e); }
    private static void Check(string actual, string supplied) { if (!StringComparer.Ordinal.Equals(actual, supplied)) throw new AccountingConcurrencyException(); }
    private static string Stamp() => Guid.NewGuid().ToString("N"); private static AccountingValidationException Error(string key, string message) => new(new Dictionary<string, string[]> { { key, [message] } });
    private static AccountDto Dto(Account x) => new(x.Id, x.Code, x.Name, x.Type, x.NormalBalance, x.AllowDirectPosting, x.IsActive, x.ConcurrencyStamp); private static FiscalPeriodDto Dto(FiscalPeriod x) => new(x.Id, x.Name, x.StartsOn, x.EndsOn, x.State, x.ClosedAtUtc, x.ConcurrencyStamp); private static JournalDto Dto(Journal x) => new(x.Id, x.Code, x.Name, x.IsActive, x.ConcurrencyStamp);
}
