using Erp.Abstractions.Modules;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Erp.Modules.Accounting;

internal static class AccountingEndpoints
{
    public static void MapAccountingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/accounting").WithTags("Accounting");
        group.MapGet("/tax-codes", (IFinancialControlsService service, CancellationToken token) => Run(() => service.TaxCodesAsync(token))).RequirePermission(AccountingPermissions.Read);
        group.MapPost("/tax-codes", (TaxCodeRequest request, IFinancialControlsService service, CancellationToken token) => Run(() => service.SaveTaxCodeAsync(null, request, token), true)).RequirePermission(AccountingPermissions.Configure);
        group.MapPut("/tax-codes/{id:guid}", (Guid id, TaxCodeRequest request, IFinancialControlsService service, CancellationToken token) => Run(() => service.SaveTaxCodeAsync(id, request, token))).RequirePermission(AccountingPermissions.Configure);
        group.MapGet("/currency-rates", (IFinancialControlsService service, CancellationToken token) => Run(() => service.CurrencyRatesAsync(token))).RequirePermission(AccountingPermissions.Read);
        group.MapGet("/currency-rates/resolve", (string baseCurrency, string quoteCurrency, DateOnly date, IFinancialControlsService service, CancellationToken token) => Run(() => service.ResolveCurrencyRateAsync(baseCurrency, quoteCurrency, date, token))).RequirePermission(AccountingPermissions.Read);
        group.MapPost("/currency-rates", (CurrencyRateRequest request, IFinancialControlsService service, CancellationToken token) => Run(() => service.AddCurrencyRateAsync(request, token), true)).RequirePermission(AccountingPermissions.Configure);
        group.MapGet("/reconciliations", (IFinancialControlsService service, CancellationToken token) => Run(() => service.ReconciliationsAsync(token))).RequirePermission(AccountingPermissions.Reconcile);
        group.MapPost("/reconciliations", (CreateBankReconciliationRequest request, IFinancialControlsService service, CancellationToken token) => Run(() => service.CreateReconciliationAsync(request, token), true)).RequirePermission(AccountingPermissions.Reconcile);
        group.MapPost("/reconciliations/{id:guid}/finalize", (Guid id, AccountingActionRequest request, IFinancialControlsService service, CancellationToken token) => Run(() => service.FinalizeReconciliationAsync(id, request, token))).RequirePermission(AccountingPermissions.Reconcile);
        group.MapGet("/payments", (IPaymentService service, CancellationToken token) => Run(() => service.ListAsync(token))).RequirePermission(AccountingPermissions.PaymentRead);
        group.MapGet("/payments/{id:guid}", (Guid id, IPaymentService service, CancellationToken token) => Run(() => service.GetAsync(id, token))).RequirePermission(AccountingPermissions.PaymentRead);
        group.MapPost("/payments", (CreatePaymentRequest request, IPaymentService service, CancellationToken token) => Run(() => service.CreateAsync(request, token), true)).RequirePermission(AccountingPermissions.PaymentManage);
        group.MapPost("/payments/{id:guid}/post", (Guid id, PostPaymentRequest request, IPaymentService service, CancellationToken token) => Run(() => service.PostAsync(id, request, token))).RequirePermission(AccountingPermissions.PaymentPost);
        group.MapPost("/payments/{id:guid}/reverse", (Guid id, ReversePaymentRequest request, IPaymentService service, CancellationToken token) => Run(() => service.ReverseAsync(id, request, token), true)).RequirePermission(AccountingPermissions.PaymentPost);
        group.MapGet("/bills", (ISupplierBillService service, CancellationToken token) => Run(() => service.ListAsync(token))).RequirePermission(AccountingPermissions.BillRead);
        group.MapGet("/bills/{id:guid}", (Guid id, ISupplierBillService service, CancellationToken token) => Run(() => service.GetAsync(id, token))).RequirePermission(AccountingPermissions.BillRead);
        group.MapPost("/bills/from-purchase-order", (CreateSupplierBillRequest request, ISupplierBillService service, CancellationToken token) => Run(() => service.CreateFromPurchaseOrderAsync(request, token), true)).RequirePermission(AccountingPermissions.BillManage);
        group.MapPost("/bills/{id:guid}/post", (Guid id, PostSupplierBillRequest request, ISupplierBillService service, CancellationToken token) => Run(() => service.PostAsync(id, request, token))).RequirePermission(AccountingPermissions.BillPost);
        group.MapPost("/bills/{id:guid}/cancel", (Guid id, AccountingActionRequest request, ISupplierBillService service, CancellationToken token) => Run(() => service.CancelAsync(id, request, token))).RequirePermission(AccountingPermissions.BillManage);
        group.MapGet("/invoices", (ICustomerInvoiceService service, CancellationToken token) => Run(() => service.ListAsync(token))).RequirePermission(AccountingPermissions.InvoiceRead);
        group.MapGet("/invoices/{id:guid}", (Guid id, ICustomerInvoiceService service, CancellationToken token) => Run(() => service.GetAsync(id, token))).RequirePermission(AccountingPermissions.InvoiceRead);
        group.MapPost("/invoices/from-sales-order", (CreateCustomerInvoiceRequest request, ICustomerInvoiceService service, CancellationToken token) => Run(() => service.CreateFromSalesOrderAsync(request, token), true)).RequirePermission(AccountingPermissions.InvoiceManage);
        group.MapPost("/invoices/{id:guid}/post", (Guid id, PostCustomerInvoiceRequest request, ICustomerInvoiceService service, CancellationToken token) => Run(() => service.PostAsync(id, request, token))).RequirePermission(AccountingPermissions.InvoicePost);
        group.MapPost("/invoices/{id:guid}/cancel", (Guid id, AccountingActionRequest request, ICustomerInvoiceService service, CancellationToken token) => Run(() => service.CancelAsync(id, request, token))).RequirePermission(AccountingPermissions.InvoiceManage);
        group.MapGet("/entries", (IGeneralLedgerService service, CancellationToken token) => Run(() => service.ListAsync(token))).RequirePermission(AccountingPermissions.Read);
        group.MapGet("/entries/{id:guid}", (Guid id, IGeneralLedgerService service, CancellationToken token) => Run(() => service.GetAsync(id, token))).RequirePermission(AccountingPermissions.Read);
        group.MapPost("/entries", (JournalEntryRequest request, IGeneralLedgerService service, CancellationToken token) => Run(() => service.CreateAsync(request, token), true)).RequirePermission(AccountingPermissions.Post);
        group.MapPost("/entries/{id:guid}/post", (Guid id, PostJournalEntryRequest request, IGeneralLedgerService service, CancellationToken token) => Run(() => service.PostAsync(id, request, token))).RequirePermission(AccountingPermissions.Post);
        group.MapPost("/entries/{id:guid}/reverse", (Guid id, ReverseJournalEntryRequest request, IGeneralLedgerService service, CancellationToken token) => Run(() => service.ReverseAsync(id, request, token), true)).RequirePermission(AccountingPermissions.Post);
        group.MapGet("/accounts", (IAccountingConfigurationService service, CancellationToken token) => Run(() => service.AccountsAsync(token))).RequirePermission(AccountingPermissions.Read);
        group.MapPost("/accounts", (AccountRequest request, IAccountingConfigurationService service, CancellationToken token) => Run(() => service.SaveAccountAsync(null, request, token), true)).RequirePermission(AccountingPermissions.Configure);
        group.MapPut("/accounts/{id:guid}", (Guid id, AccountRequest request, IAccountingConfigurationService service, CancellationToken token) => Run(() => service.SaveAccountAsync(id, request, token))).RequirePermission(AccountingPermissions.Configure);
        group.MapGet("/periods", (IAccountingConfigurationService service, CancellationToken token) => Run(() => service.PeriodsAsync(token))).RequirePermission(AccountingPermissions.Read);
        group.MapPost("/periods", (FiscalPeriodRequest request, IAccountingConfigurationService service, CancellationToken token) => Run(() => service.SavePeriodAsync(null, request, token), true)).RequirePermission(AccountingPermissions.Configure);
        group.MapPut("/periods/{id:guid}", (Guid id, FiscalPeriodRequest request, IAccountingConfigurationService service, CancellationToken token) => Run(() => service.SavePeriodAsync(id, request, token))).RequirePermission(AccountingPermissions.Configure);
        group.MapPost("/periods/{id:guid}/close", (Guid id, AccountingActionRequest request, IAccountingConfigurationService service, CancellationToken token) => Run(() => service.SetPeriodStateAsync(id, FiscalPeriodState.Closed, request.ConcurrencyStamp, token))).RequirePermission(AccountingPermissions.ClosePeriods);
        group.MapPost("/periods/{id:guid}/reopen", (Guid id, AccountingActionRequest request, IAccountingConfigurationService service, CancellationToken token) => Run(() => service.SetPeriodStateAsync(id, FiscalPeriodState.Open, request.ConcurrencyStamp, token))).RequirePermission(AccountingPermissions.ClosePeriods);
        group.MapGet("/journals", (IAccountingConfigurationService service, CancellationToken token) => Run(() => service.JournalsAsync(token))).RequirePermission(AccountingPermissions.Read);
        group.MapPost("/journals", (JournalRequest request, IAccountingConfigurationService service, CancellationToken token) => Run(() => service.SaveJournalAsync(null, request, token), true)).RequirePermission(AccountingPermissions.Configure);
        group.MapPut("/journals/{id:guid}", (Guid id, JournalRequest request, IAccountingConfigurationService service, CancellationToken token) => Run(() => service.SaveJournalAsync(id, request, token))).RequirePermission(AccountingPermissions.Configure);
    }

    private static async Task<IResult> Run<T>(Func<Task<T>> action, bool created = false)
    {
        try { var result = await action(); return created ? Results.Created("/api/v1/accounting", result) : Results.Ok(result); }
        catch (AccountingValidationException exception) { return Results.ValidationProblem(exception.Errors); }
        catch (AccountingConcurrencyException exception) { return Results.Conflict(new { error = exception.Message }); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
    }
}
