using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Erp.EndToEndTests;

internal static partial class SalesWorkflow
{
    public static async Task VerifyAsync(IPage page, string baseUrl, string productSku)
    {
        var products = await GetAsync(page, baseUrl, $"/api/v1/products?search={productSku}");
        var product = products.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("sku").GetString() == productSku);
        var productId = product.GetProperty("id").GetGuid();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var customerName = $"Browser sales customer {suffix}";
        var taxNumber = $"TEST-{suffix}";
        var customer = await PostAsync(page, baseUrl, "/api/v1/contacts/", new
        {
            kind = 1,
            displayName = customerName,
            organizationName = customerName,
            isCustomer = true,
            taxNumber,
            billingAddress = new { line1 = "10 Fixture Street", city = "Test City", countryCode = "US" },
            shippingAddress = new { line1 = "20 Fixture Avenue", city = "Test City", postalCode = "12345", countryCode = "US" }
        });
        var customerId = customer.GetProperty("id").GetGuid();
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await VisitAsync(page, baseUrl, "/sales/quotations/new", "New quotation");
        await VerifyViewportAsync(page, "New quotation");
        await page.GetByLabel("Customer ID", new() { Exact = true }).FillAsync(customerId.ToString());
        await page.GetByLabel("Currency", new() { Exact = true }).FillAsync(product.GetProperty("currencyCode").GetString()!);
        await page.GetByLabel("Quotation date", new() { Exact = true }).FillAsync(date);
        await FillLineAsync(page.Locator("main fieldset").First, productId, "2", "12.5", "10", "5", $"Browser line {suffix}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save quotation", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(QuotationUrl(), new() { Timeout = 30_000 });
        await ReadyAsync(page);
        var quotationId = Guid.Parse(new Uri(page.Url).Segments[^1]);
        var quotationPath = $"/sales/quotations/{quotationId}";
        var quotationApi = $"/api/v1/sales/quotations/{quotationId}";
        var quotation = await GetAsync(page, baseUrl, quotationApi);
        var quotationNumber = quotation.GetProperty("number").GetString()!;
        Assert.StartsWith("QUO-", quotationNumber, StringComparison.Ordinal);
        Assert.Equal(23.625m, quotation.GetProperty("total").GetDecimal());
        await ExpectDocumentAsync(page, quotationNumber, "Draft");

        await FollowAsync(page, "Edit");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Edit quotation", Level = 1, Exact = true })).ToBeVisibleAsync();
        await page.GetByLabel("Notes", new() { Exact = true }).FillAsync($"Updated browser quotation {suffix}");
        await page.Locator("main fieldset").First.GetByLabel("Quantity", new() { Exact = true }).FillAsync("3");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add line", Exact = true }).ClickAsync();
        await FillLineAsync(page.Locator("main fieldset").Nth(1), productId, "1", "7", "0", "0", $"Additional line {suffix}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Save quotation", Exact = true }).ClickAsync();
        await ExpectPathAsync(page, quotationPath);
        await ExpectDocumentAsync(page, quotationNumber, "Draft");
        var edited = await GetAsync(page, baseUrl, quotationApi);
        Assert.Equal(2, edited.GetProperty("lines").GetArrayLength());
        Assert.Equal(42.4375m, edited.GetProperty("total").GetDecimal());
        Assert.NotEqual(quotation.GetProperty("concurrencyStamp").GetString(), edited.GetProperty("concurrencyStamp").GetString());

        foreach (var state in new[] { "Sent", "Accepted" })
        {
            await FollowAsync(page, "Actions");
            await page.GetByRole(AriaRole.Button, new() { Name = state, Exact = true }).ClickAsync();
            await ExpectPathAsync(page, quotationPath);
            await ExpectDocumentAsync(page, quotationNumber, state);
        }
        await FollowAsync(page, "Create sales order");
        await page.GetByLabel("Order date", new() { Exact = true }).FillAsync(date);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create sales order", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(OrderUrl(), new() { Timeout = 30_000 });
        await ReadyAsync(page);
        var orderId = Guid.Parse(new Uri(page.Url).Segments[^1]);
        var orderPath = $"/sales/orders/{orderId}";
        var orderApi = $"/api/v1/sales/orders/{orderId}";
        var snapshot = await GetAsync(page, baseUrl, orderApi);
        var orderNumber = snapshot.GetProperty("number").GetString()!;
        Assert.StartsWith("SO-", orderNumber, StringComparison.Ordinal);
        Assert.Equal(quotationId, snapshot.GetProperty("sourceQuotationId").GetGuid());
        Assert.Equal(customerName, snapshot.GetProperty("customerName").GetString());
        Assert.Equal(taxNumber, snapshot.GetProperty("customerTaxNumber").GetString());
        Assert.Equal("10 Fixture Street", snapshot.GetProperty("billingAddress").GetProperty("line1").GetString());
        Assert.Equal("20 Fixture Avenue", snapshot.GetProperty("shippingAddress").GetProperty("line1").GetString());
        Assert.Equal(42.4375m, snapshot.GetProperty("total").GetDecimal());
        Assert.Equal(2, snapshot.GetProperty("lines").GetArrayLength());
        await ExpectDocumentAsync(page, orderNumber, "Draft");

        var accepted = await GetAsync(page, baseUrl, quotationApi);
        var duplicate = await page.Context.APIRequest.PostAsync(Url(baseUrl, $"{quotationApi}/convert"), new()
        {
            DataObject = new { concurrencyStamp = accepted.GetProperty("concurrencyStamp").GetString(), orderDate = date }
        });
        Assert.Equal(400, duplicate.Status);
        foreach (var action in new[] { (Link: "Confirm", Button: "Confirm", State: "Confirmed"), (Link: "Cancel", Button: "Cancel order", State: "Cancelled") })
        {
            await FollowAsync(page, action.Link);
            await page.GetByRole(AriaRole.Button, new() { Name = action.Button, Exact = true }).ClickAsync();
            await ExpectPathAsync(page, orderPath);
            await ExpectDocumentAsync(page, orderNumber, action.State);
        }
        var cancelled = await GetAsync(page, baseUrl, orderApi);
        foreach (var field in new[] { "sourceQuotationId", "customerId", "customerName", "customerTaxNumber", "currencyCode", "baseCurrencyCode", "exchangeRate", "subtotal", "discountTotal", "taxTotal", "total", "notes", "terms", "billingAddress", "shippingAddress", "lines" })
            Assert.Equal(snapshot.GetProperty(field).GetRawText(), cancelled.GetProperty(field).GetRawText());

        foreach (var document in new[] { (Path: quotationPath, Number: quotationNumber), (Path: orderPath, Number: orderNumber) })
        {
            await VisitAsync(page, baseUrl, document.Path, document.Number);
            await VerifyViewportAsync(page, document.Number);
        }
    }

    private static async Task FillLineAsync(ILocator line, Guid productId, string quantity, string price, string discount, string tax, string description)
    {
        foreach (var input in new[] { (Label: "Product ID", Value: productId.ToString()), (Label: "Quantity", Value: quantity), (Label: "Unit price", Value: price), (Label: "Discount %", Value: discount), (Label: "Tax %", Value: tax), (Label: "Description", Value: description) })
            await line.GetByLabel(input.Label, new() { Exact = true }).FillAsync(input.Value);
    }

    private static async Task VerifyViewportAsync(IPage page, string context)
    {
        foreach (var width in new[] { 1440, 390 })
        {
            await page.SetViewportSizeAsync(width, 900);
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), $"Sales page {context} overflows at width {width}.");
            Assert.Empty((await page.RunAxe()).Violations ?? []);
        }
        await page.SetViewportSizeAsync(1440, 1000);
    }

    private static Task ReadyAsync(IPage page) => Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");

    private static async Task ExpectDocumentAsync(IPage page, string number, string state)
    {
        await ReadyAsync(page);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = number, Level = 1, Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.Locator("main .eyebrow")).ToHaveTextAsync(state);
        Assert.Equal(0, await page.Locator(".error-panel, main [role='alert']").CountAsync());
    }

    private static async Task VisitAsync(IPage page, string baseUrl, string path, string heading)
    {
        var response = await page.GotoAsync(Url(baseUrl, path));
        Assert.NotNull(response);
        Assert.True(response.Ok, $"Sales navigation returned HTTP {response.Status}.");
        await ReadyAsync(page);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1, Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, await page.Locator(".error-panel, main [role='alert']").CountAsync());
    }

    private static async Task FollowAsync(IPage page, string name)
    {
        await ReadyAsync(page);
        var link = page.GetByRole(AriaRole.Link, new() { Name = name, Exact = true });
        var href = await link.GetAttributeAsync("href");
        Assert.False(string.IsNullOrWhiteSpace(href));
        var target = new Uri(new Uri(page.Url), href).ToString();
        await link.ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(target, new() { Timeout = 30_000 });
        await ReadyAsync(page);
    }

    private static Task ExpectPathAsync(IPage page, string path) =>
        Assertions.Expect(page).ToHaveURLAsync(Url(page.Url, path), new() { Timeout = 30_000 });

    private static async Task<JsonElement> GetAsync(IPage page, string baseUrl, string path)
    {
        var response = await page.Context.APIRequest.GetAsync(Url(baseUrl, path));
        Assert.True(response.Ok, $"Sales read returned HTTP {response.Status}.");
        using var document = JsonDocument.Parse(await response.TextAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> PostAsync(IPage page, string baseUrl, string path, object request)
    {
        var response = await page.Context.APIRequest.PostAsync(Url(baseUrl, path), new() { DataObject = request });
        Assert.True(response.Ok, $"Sales setup returned HTTP {response.Status}.");
        using var document = JsonDocument.Parse(await response.TextAsync());
        return document.RootElement.Clone();
    }

    private static string Url(string baseUrl, string path) => new Uri(new Uri(baseUrl), path).ToString();

    [GeneratedRegex("/sales/quotations/[0-9a-f-]{36}$")]
    private static partial Regex QuotationUrl();

    [GeneratedRegex("/sales/orders/[0-9a-f-]{36}$")]
    private static partial Regex OrderUrl();
}
