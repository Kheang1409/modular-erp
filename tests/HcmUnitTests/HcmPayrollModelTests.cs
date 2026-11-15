using System.Text.Json;
using Erp.Modules.Hcm;
using Erp.Modules.Hcm.Contracts;
using Microsoft.EntityFrameworkCore;

namespace Erp.Hcm.UnitTests;

public sealed class HcmPayrollModelTests
{
    [Fact]
    public void PayrollConfigurationModelMatchesMigrationSnapshot()
    {
        using var database = new HcmDbContext(new DbContextOptionsBuilder<HcmDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=erp").Options);

        Assert.False(database.Database.HasPendingModelChanges());
    }

    [Fact]
    public void TypedPayrollSnapshotRoundTripsAllCalculationInputsAndAttendanceEvidence()
    {
        var day = new DateOnly(2026, 2, 28);
        var compensation = new EmployeeCompensationDto(Guid.NewGuid(), Guid.NewGuid(), "USD", day, null,
            3200m, [new("housing", 100m)], [new("retirement", 50m)]);
        var calculation = new PayrollCalculationResult(3200m, 3200m, 0m, 0m, 100m, 3300m,
            330m, 50m, 2920m, [new("allowance:housing", 100m)]);
        var snapshot = new PayrollRunCalculationSnapshot("2", DateTimeOffset.UtcNow, Guid.NewGuid(), Guid.NewGuid(),
            new(Guid.NewGuid(), "2026-02", PayrollPeriodType.Monthly, new(2026, 2, 1), day, new(2026, 3, 5),
                "TEST", PayrollPeriodStatus.Closed, [day], DateTimeOffset.UtcNow),
            Guid.NewGuid(), new(Guid.NewGuid(), "TEST", "v2", true, new(2026, 1, 1), null, 160m, 1.5m,
                [new(10000m, 0.1m)]),
            [new(compensation.EmployeeId, "E-1", "Example Employee", compensation.Id, compensation,
                [new(Guid.NewGuid(), day, AttendanceApprovalStatus.Finalized, AttendanceDisposition.Worked,
                    DateTimeOffset.UtcNow.AddHours(-8), DateTimeOffset.UtcNow, 0m, [])], calculation)]);

        var json = PayrollSnapshotJson.Serialize(snapshot);
        var restored = PayrollSnapshotJson.Deserialize(json);

        Assert.NotNull(restored);
        Assert.Equal(snapshot.TenantId, restored.TenantId);
        Assert.Equal(snapshot.PayrollRules.Version, restored.PayrollRules.Version);
        var employee = Assert.Single(restored.Employees);
        Assert.Equal(compensation.EmployeeId, employee.EmployeeId);
        Assert.Equal(50m, Assert.Single(employee.Compensation.Deductions).Amount);
        Assert.Equal(AttendanceDisposition.Worked, Assert.Single(employee.Attendance).Disposition);
        Assert.Equal(330m, employee.Calculation.TaxWithheld);
        Assert.Contains("\"approvalStatus\":\"Finalized\"", json, StringComparison.Ordinal);
        Assert.Contains("\"basePayForPeriod\":3200", json, StringComparison.Ordinal);

        var legacyJson = JsonSerializer.Serialize(new
        {
            engineVersion = "1",
            calculatedAtUtc = snapshot.CalculatedAtUtc,
            tenantId = snapshot.TenantId,
            companyId = snapshot.CompanyId,
            payrollPeriod = snapshot.PayrollPeriod,
            payrollRuleConfigurationId = snapshot.PayrollRuleConfigurationId,
            payrollRules = snapshot.PayrollRules,
            employees = Array.Empty<PayrollEmployeeCalculationSnapshot>()
        });
        Assert.Equal("1", PayrollSnapshotJson.Deserialize(legacyJson)!.EngineVersion);
    }
}

