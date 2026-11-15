using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Currencies;
using Erp.Abstractions.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Modules.Accounting;

public interface IBudgetService
{
    Task<IReadOnlyList<BudgetPlanDto>> ListAsync(CancellationToken cancellationToken);
    Task<BudgetPlanDto?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<BudgetPlanDto> CreateAsync(BudgetPlanRequest request, CancellationToken cancellationToken);
    Task<BudgetPlanDto> UpdateAsync(Guid id, BudgetPlanRequest request, CancellationToken cancellationToken);
    Task<BudgetPlanDto> ApproveAsync(Guid id, AccountingActionRequest request, CancellationToken cancellationToken);
}

public sealed class BudgetService(
    AccountingDbContext database,
    ITenantContext tenant,
    ICompanyContext company,
    ICurrencyConfigurationService currencies,
    IClock clock,
    IAuditLogWriter audit,
    IBusinessTransactionCoordinator transactions) : IBudgetService
{
    public async Task<IReadOnlyList<BudgetPlanDto>> ListAsync(CancellationToken cancellationToken)
    {
        var plans = await database.BudgetPlans.AsNoTracking().Include(x => x.Lines)
            .OrderByDescending(x => x.UpdatedAtUtc).ToListAsync(cancellationToken);
        var periods = await database.FiscalPeriods.AsNoTracking()
            .Where(x => plans.Select(plan => plan.FiscalPeriodId).Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var accountIds = plans.SelectMany(x => x.Lines).Select(x => x.AccountId).Distinct().ToArray();
        var accounts = await database.Accounts.AsNoTracking().Where(x => accountIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var actuals = await ActualsAsync(plans.Select(x => periods[x.FiscalPeriodId]).DistinctBy(x => x.Id).ToArray(),
            accountIds, cancellationToken);
        return plans.Select(plan => Dto(plan, periods[plan.FiscalPeriodId], accounts, actuals)).ToArray();
    }

    public async Task<BudgetPlanDto?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var plan = await database.BudgetPlans.AsNoTracking().Include(x => x.Lines)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return plan is null ? null : await ToDtoAsync(plan, cancellationToken);
    }

    public async Task<BudgetPlanDto> CreateAsync(BudgetPlanRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var period = await PeriodAsync(request.FiscalPeriodId, cancellationToken);
        var accountIds = request.Lines.Select(x => x.AccountId).Distinct().ToArray();
        var accounts = await database.Accounts.AsNoTracking()
            .Where(x => accountIds.Contains(x.Id) && x.IsActive && (x.Type == AccountType.Expense || x.Type == AccountType.Revenue))
            .ToListAsync(cancellationToken);
        if (accounts.Count != accountIds.Length) throw Error("lines", "Budget lines must use active revenue or expense accounts in this company.");
        var currency = await currencies.GetAsync(cancellationToken);
        var now = clock.UtcNow;
        var planId = Guid.NewGuid();
        var plan = new BudgetPlan
        {
            Id = planId,
            TenantId = TenantId(),
            CompanyId = CompanyId(),
            FiscalPeriodId = period.Id,
            Name = request.Name.Trim(),
            State = BudgetPlanState.Draft,
            CurrencyCode = currency.BaseCurrencyCode,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ConcurrencyStamp = Stamp(),
            Lines = request.Lines.Select(line => new BudgetPlanLine
            {
                Id = Guid.NewGuid(),
                TenantId = TenantId(),
                CompanyId = CompanyId(),
                BudgetPlanId = planId,
                AccountId = line.AccountId,
                BudgetAmount = Money(line.BudgetAmount)
            }).ToList()
        };
        database.BudgetPlans.Add(plan);
        await SaveAsync(cancellationToken);
        await AuditAsync("accounting.budget.created", plan, cancellationToken);
        var result = Dto(plan, period, accounts.ToDictionary(x => x.Id), new Dictionary<(Guid Period, Guid Account), decimal>());
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<BudgetPlanDto> UpdateAsync(Guid id, BudgetPlanRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var plan = await database.BudgetPlans.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException();
        CheckStamp(plan, request.ConcurrencyStamp);
        if (plan.State != BudgetPlanState.Draft) throw Error("state", "Only draft budget plans can be edited.");
        var period = await PeriodAsync(request.FiscalPeriodId, cancellationToken);
        var accountIds = request.Lines.Select(x => x.AccountId).Distinct().ToArray();
        var accounts = await database.Accounts.AsNoTracking()
            .Where(x => accountIds.Contains(x.Id) && x.IsActive && (x.Type == AccountType.Expense || x.Type == AccountType.Revenue))
            .ToListAsync(cancellationToken);
        if (accounts.Count != accountIds.Length) throw Error("lines", "Budget lines must use active revenue or expense accounts in this company.");
        var existingLines = plan.Lines.ToDictionary(x => x.AccountId);
        var requestedAccountIds = request.Lines.Select(x => x.AccountId).ToHashSet();
        var removedLines = plan.Lines.Where(x => !requestedAccountIds.Contains(x.AccountId)).ToArray();
        database.BudgetPlanLines.RemoveRange(removedLines);
        plan.Lines.RemoveAll(x => !requestedAccountIds.Contains(x.AccountId));
        plan.FiscalPeriodId = period.Id;
        plan.Name = request.Name.Trim();
        foreach (var requestLine in request.Lines)
        {
            if (existingLines.TryGetValue(requestLine.AccountId, out var existingLine))
            {
                existingLine.BudgetAmount = Money(requestLine.BudgetAmount);
                continue;
            }
            var line = new BudgetPlanLine
            {
                Id = Guid.NewGuid(),
                TenantId = plan.TenantId,
                CompanyId = plan.CompanyId,
                BudgetPlanId = plan.Id,
                AccountId = requestLine.AccountId,
                BudgetAmount = Money(requestLine.BudgetAmount)
            };
            plan.Lines.Add(line);
            database.BudgetPlanLines.Add(line);
        }
        plan.UpdatedAtUtc = clock.UtcNow;
        plan.ConcurrencyStamp = Stamp();
        await SaveAsync(cancellationToken);
        await AuditAsync("accounting.budget.updated", plan, cancellationToken);
        var actuals = await ActualsAsync([period], accountIds, cancellationToken);
        var result = Dto(plan, period, accounts.ToDictionary(x => x.Id), actuals);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<BudgetPlanDto> ApproveAsync(Guid id, AccountingActionRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var plan = await database.BudgetPlans.Include(x => x.Lines).SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException();
        CheckStamp(plan, request.ConcurrencyStamp);
        if (plan.State == BudgetPlanState.Approved)
        {
            var replay = await ToDtoAsync(plan, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return replay;
        }
        if (plan.Lines.Count == 0) throw Error("lines", "A budget must contain at least one line before approval.");
        var superseded = await database.BudgetPlans.SingleOrDefaultAsync(
            x => x.FiscalPeriodId == plan.FiscalPeriodId && x.State == BudgetPlanState.Approved,
            cancellationToken);
        if (superseded is not null)
        {
            superseded.State = BudgetPlanState.Superseded;
            superseded.UpdatedAtUtc = clock.UtcNow;
            superseded.ConcurrencyStamp = Stamp();
            await AuditAsync("accounting.budget.superseded", superseded, cancellationToken);
        }
        plan.State = BudgetPlanState.Approved;
        plan.ApprovedAtUtc = clock.UtcNow;
        plan.UpdatedAtUtc = clock.UtcNow;
        plan.ConcurrencyStamp = Stamp();
        await SaveAsync(cancellationToken);
        await AuditAsync("accounting.budget.approved", plan, cancellationToken);
        var result = await ToDtoAsync(plan, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<BudgetPlanDto> ToDtoAsync(BudgetPlan plan, CancellationToken cancellationToken)
    {
        var period = await PeriodAsync(plan.FiscalPeriodId, cancellationToken);
        var accountIds = plan.Lines.Select(x => x.AccountId).ToArray();
        var accounts = await database.Accounts.AsNoTracking().Where(x => accountIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var actuals = await ActualsAsync([period], accountIds, cancellationToken);
        return Dto(plan, period, accounts, actuals);
    }

    private async Task<FiscalPeriod> PeriodAsync(Guid id, CancellationToken cancellationToken) =>
        await database.FiscalPeriods.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw Error("fiscalPeriodId", "The fiscal period must belong to this company.");

    private async Task<Dictionary<(Guid Period, Guid Account), decimal>> ActualsAsync(
        FiscalPeriod[] periods, Guid[] accountIds, CancellationToken cancellationToken)
    {
        if (periods.Length == 0 || accountIds.Length == 0) return [];
        var start = periods.Min(x => x.StartsOn);
        var end = periods.Max(x => x.EndsOn);
        var rows = await database.JournalEntryLines.AsNoTracking()
            .Where(line => accountIds.Contains(line.AccountId))
            .Join(database.JournalEntries.AsNoTracking().Where(entry =>
                    entry.State == JournalEntryState.Posted && entry.BusinessDate >= start && entry.BusinessDate <= end),
                line => line.JournalEntryId, entry => entry.Id,
                (line, entry) => new { line.AccountId, entry.BusinessDate, line.BaseDebit, line.BaseCredit })
            .ToListAsync(cancellationToken);
        var normalBalances = await database.Accounts.AsNoTracking().Where(x => accountIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.NormalBalance, cancellationToken);
        var result = new Dictionary<(Guid Period, Guid Account), decimal>();
        foreach (var period in periods)
        {
            foreach (var group in rows.Where(row => row.BusinessDate >= period.StartsOn && row.BusinessDate <= period.EndsOn)
                         .GroupBy(row => row.AccountId))
            {
                var amount = normalBalances[group.Key] == NormalBalance.Credit
                    ? group.Sum(x => x.BaseCredit - x.BaseDebit)
                    : group.Sum(x => x.BaseDebit - x.BaseCredit);
                result[(period.Id, group.Key)] = Money(amount);
            }
        }
        return result;
    }

    private static BudgetPlanDto Dto(BudgetPlan plan, FiscalPeriod period, Dictionary<Guid, Account> accounts,
        IReadOnlyDictionary<(Guid Period, Guid Account), decimal> actuals)
    {
        var lines = plan.Lines.OrderBy(x => accounts[x.AccountId].Code, StringComparer.Ordinal)
            .Select(line =>
            {
                var actual = actuals.GetValueOrDefault((period.Id, line.AccountId));
                return new BudgetPlanLineDto(line.AccountId, accounts[line.AccountId].Code, accounts[line.AccountId].Name,
                    line.BudgetAmount, actual, Money(line.BudgetAmount - actual));
            }).ToArray();
        return new(plan.Id, period.Id, period.Name, plan.Name, plan.State, plan.CurrencyCode,
            Money(lines.Sum(x => x.BudgetAmount)), Money(lines.Sum(x => x.ActualAmount)),
            Money(lines.Sum(x => x.Variance)), plan.CreatedAtUtc, plan.UpdatedAtUtc, plan.ApprovedAtUtc,
            plan.ConcurrencyStamp, lines);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new BudgetConcurrencyException(); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw Error("fiscalPeriodId", "An approved budget already exists for this fiscal period.");
        }
    }

    private async Task AuditAsync(string action, BudgetPlan plan, CancellationToken cancellationToken)
    {
        audit.Append(new(action, nameof(BudgetPlan), plan.Id.ToString(), null, null, JsonSerializer.Serialize(plan)));
        await audit.FlushAsync(cancellationToken);
    }

    private Guid TenantId() => tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
    private Guid CompanyId() => company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
    private static void Validate(BudgetPlanRequest request)
    {
        if (request.FiscalPeriodId == Guid.Empty) throw Error("fiscalPeriodId", "A fiscal period is required.");
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200) throw Error("name", "A budget name is required and must be 200 characters or fewer.");
        if (request.Lines is null || request.Lines.Count > 500) throw Error("lines", "A budget must contain no more than 500 lines.");
        if (request.Lines.Any(line => line.AccountId == Guid.Empty || line.BudgetAmount < 0 || line.BudgetAmount > 999999999999m))
            throw Error("lines", "Budget lines require a valid account and a non-negative amount.");
        if (request.Lines.Select(line => line.AccountId).Distinct().Count() != request.Lines.Count)
            throw Error("lines", "An account may appear only once in a budget.");
    }

    private static void CheckStamp(BudgetPlan plan, string? stamp)
    {
        if (!StringComparer.Ordinal.Equals(plan.ConcurrencyStamp, stamp)) throw new BudgetConcurrencyException();
    }

    private static decimal Money(decimal value) => decimal.Round(value, 4, MidpointRounding.ToEven);
    private static string Stamp() => Guid.NewGuid().ToString("N");
    private static BudgetValidationException Error(string key, string message) =>
        new(new Dictionary<string, string[]> { [key] = [message] });
}
