using System.Text.Json;
using Erp.Abstractions.Auditing;
using Erp.Abstractions.Context;
using Erp.Abstractions.Persistence;
using Erp.Modules.Contacts.Contracts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Erp.Modules.Purchasing;

public interface ISupplierSourcingService
{
    Task<IReadOnlyList<SupplierContractDto>> ContractsAsync(CancellationToken cancellationToken);
    Task<SupplierContractDto?> ContractAsync(Guid id, CancellationToken cancellationToken);
    Task<SupplierContractDto> CreateContractAsync(SupplierContractRequest request, CancellationToken cancellationToken);
    Task<SupplierContractDto> TransitionContractAsync(Guid id, SupplierContractState target, SupplierContractActionRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SupplierEvaluationDto>> EvaluationsAsync(Guid? supplierId, CancellationToken cancellationToken);
    Task<SupplierEvaluationDto?> EvaluationAsync(Guid id, CancellationToken cancellationToken);
    Task<SupplierEvaluationDto> EvaluateSupplierAsync(SupplierEvaluationRequest request, CancellationToken cancellationToken);
}

public sealed class SupplierSourcingService(
    PurchasingDbContext database,
    ISupplierDocumentDirectory suppliers,
    ITenantContext tenant,
    ICompanyContext company,
    IClock clock,
    IAuditLogWriter audit,
    IBusinessTransactionCoordinator transactions) : ISupplierSourcingService
{
    public async Task<IReadOnlyList<SupplierContractDto>> ContractsAsync(CancellationToken cancellationToken) =>
        (await database.SupplierContracts.AsNoTracking().OrderBy(x => x.SupplierName).ThenBy(x => x.EndsOn)
            .ToListAsync(cancellationToken)).Select(ContractDto).ToArray();

    public async Task<SupplierContractDto?> ContractAsync(Guid id, CancellationToken cancellationToken) =>
        await database.SupplierContracts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken) is { } contract
            ? ContractDto(contract)
            : null;

    public async Task<SupplierContractDto> CreateContractAsync(SupplierContractRequest request, CancellationToken cancellationToken)
    {
        ValidateContract(request);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var supplier = await SupplierAsync(request.SupplierId, cancellationToken);
        var currency = NormalizeCurrency(request.CurrencyCode);
        var now = clock.UtcNow;
        var contract = new SupplierContract
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId(),
            CompanyId = CompanyId(),
            SupplierId = supplier.Id,
            SupplierName = supplier.DisplayName,
            ContractNumber = request.ContractNumber.Trim(),
            Title = request.Title.Trim(),
            StartsOn = request.StartsOn,
            EndsOn = request.EndsOn,
            ContractValue = request.ContractValue is null ? null : decimal.Round(request.ContractValue.Value, 4, MidpointRounding.ToEven),
            CurrencyCode = currency,
            State = SupplierContractState.Draft,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            ConcurrencyStamp = Stamp()
        };
        database.SupplierContracts.Add(contract);
        await SaveAsync(cancellationToken);
        await AuditAsync("purchasing.supplier_contract.created", contract, cancellationToken);
        var result = ContractDto(contract);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<SupplierContractDto> TransitionContractAsync(
        Guid id, SupplierContractState target, SupplierContractActionRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var contract = await database.SupplierContracts.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException();
        if (!StringComparer.Ordinal.Equals(contract.ConcurrencyStamp, request.ConcurrencyStamp))
            throw new PurchasingConcurrencyException();
        var allowed = (contract.State, target) switch
        {
            (SupplierContractState.Draft, SupplierContractState.Active) => true,
            (SupplierContractState.Active, SupplierContractState.Terminated) => true,
            _ => false
        };
        if (!allowed) throw Error("state", $"Cannot move supplier contract from {contract.State} to {target}.");
        if (target == SupplierContractState.Active && contract.EndsOn < DateOnly.FromDateTime(clock.UtcNow.UtcDateTime))
            throw Error("endsOn", "An expired supplier contract cannot be activated.");
        contract.State = target;
        contract.UpdatedAtUtc = clock.UtcNow;
        contract.TerminatedAtUtc = target == SupplierContractState.Terminated ? clock.UtcNow : null;
        contract.ConcurrencyStamp = Stamp();
        await SaveAsync(cancellationToken);
        await AuditAsync($"purchasing.supplier_contract.{target.ToString().ToLowerInvariant()}", contract, cancellationToken);
        var result = ContractDto(contract);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<SupplierEvaluationDto>> EvaluationsAsync(Guid? supplierId, CancellationToken cancellationToken)
    {
        var query = database.SupplierEvaluations.AsNoTracking();
        if (supplierId.HasValue) query = query.Where(x => x.SupplierId == supplierId.Value);
        return (await query.OrderByDescending(x => x.AssessedOn).ThenByDescending(x => x.CreatedAtUtc)
            .Take(500).ToListAsync(cancellationToken)).Select(EvaluationDto).ToArray();
    }

    public async Task<SupplierEvaluationDto?> EvaluationAsync(Guid id, CancellationToken cancellationToken) =>
        await database.SupplierEvaluations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, cancellationToken) is { } evaluation
            ? EvaluationDto(evaluation)
            : null;

    public async Task<SupplierEvaluationDto> EvaluateSupplierAsync(SupplierEvaluationRequest request, CancellationToken cancellationToken)
    {
        ValidateEvaluation(request);
        await using var transaction = await transactions.BeginAsync(database, cancellationToken);
        var supplier = await SupplierAsync(request.SupplierId, cancellationToken);
        var evaluation = new SupplierEvaluation
        {
            Id = Guid.NewGuid(),
            TenantId = TenantId(),
            CompanyId = CompanyId(),
            SupplierId = supplier.Id,
            SupplierName = supplier.DisplayName,
            AssessedOn = request.AssessedOn,
            QualityScore = request.QualityScore,
            DeliveryScore = request.DeliveryScore,
            ResponsivenessScore = request.ResponsivenessScore,
            Notes = string.IsNullOrWhiteSpace(request.Notes) ? null : request.Notes.Trim(),
            CreatedAtUtc = clock.UtcNow
        };
        database.SupplierEvaluations.Add(evaluation);
        await SaveAsync(cancellationToken);
        var result = EvaluationDto(evaluation);
        audit.Append(new("purchasing.supplier_evaluation.created", nameof(SupplierEvaluation), evaluation.Id.ToString(),
            null, null, JsonSerializer.Serialize(result)));
        await audit.FlushAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    private async Task<CustomerDocumentReference> SupplierAsync(Guid id, CancellationToken cancellationToken) =>
        await suppliers.FindAsync(id, cancellationToken) is { IsArchived: false } supplier
            && supplier.Id != Guid.Empty
            ? EnsureSupplier(supplier)
            : throw Error("supplierId", "The supplier must be an active supplier in this company.");

    private static CustomerDocumentReference EnsureSupplier(CustomerDocumentReference supplier)
    {
        if (supplier.IsArchived) throw Error("supplierId", "The supplier must be active.");
        return supplier;
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        try { await database.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException) { throw new PurchasingConcurrencyException(); }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw Error("duplicate", "A contract number or supplier assessment already exists for this supplier and date.");
        }
    }

    private async Task AuditAsync(string action, SupplierContract contract, CancellationToken cancellationToken)
    {
        audit.Append(new(action, nameof(SupplierContract), contract.Id.ToString(), null, null, JsonSerializer.Serialize(ContractDto(contract))));
        await audit.FlushAsync(cancellationToken);
    }

    private Guid TenantId() => tenant.TenantId?.Value ?? throw new InvalidOperationException("An active tenant is required.");
    private Guid CompanyId() => company.CompanyId?.Value ?? throw new InvalidOperationException("An active company is required.");
    private static string Stamp() => Guid.NewGuid().ToString("N");
    private SupplierContractDto ContractDto(SupplierContract contract) => new(
        contract.Id, contract.SupplierId, contract.SupplierName, contract.ContractNumber, contract.Title, contract.StartsOn,
        contract.EndsOn, contract.ContractValue, contract.CurrencyCode, contract.State,
        contract.State == SupplierContractState.Active && contract.EndsOn < DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
        contract.CreatedAtUtc, contract.UpdatedAtUtc, contract.ConcurrencyStamp);
    private static SupplierEvaluationDto EvaluationDto(SupplierEvaluation evaluation) => new(
        evaluation.Id, evaluation.SupplierId, evaluation.SupplierName, evaluation.AssessedOn,
        evaluation.QualityScore, evaluation.DeliveryScore, evaluation.ResponsivenessScore,
        decimal.Round((evaluation.QualityScore + evaluation.DeliveryScore + evaluation.ResponsivenessScore) / 3m, 2),
        evaluation.Notes, evaluation.CreatedAtUtc);

    private static void ValidateContract(SupplierContractRequest request)
    {
        if (request.SupplierId == Guid.Empty) throw Error("supplierId", "A supplier is required.");
        if (string.IsNullOrWhiteSpace(request.ContractNumber) || request.ContractNumber.Trim().Length > 100)
            throw Error("contractNumber", "A contract number is required and must be 100 characters or fewer.");
        if (string.IsNullOrWhiteSpace(request.Title) || request.Title.Trim().Length > 200)
            throw Error("title", "A title is required and must be 200 characters or fewer.");
        if (request.StartsOn == default || request.EndsOn < request.StartsOn)
            throw Error("endsOn", "The end date must be on or after the start date.");
        if (request.ContractValue is < 0 or > 999999999999m) throw Error("contractValue", "Contract value must be non-negative.");
        var currency = NormalizeCurrency(request.CurrencyCode);
        if (request.ContractValue.HasValue != (currency is not null))
            throw Error("currencyCode", "A contract value and its currency must be provided together.");
    }

    private void ValidateEvaluation(SupplierEvaluationRequest request)
    {
        if (request.SupplierId == Guid.Empty) throw Error("supplierId", "A supplier is required.");
        if (request.AssessedOn == default || request.AssessedOn > DateOnly.FromDateTime(clock.UtcNow.UtcDateTime))
            throw Error("assessedOn", "Assessment date must be today or earlier.");
        if (request.QualityScore is < 1 or > 5 || request.DeliveryScore is < 1 or > 5 || request.ResponsivenessScore is < 1 or > 5)
            throw Error("scores", "Quality, delivery, and responsiveness scores must each be between 1 and 5.");
        if (request.Notes?.Length > 2000) throw Error("notes", "Evaluation notes must be 2,000 characters or fewer.");
    }

    private static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var code = value.Trim().ToUpperInvariant();
        if (code.Length != 3 || code.Any(character => character is < 'A' or > 'Z'))
            throw Error("currencyCode", "Currency must be a three-letter ISO code.");
        return code;
    }

    private static PurchasingValidationException Error(string key, string message) =>
        new(new Dictionary<string, string[]> { [key] = [message] });
}
