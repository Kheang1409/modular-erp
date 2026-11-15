using System.Reflection;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Persistence;
using Erp.Modules.Hcm.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.Modules.Hcm;

public sealed class HcmModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new(
        "erp.hcm", "Human Capital Management", "0.1.0",
        "Tenant-safe employee records, benefits, training, performance reviews, leave, and attendance.",
        "ERP Community", "Apache-2.0", "0.1.0", [new("erp.accounting", ">=0.1.0 <1.0.0")],
        [HcmPermissions.Read, HcmPermissions.Manage, HcmPermissions.ApproveLeave,
            HcmPermissions.BenefitsRead, HcmPermissions.BenefitsManage,
            HcmPermissions.TrainingRead, HcmPermissions.TrainingManage,
            HcmPermissions.PerformanceReviewsRead, HcmPermissions.PerformanceReviewsManage,
            HcmPermissions.PayrollRead, HcmPermissions.PayrollManage, HcmPermissions.PayrollApprove, HcmPermissions.PayrollAdjust,
            HcmPermissions.AttendanceApprove]);

    public Assembly ComponentAssembly => typeof(HcmModule).Assembly;

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddHttpContextAccessor();
        services.AddDbContext<HcmDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<IRelationalDatabaseSession>().Connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "hcm")));
        services.AddScoped<HcmService>();
        services.AddScoped<IHcmService>(provider => provider.GetRequiredService<HcmService>());
        services.AddScoped<IEmployeeDirectory>(provider => provider.GetRequiredService<HcmService>());
        services.AddScoped<IModuleDatabaseInitializer, HcmDatabaseInitializer>();
    }

    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapHcmEndpoints();

    public void ConfigurePermissions(IPermissionRegistry permissions)
    {
        permissions.Add(new(HcmPermissions.Read, "View employees, leave, and attendance", Manifest.Id));
        permissions.Add(new(HcmPermissions.Manage, "Manage employees, leave requests, and attendance", Manifest.Id));
        permissions.Add(new(HcmPermissions.ApproveLeave, "Approve or reject leave requests", Manifest.Id));
        permissions.Add(new(HcmPermissions.AttendanceApprove, "Finalize attendance and approve overtime", Manifest.Id));
        permissions.Add(new(HcmPermissions.BenefitsRead, "View employee benefit enrollments", Manifest.Id));
        permissions.Add(new(HcmPermissions.BenefitsManage, "Manage employee benefit enrollments", Manifest.Id));
        permissions.Add(new(HcmPermissions.TrainingRead, "View employee training records", Manifest.Id));
        permissions.Add(new(HcmPermissions.TrainingManage, "Manage employee training records", Manifest.Id));
        permissions.Add(new(HcmPermissions.PerformanceReviewsRead, "View confidential performance reviews", Manifest.Id));
        permissions.Add(new(HcmPermissions.PerformanceReviewsManage, "Manage confidential performance reviews", Manifest.Id));
        permissions.Add(new(HcmPermissions.PayrollRead, "View payroll runs", Manifest.Id));
        permissions.Add(new(HcmPermissions.PayrollManage, "Create draft payroll runs", Manifest.Id));
        permissions.Add(new(HcmPermissions.PayrollApprove, "Approve and mark payroll runs paid", Manifest.Id));
        permissions.Add(new(HcmPermissions.PayrollAdjust, "Create payroll correction requests", Manifest.Id));
    }

    public void ConfigureNavigation(INavigationRegistry navigation)
    {
        navigation.Add(new("People", "/hcm/people", "HCM", 10, HcmPermissions.Read));
        navigation.Add(new("Leave", "/hcm/leave", "HCM", 11, HcmPermissions.Read));
        navigation.Add(new("Training", "/hcm/training", "HCM", 12, HcmPermissions.TrainingRead));
        navigation.Add(new("Performance", "/hcm/reviews", "HCM", 13, HcmPermissions.PerformanceReviewsRead));
        navigation.Add(new("Attendance", "/hcm/attendance", "HCM", 14, HcmPermissions.Read));
        navigation.Add(new("Benefits", "/hcm/benefits", "HCM", 15, HcmPermissions.BenefitsRead));
        navigation.Add(new("Payroll", "/hcm/payroll", "HCM", 16, HcmPermissions.PayrollRead));
    }
}

public static class HcmPermissions
{
    public const string Read = "hcm.read";
    public const string Manage = "hcm.manage";
    public const string ApproveLeave = "hcm.leave.approve";
    public const string BenefitsRead = "hcm.benefits.read";
    public const string BenefitsManage = "hcm.benefits.manage";
    public const string TrainingRead = "hcm.training.read";
    public const string TrainingManage = "hcm.training.manage";
    public const string PerformanceReviewsRead = "hcm.performance_reviews.read";
    public const string PerformanceReviewsManage = "hcm.performance_reviews.manage";
    public const string PayrollRead = "hcm.payroll.read";
    public const string PayrollManage = "hcm.payroll.manage";
    public const string PayrollApprove = "hcm.payroll.approve";
    public const string PayrollAdjust = "hcm.payroll.adjust";
    public const string AttendanceApprove = "hcm.attendance.approve";
}

internal sealed class HcmDatabaseInitializer(HcmDbContext database) : IModuleDatabaseInitializer
{
    public string ModuleId => "erp.hcm";
    public Task MigrateAsync(CancellationToken cancellationToken) => database.Database.MigrateAsync(cancellationToken);
}
