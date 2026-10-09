using Erp.Modules.Accounting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Erp.UnitTests;

public sealed class AccountingAuthorizationTests
{
    [Fact]
    public void EveryAccountingEndpointDeclaresExpectedPermission()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        builder.Services.AddScoped<IAccountingConfigurationService>(_ => null!);
        builder.Services.AddScoped<IGeneralLedgerService>(_ => null!);
        builder.Services.AddScoped<ICustomerInvoiceService>(_ => null!);
        builder.Services.AddScoped<ISupplierBillService>(_ => null!);
        builder.Services.AddScoped<IPaymentService>(_ => null!);
        builder.Services.AddScoped<IFinancialControlsService>(_ => null!);
        var app = builder.Build();
        new AccountingModule().ConfigureEndpoints(app);

        var expected = new Dictionary<(string Route, string Method), string>
        {
            [("/api/v1/accounting/tax-codes", "GET")] = AccountingPermissions.Read,
            [("/api/v1/accounting/tax-codes", "POST")] = AccountingPermissions.Configure,
            [("/api/v1/accounting/tax-codes/{id:guid}", "PUT")] = AccountingPermissions.Configure,
            [("/api/v1/accounting/currency-rates", "GET")] = AccountingPermissions.Read,
            [("/api/v1/accounting/currency-rates/resolve", "GET")] = AccountingPermissions.Read,
            [("/api/v1/accounting/currency-rates", "POST")] = AccountingPermissions.Configure,
            [("/api/v1/accounting/reconciliations", "GET")] = AccountingPermissions.Reconcile,
            [("/api/v1/accounting/reconciliations", "POST")] = AccountingPermissions.Reconcile,
            [("/api/v1/accounting/reconciliations/{id:guid}/finalize", "POST")] = AccountingPermissions.Reconcile,
            [("/api/v1/accounting/payments", "GET")] = AccountingPermissions.PaymentRead,
            [("/api/v1/accounting/payments/{id:guid}", "GET")] = AccountingPermissions.PaymentRead,
            [("/api/v1/accounting/payments", "POST")] = AccountingPermissions.PaymentManage,
            [("/api/v1/accounting/payments/{id:guid}/post", "POST")] = AccountingPermissions.PaymentPost,
            [("/api/v1/accounting/payments/{id:guid}/reverse", "POST")] = AccountingPermissions.PaymentPost,
            [("/api/v1/accounting/bills", "GET")] = AccountingPermissions.BillRead,
            [("/api/v1/accounting/bills/{id:guid}", "GET")] = AccountingPermissions.BillRead,
            [("/api/v1/accounting/bills/from-purchase-order", "POST")] = AccountingPermissions.BillManage,
            [("/api/v1/accounting/bills/{id:guid}/post", "POST")] = AccountingPermissions.BillPost,
            [("/api/v1/accounting/bills/{id:guid}/cancel", "POST")] = AccountingPermissions.BillManage,
            [("/api/v1/accounting/invoices", "GET")] = AccountingPermissions.InvoiceRead,
            [("/api/v1/accounting/invoices/{id:guid}", "GET")] = AccountingPermissions.InvoiceRead,
            [("/api/v1/accounting/invoices/from-sales-order", "POST")] = AccountingPermissions.InvoiceManage,
            [("/api/v1/accounting/invoices/{id:guid}/post", "POST")] = AccountingPermissions.InvoicePost,
            [("/api/v1/accounting/invoices/{id:guid}/cancel", "POST")] = AccountingPermissions.InvoiceManage,
            [("/api/v1/accounting/entries", "GET")] = AccountingPermissions.Read,
            [("/api/v1/accounting/entries/{id:guid}", "GET")] = AccountingPermissions.Read,
            [("/api/v1/accounting/entries", "POST")] = AccountingPermissions.Post,
            [("/api/v1/accounting/entries/{id:guid}/post", "POST")] = AccountingPermissions.Post,
            [("/api/v1/accounting/entries/{id:guid}/reverse", "POST")] = AccountingPermissions.Post,
            [("/api/v1/accounting/accounts", "GET")] = AccountingPermissions.Read,
            [("/api/v1/accounting/accounts", "POST")] = AccountingPermissions.Configure,
            [("/api/v1/accounting/accounts/{id:guid}", "PUT")] = AccountingPermissions.Configure,
            [("/api/v1/accounting/periods", "GET")] = AccountingPermissions.Read,
            [("/api/v1/accounting/periods", "POST")] = AccountingPermissions.Configure,
            [("/api/v1/accounting/periods/{id:guid}", "PUT")] = AccountingPermissions.Configure,
            [("/api/v1/accounting/periods/{id:guid}/close", "POST")] = AccountingPermissions.ClosePeriods,
            [("/api/v1/accounting/periods/{id:guid}/reopen", "POST")] = AccountingPermissions.ClosePeriods,
            [("/api/v1/accounting/journals", "GET")] = AccountingPermissions.Read,
            [("/api/v1/accounting/journals", "POST")] = AccountingPermissions.Configure,
            [("/api/v1/accounting/journals/{id:guid}", "PUT")] = AccountingPermissions.Configure
        };

        var endpoints = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v1/accounting", StringComparison.Ordinal) == true)
            .ToArray();

        foreach (var endpoint in endpoints)
        {
            var method = Assert.Single(endpoint.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods);
            var authorization = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>());
            Assert.Equal(expected[(endpoint.RoutePattern.RawText!, method)], authorization.Policy);
        }

        Assert.Equal(expected.Count, endpoints.Length);
    }
}
