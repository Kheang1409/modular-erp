using System.Data;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Infrastructure.Persistence;

public sealed class OrganizationAdministrationService(
    PlatformDbContext database,
    ITenantContext tenantContext,
    ICompanyContext companyContext,
    IAuditLogWriter audit,
    IHttpContextAccessor? httpContextAccessor = null) : IOrganizationAdministrationService
{
    public async Task<IReadOnlyList<CompanySummary>> ListCompaniesAsync(
        Guid userId, CancellationToken cancellationToken, bool includeAllCompanies = false)
    {
        var tenantId = RequireTenant();
        var currentCompanyId = companyContext.CompanyId?.Value;
        var memberships = await database.UserCompanyMemberships.IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId && x.UserId == userId)
            .Select(x => x.CompanyId).ToListAsync(cancellationToken);
        var counts = await database.UserCompanyMemberships.IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId).GroupBy(x => x.CompanyId)
            .ToDictionaryAsync(x => x.Key, x => x.Count(), cancellationToken);
        var companies = await database.Companies.IgnoreQueryFilters().AsNoTracking()
            .Where(x => x.TenantId == tenantId && (includeAllCompanies || memberships.Contains(x.Id)))
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(cancellationToken);
        return companies.Select(company => Summary(
            company, memberships.Contains(company.Id), memberships.Contains(company.Id) && company.Id == currentCompanyId,
            counts.GetValueOrDefault(company.Id))).ToArray();
    }

    public async Task<CompanySummary> CreateCompanyAsync(
        Guid userId,
        CreateCompanyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = RequireTenant();
        var name = Required(request.Name, "Company name", 200);
        var code = Required(request.Code, "Company code", 30).ToUpperInvariant();
        var currency = Currency(request.BaseCurrencyCode);
        var timeZone = TimeZone(request.TimeZoneId);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (!await HasActiveAccessAsync(userId, tenantId, cancellationToken))
            throw new InvalidOperationException("The company creator must belong to an active company in this organization.");
        if (await database.Companies.IgnoreQueryFilters().AnyAsync(
                x => x.TenantId == tenantId && x.Code == code, cancellationToken))
            throw new InvalidOperationException($"Company code '{code}' already exists in this organization.");

        var company = new Company
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Name = name,
            Code = code,
            BaseCurrencyCode = currency,
            TimeZoneId = timeZone,
            IsActive = true
        };
        database.Companies.Add(company);
        database.UserCompanyMemberships.Add(new UserCompanyMembership
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            UserId = userId,
            CompanyId = company.Id,
            IsDefault = false
        });
        database.CompanyCurrencies.Add(new CompanyCurrency
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            CompanyId = company.Id,
            Code = currency,
            Name = currency,
            Symbol = currency,
            MinorUnits = 2,
            IsActive = true,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        });
        audit.Append(new("organization.company.created", "Company", company.Id.ToString(), null, null,
            JsonSerializer.Serialize(new { company.Name, company.Code, company.BaseCurrencyCode, company.TimeZoneId })));
        await FlushChangesAsync(cancellationToken);
        await CommitChangesAsync(transaction, cancellationToken);
        return Summary(company, true, false, 1);
    }

    public async Task<CompanySummary?> UpdateCompanyAsync(
        Guid companyId,
        UpdateCompanyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = RequireTenant();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (!await database.Tenants.AnyAsync(x => x.Id == tenantId && x.IsActive, cancellationToken))
            throw new InvalidOperationException("This organization is inactive or unavailable.");
        var company = await database.Companies.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == companyId, cancellationToken);
        if (company is null) return null;
        await database.Entry(company).ReloadAsync(cancellationToken);
        if (!string.Equals(CompanyVersion(company), request.ExpectedVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("This company changed after it was loaded. Refresh and try again.");
        var name = Required(request.Name, "Company name", 200);
        var code = Required(request.Code, "Company code", 30).ToUpperInvariant();
        var timeZone = TimeZone(request.TimeZoneId);
        if (await database.Companies.IgnoreQueryFilters().AnyAsync(
                x => x.TenantId == tenantId && x.Id != companyId && x.Code == code, cancellationToken))
            throw new InvalidOperationException($"Company code '{code}' already exists in this organization.");
        if (!request.IsActive)
        {
            if (companyContext.CompanyId?.Value == companyId)
                throw new InvalidOperationException("Switch to another company before deactivating the current company.");
            if (!await database.Companies.IgnoreQueryFilters().AnyAsync(
                    x => x.TenantId == tenantId && x.Id != companyId && x.IsActive, cancellationToken))
                throw new InvalidOperationException("The final active company cannot be deactivated.");
            var strandedUsers = await (
                from membership in database.UserCompanyMemberships.IgnoreQueryFilters()
                join user in database.Users on membership.UserId equals user.Id
                where membership.TenantId == tenantId && membership.CompanyId == companyId
                    && (!user.LockoutEnabled || user.LockoutEnd != DateTimeOffset.MaxValue)
                    && !(
                        from other in database.UserCompanyMemberships.IgnoreQueryFilters()
                        join activeCompany in database.Companies.IgnoreQueryFilters()
                            on new { other.TenantId, Id = other.CompanyId } equals new { activeCompany.TenantId, activeCompany.Id }
                        where other.TenantId == tenantId && other.UserId == user.Id
                            && other.CompanyId != companyId && activeCompany.IsActive
                        select other.Id).Any()
                select user.Id).AnyAsync(cancellationToken);
            if (strandedUsers)
                throw new InvalidOperationException("Assign every enabled member to another active company before deactivating this company.");
        }
        var before = JsonSerializer.Serialize(new { company.Name, company.Code, company.TimeZoneId, company.IsActive });
        company.Name = name; company.Code = code; company.TimeZoneId = timeZone; company.IsActive = request.IsActive;
        audit.Append(new("organization.company.updated", "Company", company.Id.ToString(), null, before,
            JsonSerializer.Serialize(new { company.Name, company.Code, company.TimeZoneId, company.IsActive })));
        await FlushChangesAsync(cancellationToken);
        await CommitChangesAsync(transaction, cancellationToken);
        var memberCount = await database.UserCompanyMemberships.IgnoreQueryFilters()
            .CountAsync(x => x.TenantId == tenantId && x.CompanyId == company.Id, cancellationToken);
        var userId = Guid.TryParse(httpContextAccessor?.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier), out var actorId)
            ? actorId : Guid.Empty;
        var isMember = await database.UserCompanyMemberships.IgnoreQueryFilters().AnyAsync(
            x => x.TenantId == tenantId && x.CompanyId == companyId && x.UserId == userId, cancellationToken);
        return Summary(company, isMember, isMember && companyContext.CompanyId?.Value == company.Id, memberCount);
    }

    public async Task<bool> SwitchCompanyAsync(Guid userId, Guid companyId, CancellationToken cancellationToken)
    {
        var tenantId = RequireTenant();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (!await HasActiveAccessAsync(userId, tenantId, cancellationToken)) return false;
        var target = await database.UserCompanyMemberships.IgnoreQueryFilters()
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.UserId == userId && x.CompanyId == companyId, cancellationToken);
        if (target is null || !await database.Companies.IgnoreQueryFilters().AnyAsync(
                x => x.TenantId == tenantId && x.Id == companyId && x.IsActive, cancellationToken)) return false;
        var memberships = await database.UserCompanyMemberships.IgnoreQueryFilters()
            .Where(x => x.TenantId == tenantId && x.UserId == userId).ToListAsync(cancellationToken);
        foreach (var membership in memberships) membership.IsDefault = membership.Id == target.Id;
        audit.Append(new("organization.company.switched", "Company", companyId.ToString(),
            JsonSerializer.Serialize(new { PreviousCompanyId = companyContext.CompanyId?.Value }), null, null));
        await FlushChangesAsync(cancellationToken);
        await CommitChangesAsync(transaction, cancellationToken);
        return true;
    }

    public async Task<OrganizationSummary> UpdateOrganizationAsync(
        UpdateOrganizationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = RequireTenant();
        var name = Required(request.Name, "Organization name", 200);
        var timeZone = TimeZone(request.TimeZoneId);
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var tenant = await database.Tenants.SingleOrDefaultAsync(x => x.Id == tenantId && x.IsActive, cancellationToken)
            ?? throw new InvalidOperationException("This organization is inactive or unavailable.");
        await database.Entry(tenant).ReloadAsync(cancellationToken);
        if (!string.Equals(OrganizationVersion(tenant), request.ExpectedVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("This organization changed after it was loaded. Refresh and try again.");
        var before = JsonSerializer.Serialize(new { tenant.Name, tenant.TimeZoneId });
        tenant.Name = name;
        tenant.TimeZoneId = timeZone;
        audit.Append(new("organization.updated", "Organization", tenant.Id.ToString(), null, before,
            JsonSerializer.Serialize(new { tenant.Name, tenant.TimeZoneId })));
        await FlushChangesAsync(cancellationToken);
        await CommitChangesAsync(transaction, cancellationToken);
        return new(tenant.Id, tenant.Name, tenant.TimeZoneId, OrganizationVersion(tenant));
    }

    private async Task FlushChangesAsync(CancellationToken cancellationToken)
    {
        try { await audit.FlushAsync(cancellationToken); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "23505" })
        {
            database.ChangeTracker.Clear();
            throw new InvalidOperationException("A company with that code already exists. Refresh and choose a unique code.", exception);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: "40001" })
        {
            database.ChangeTracker.Clear();
            throw new InvalidOperationException("Organization access or settings changed during this action. Refresh and try again.", exception);
        }
    }

    private async Task CommitChangesAsync(IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        try { await transaction.CommitAsync(cancellationToken); }
        catch (PostgresException exception) when (exception.SqlState == "40001")
        {
            database.ChangeTracker.Clear();
            throw new InvalidOperationException("Organization access or settings changed during this action. Refresh and try again.", exception);
        }
    }

    private Task<bool> HasActiveAccessAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return (
            from membership in database.UserCompanyMemberships.IgnoreQueryFilters()
            join company in database.Companies.IgnoreQueryFilters()
                on new { membership.TenantId, Id = membership.CompanyId } equals new { company.TenantId, company.Id }
            join tenant in database.Tenants on membership.TenantId equals tenant.Id
            join user in database.Users on membership.UserId equals user.Id
            where membership.TenantId == tenantId && membership.UserId == userId && company.IsActive && tenant.IsActive
                && (!user.LockoutEnabled || !user.LockoutEnd.HasValue || user.LockoutEnd.Value <= now)
            select membership.Id).AnyAsync(cancellationToken);
    }

    private Guid RequireTenant() => tenantContext.TenantId?.Value
        ?? throw new InvalidOperationException("An active organization is required.");
    private static CompanySummary Summary(Company company, bool member, bool current, int count) =>
        new(company.Id, company.Name, company.Code, company.BaseCurrencyCode, company.TimeZoneId,
            company.IsActive, member, current, count, CompanyVersion(company));
    private static string CompanyVersion(Company company) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            company.Name,
            company.Code,
            company.BaseCurrencyCode,
            company.TimeZoneId,
            company.IsActive
        }))));
    internal static string OrganizationVersion(Tenant tenant) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { tenant.Name, tenant.TimeZoneId, tenant.IsActive }))));
    private static string Required(string? value, string label, int maximum)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length > maximum)
            throw new ArgumentException($"{label} is required and cannot exceed {maximum} characters.");
        return normalized;
    }
    private static string Currency(string? value)
    {
        var code = value?.Trim().ToUpperInvariant();
        if (code is not { Length: 3 } || !code.All(char.IsAsciiLetter))
            throw new ArgumentException("Base currency must contain three letters.");
        return code;
    }
    private static string TimeZone(string? value)
    {
        var id = Required(value, "Time zone", 100);
        try { _ = TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (TimeZoneNotFoundException) { throw new ArgumentException($"Time zone '{id}' is not available on this server."); }
        catch (InvalidTimeZoneException) { throw new ArgumentException($"Time zone '{id}' is invalid."); }
        return id;
    }
}
