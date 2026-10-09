using System.Reflection;
using Erp.Abstractions.Currencies;
using Erp.Modules.Crm;
using Erp.Modules.Crm.Pages;
using Erp.Modules.Purchasing;
using Erp.Modules.Purchasing.Pages;
using Erp.Modules.Sales;
using Erp.Modules.Sales.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Erp.UnitTests;

public sealed class FormErrorRenderingTests
{
    private static readonly Guid RecordId = Guid.Parse("e8ef5f13-429e-4495-8e37-de373c3bc230");
    private static readonly DateOnly RecordDate = new(2026, 10, 7);

    [Theory]
    [InlineData(typeof(NewLeadPage), "New lead")]
    [InlineData(typeof(EditLeadPage), "Edit lead")]
    [InlineData(typeof(NewQuotationPage), "New quotation")]
    [InlineData(typeof(EditQuotationPage), "Edit quotation")]
    [InlineData(typeof(NewPurchaseOrderPage), "New purchase order")]
    [InlineData(typeof(EditPurchaseOrderPage), "Edit purchase order")]
    public async Task InitialFormDoesNotRenderLiteralErrorVariableOrSpuriousAlert(Type pageType, string heading)
    {
        var parameters = pageType.GetProperty("Id") is null
            ? ParameterView.Empty
            : ParameterView.FromDictionary(new Dictionary<string, object?> { ["Id"] = RecordId });

        var html = await RenderAsync(pageType, parameters);

        Assert.Contains($"<h1>{heading}</h1>", html);
        Assert.Contains("<form", html);
        Assert.DoesNotContain("_error", html);
        Assert.DoesNotContain("role=\"alert\"", html);
    }

    [Theory]
    [InlineData(typeof(LeadEditor), typeof(LeadForm))]
    [InlineData(typeof(QuotationEditor), typeof(QuotationForm))]
    [InlineData(typeof(PurchaseOrderEditor), typeof(PurchaseOrderForm))]
    public async Task EditorsRenderSuppliedErrorAsAnAlert(Type editorType, Type modelType)
    {
        const string message = "This record changed since it was loaded. Please reload before saving.";
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Model"] = Activator.CreateInstance(modelType),
            ["Error"] = message
        });

        var html = await RenderAsync(editorType, parameters);

        Assert.Contains("role=\"alert\"", html);
        Assert.Contains(message, html);
        Assert.DoesNotContain("_error", html);
    }

    [Theory]
    [InlineData(typeof(LeadEditor), typeof(LeadForm))]
    [InlineData(typeof(QuotationEditor), typeof(QuotationForm))]
    [InlineData(typeof(PurchaseOrderEditor), typeof(PurchaseOrderForm))]
    public async Task LiteralStringErrorParameterReproducesReportedText(Type editorType, Type modelType)
    {
        // This models Error="_error" without changing any production component.
        var parameters = ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            ["Model"] = Activator.CreateInstance(modelType),
            ["Error"] = "_error"
        });

        var html = await RenderAsync(editorType, parameters);

        Assert.Contains("role=\"alert\"", html);
        Assert.Contains(">_error</p>", html);
    }

    private static async Task<string> RenderAsync(Type componentType, ParameterView parameters)
    {
        await using var services = CreateServices();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync(componentType, parameters);
            return component.ToHtmlString();
        });
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<NavigationManager, TestNavigationManager>();
        services.AddSingleton(ReadOnlyServiceProxy.Create<ICurrencyConfigurationService>(
            Task.FromResult(new CurrencyConfiguration("USD", [], []))));
        services.AddSingleton(ReadOnlyServiceProxy.Create<ILeadService>(Task.FromResult<LeadDto?>(new(
            Id: RecordId, Title: "Existing lead", ContactId: null, ContactName: null, Email: null,
            Phone: null, Source: null, ExpectedRevenue: 100, Probability: 50, AssignedUserId: null,
            PlannedCloseDate: null, State: LeadState.New, LostReason: null, Notes: null, IsArchived: false,
            CreatedAtUtc: DateTimeOffset.UnixEpoch, UpdatedAtUtc: DateTimeOffset.UnixEpoch,
            ConcurrencyStamp: "lead-version"))));
        services.AddSingleton(ReadOnlyServiceProxy.Create<IQuotationService>(Task.FromResult<QuotationDto?>(new(
            Id: RecordId, Number: "QUO-TEST", CustomerId: RecordId, CustomerName: "Existing customer",
            CurrencyCode: "USD", BaseCurrencyCode: "USD", ExchangeRate: 1, QuotationDate: RecordDate,
            ValidUntil: RecordDate.AddDays(30), State: QuotationState.Draft, Subtotal: 0, DiscountTotal: 0,
            TaxTotal: 0, Total: 0, Notes: null, Terms: null, IsArchived: false,
            CreatedAtUtc: DateTimeOffset.UnixEpoch, UpdatedAtUtc: DateTimeOffset.UnixEpoch,
            ConcurrencyStamp: "quotation-version", Lines: []))));
        services.AddSingleton(ReadOnlyServiceProxy.Create<IPurchaseOrderService>(Task.FromResult<PurchaseOrderDto?>(new(
            Id: RecordId, Number: "PO-TEST", SupplierId: RecordId, SupplierName: "Existing supplier",
            SupplierTaxNumber: null, SupplierAddress: new(null, null, null, null, null, null),
            CurrencyCode: "USD", BaseCurrencyCode: "USD", ExchangeRate: 1, OrderDate: RecordDate,
            ExpectedDeliveryDate: null, State: PurchaseOrderState.Draft, Subtotal: 0, DiscountTotal: 0,
            TaxTotal: 0, Total: 0, Notes: null, IsArchived: false,
            CreatedAtUtc: DateTimeOffset.UnixEpoch, UpdatedAtUtc: DateTimeOffset.UnixEpoch,
            ConcurrencyStamp: "purchase-order-version", Lines: []))));
        return services.BuildServiceProvider();
    }

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://erp.example.test/", "https://erp.example.test/");

        protected override void NavigateToCore(string uri, bool forceLoad) =>
            throw new InvalidOperationException("Initial form rendering must not navigate.");
    }

    public class ReadOnlyServiceProxy : DispatchProxy
    {
        private object? _result;

        public static TService Create<TService>(object result) where TService : class
        {
            var service = Create<TService, ReadOnlyServiceProxy>();
            ((ReadOnlyServiceProxy)(object)service)._result = result;
            return service;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod?.Name == "GetAsync"
                ? _result
                : throw new InvalidOperationException($"Unexpected service call during initial rendering: {targetMethod?.Name}.");
    }
}
