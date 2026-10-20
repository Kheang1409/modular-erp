using System.Reflection;
using Erp.Abstractions.Modules;
using Erp.Abstractions.Events;
using Erp.Abstractions.Persistence;
using Erp.Modules.Accounting.Contracts;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

#pragma warning disable CA1725
namespace Erp.Modules.Accounting;

public sealed class AccountingModule : IErpModule
{
    public ModuleManifest Manifest { get; } = new("erp.accounting", "Accounting", "0.1.0", "Double-entry accounting and financial controls.", "ERP Community", "Apache-2.0", "0.1.0", [new("erp.sales", ">=0.1.0 <1.0.0"), new("erp.purchasing", ">=0.1.0 <1.0.0")], [AccountingPermissions.Read, AccountingPermissions.Configure, AccountingPermissions.Post, AccountingPermissions.ClosePeriods, AccountingPermissions.InvoiceRead, AccountingPermissions.InvoiceManage, AccountingPermissions.InvoicePost, AccountingPermissions.BillRead, AccountingPermissions.BillManage, AccountingPermissions.BillPost, AccountingPermissions.PaymentRead, AccountingPermissions.PaymentManage, AccountingPermissions.PaymentPost, AccountingPermissions.Reconcile]);
    public Assembly ComponentAssembly => typeof(AccountingModule).Assembly;
    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddDbContext<AccountingDbContext>((provider, options) => options.UseNpgsql(
            provider.GetRequiredService<IRelationalDatabaseSession>().Connection,
            postgres => postgres.MigrationsHistoryTable("__EFMigrationsHistory", "accounting")));
        services.AddScoped<IAccountingConfigurationService, AccountingConfigurationService>();
        services.AddScoped<IGeneralLedgerService, GeneralLedgerService>();
        services.AddScoped<ICustomerInvoiceService, CustomerInvoiceService>();
        services.AddScoped<ISupplierBillService, SupplierBillService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IFinancialControlsService, FinancialControlsService>();
        services.AddScoped<IAccountingPeriodStatus, AccountingPeriodStatus>();
        services.AddScoped<IAccountingReportingData, AccountingReportingData>();
        services.AddScoped<IModuleDatabaseInitializer, AccountingDatabaseInitializer>();
    }
    public void ConfigureEndpoints(IEndpointRouteBuilder endpoints) => endpoints.MapAccountingEndpoints();
    public void ConfigureIntegrationEvents(IIntegrationEventRegistry events)
    {
        events.Add<JournalEntryPostedEvent>("accounting.journal-entry.posted.v1");
        events.Add<CustomerInvoicePostedEvent>("accounting.customer-invoice.posted.v1");
        events.Add<SupplierBillPostedEvent>("accounting.supplier-bill.posted.v1");
        events.Add<PaymentPostedEvent>("accounting.payment.posted.v1");
        events.Add<PaymentReversedEvent>("accounting.payment.reversed.v1");
        events.Add<FiscalPeriodClosedEvent>("accounting.fiscal-period.closed.v1");
    }
    public void ConfigurePermissions(IPermissionRegistry permissions)
    {
        permissions.Add(new(AccountingPermissions.Read, "View accounting", Manifest.Id));
        permissions.Add(new(AccountingPermissions.Configure, "Configure accounting", Manifest.Id));
        permissions.Add(new(AccountingPermissions.Post, "Post journal entries", Manifest.Id));
        permissions.Add(new(AccountingPermissions.ClosePeriods, "Close or reopen fiscal periods", Manifest.Id));
        permissions.Add(new(AccountingPermissions.InvoiceRead, "View customer invoices", Manifest.Id));
        permissions.Add(new(AccountingPermissions.InvoiceManage, "Create or cancel draft customer invoices", Manifest.Id));
        permissions.Add(new(AccountingPermissions.InvoicePost, "Post customer invoices", Manifest.Id));
        permissions.Add(new(AccountingPermissions.BillRead, "View supplier bills", Manifest.Id));
        permissions.Add(new(AccountingPermissions.BillManage, "Create or cancel draft supplier bills", Manifest.Id));
        permissions.Add(new(AccountingPermissions.BillPost, "Post supplier bills", Manifest.Id));
        permissions.Add(new(AccountingPermissions.PaymentRead, "View payments", Manifest.Id));
        permissions.Add(new(AccountingPermissions.PaymentManage, "Create payments and allocations", Manifest.Id));
        permissions.Add(new(AccountingPermissions.PaymentPost, "Post or reverse payments", Manifest.Id));
        permissions.Add(new(AccountingPermissions.Reconcile, "Create and finalize bank reconciliations", Manifest.Id));
    }
    public void ConfigureNavigation(INavigationRegistry navigation)
    {
        navigation.Add(new("Financial controls", "/accounting/controls", "Accounting", 84, AccountingPermissions.Read));
        navigation.Add(new("Bank reconciliation", "/accounting/reconciliation", "Accounting", 85, AccountingPermissions.Reconcile));
        navigation.Add(new("Payments", "/accounting/payments", "Accounting", 86, AccountingPermissions.PaymentRead));
        navigation.Add(new("Supplier bills", "/accounting/bills", "Accounting", 87, AccountingPermissions.BillRead));
        navigation.Add(new("Customer invoices", "/accounting/invoices", "Accounting", 88, AccountingPermissions.InvoiceRead));
        navigation.Add(new("General ledger", "/accounting/entries", "Accounting", 89, AccountingPermissions.Read));
        navigation.Add(new("Accounting setup", "/accounting/setup", "Accounting", 90, AccountingPermissions.Read));
    }
}

public static class AccountingPermissions
{
    public const string Read = "accounting.read";
    public const string Configure = "accounting.configure";
    public const string Post = "accounting.post";
    public const string ClosePeriods = "accounting.period.close";
    public const string InvoiceRead = "accounting.invoice.read";
    public const string InvoiceManage = "accounting.invoice.manage";
    public const string InvoicePost = "accounting.invoice.post";
    public const string BillRead = "accounting.bill.read";
    public const string BillManage = "accounting.bill.manage";
    public const string BillPost = "accounting.bill.post";
    public const string PaymentRead = "accounting.payment.read";
    public const string PaymentManage = "accounting.payment.manage";
    public const string PaymentPost = "accounting.payment.post";
    public const string Reconcile = "accounting.reconcile";
}

internal sealed class AccountingDatabaseInitializer(AccountingDbContext db) : IModuleDatabaseInitializer
{
    public string ModuleId => "erp.accounting";
    public Task MigrateAsync(CancellationToken token) => db.Database.MigrateAsync(token);
}
