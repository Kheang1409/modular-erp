using System.Data;
using System.Globalization;
using System.Text.RegularExpressions;
using Erp.Abstractions.Context;
using Erp.Abstractions.Sequences;
using Erp.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Erp.Infrastructure.Sequences;

public sealed partial class PostgresSequenceService(
    PlatformDbContext database,
    ITenantContext tenantContext,
    ICompanyContext companyContext) : ISequenceService
{
    public async Task<string> NextAsync(SequenceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Validate(request);
        var tenantId = tenantContext.TenantId?.Value
            ?? throw new InvalidOperationException("An active tenant is required to allocate a sequence number.");
        var companyId = companyContext.CompanyId?.Value
            ?? throw new InvalidOperationException("An active company is required to allocate a sequence number.");

        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        await database.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO platform.document_sequences
                ("Id", "TenantId", "CompanyId", "Key", "PrefixTemplate", "Padding", "NextValue")
            VALUES
                ({Guid.NewGuid()}, {tenantId}, {companyId}, {request.Key}, {request.PrefixTemplate}, {request.Padding}, 1)
            ON CONFLICT ("TenantId", "CompanyId", "Key") DO NOTHING
            """,
            cancellationToken);

        var definition = await database.DocumentSequences
            .SingleAsync(x => x.CompanyId == companyId && x.Key == request.Key, cancellationToken);
        if (!string.Equals(definition.PrefixTemplate, request.PrefixTemplate, StringComparison.Ordinal) ||
            definition.Padding != request.Padding)
        {
            throw new InvalidOperationException(
                $"Sequence '{request.Key}' is already configured with a different prefix or padding.");
        }

        var connection = (NpgsqlConnection)database.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        command.CommandText =
            """
            UPDATE platform.document_sequences
            SET "NextValue" = "NextValue" + 1
            WHERE "TenantId" = @tenantId AND "CompanyId" = @companyId AND "Key" = @key
            RETURNING "NextValue" - 1
            """;
        command.Parameters.AddWithValue("tenantId", tenantId);
        command.Parameters.AddWithValue("companyId", companyId);
        command.Parameters.AddWithValue("key", request.Key);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        var number = Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
        await transaction.CommitAsync(cancellationToken);

        var prefix = request.PrefixTemplate
            .Replace("{yyyy}", request.BusinessDate.Year.ToString("D4", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{MM}", request.BusinessDate.Month.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{dd}", request.BusinessDate.Day.ToString("D2", CultureInfo.InvariantCulture), StringComparison.Ordinal);
        return string.Concat(prefix, number.ToString($"D{request.Padding}", CultureInfo.InvariantCulture));
    }

    private static void Validate(SequenceRequest request)
    {
        if (!KeyPattern().IsMatch(request.Key))
            throw new ArgumentException("Sequence key must contain lowercase letters, numbers, dots, or hyphens.", nameof(request));
        var literalPrefix = request.PrefixTemplate
            .Replace("{yyyy}", string.Empty, StringComparison.Ordinal)
            .Replace("{MM}", string.Empty, StringComparison.Ordinal)
            .Replace("{dd}", string.Empty, StringComparison.Ordinal);
        if (request.PrefixTemplate.Length > 100 || literalPrefix.Contains('{') || literalPrefix.Contains('}'))
            throw new ArgumentException("Prefix is too long or contains an unsupported date token.", nameof(request));
        if (request.Padding is < 1 or > 18)
            throw new ArgumentOutOfRangeException(nameof(request), "Sequence padding must be between 1 and 18.");
    }

    [GeneratedRegex("^[a-z][a-z0-9.-]{1,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
