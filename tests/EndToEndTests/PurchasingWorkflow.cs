using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Erp.EndToEndTests;

internal static partial class PurchasingWorkflow
{
    public static async Task VerifyAsync(IPage page, string baseUrl, string productSku)
    {
        var products = await GetAsync(page, baseUrl, $"/api/v1/products?search={productSku}");
        var product = products.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("sku").GetString() == productSku);
        var productId = product.GetProperty("id").GetGuid();
        var currency = product.GetProperty("currencyCode").GetString()!;
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var supplierName = $"Browser purchasing supplier {suffix}";
        var taxNumber = $"SUPPLIER-{suffix}";
        var supplier = await PostAsync(page, baseUrl, "/api/v1/contacts/", new
        {
            kind = 1,
            displayName = supplierName,
            organizationName = supplierName,
            isSupplier = true,
            taxNumber,
            billingAddress = new { line1 = "30 Supplier Street", city = "Test City", postalCode = "12345", countryCode = "US" }
        });
        var supplierId = supplier.GetProperty("id").GetGuid();
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var delivery = DateTime.UtcNow.AddDays(7).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        await VisitAsync(page, baseUrl, "/purchasing/orders/new", "New purchase order");
        await VerifyViewportAsync(page, "New purchase order");
        await page.GetByLabel("Supplier ID", new() { Exact = true }).FillAsync(supplierId.ToString());
        await page.GetByLabel("Currency", new() { Exact = true }).FillAsync(currency);
        await page.GetByLabel("Order date", new() { Exact = true }).FillAsync(date);
        await page.GetByLabel("Expected delivery", new() { Exact = true }).FillAsync(delivery);
        await FillLineAsync(page.Locator("main fieldset").First, productId, "2", "12.5", "10", "5", $"Browser purchase line {suffix}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add line", Exact = true }).ClickAsync();
        await FillLineAsync(page.Locator("main fieldset").Nth(1), productId, "1", "7", "0", "0", $"Additional purchase line {suffix}");
        await SaveAsync(page);
        await Assertions.Expect(page).ToHaveURLAsync(OrderUrl(), new() { Timeout = 30_000 });
        await ReadyAsync(page);
        var orderId = Guid.Parse(new Uri(page.Url).Segments[^1]);
        var orderPath = $"/purchasing/orders/{orderId}";
        var orderApi = $"/api/v1/purchasing/orders/{orderId}";
        var created = await GetAsync(page, baseUrl, orderApi);
        var number = created.GetProperty("number").GetString()!;
        Assert.StartsWith("PO-", number, StringComparison.Ordinal);
        Assert.Equal(30.625m, created.GetProperty("total").GetDecimal());
        await ExpectDocumentAsync(page, number, "Draft");

        await EditAsync(page);
        await page.GetByLabel("Notes", new() { Exact = true }).FillAsync($"Updated browser purchase order {suffix}");
        await page.Locator("main fieldset").First.GetByLabel("Quantity", new() { Exact = true }).FillAsync("3");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add line", Exact = true }).ClickAsync();
        await FillLineAsync(page.Locator("main fieldset").Nth(2), productId, "2", "4.25", "20", "10", $"Third purchase line {suffix}");
        var edited = await SaveEditAsync(page, baseUrl, orderPath, orderApi, number, created, 3, 49.9175m);

        await EditAsync(page);
        await page.Locator("main fieldset").Nth(2).GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        await page.Locator("main fieldset").Nth(1).GetByRole(AriaRole.Button, new() { Name = "Remove", Exact = true }).ClickAsync();
        edited = await SaveEditAsync(page, baseUrl, orderPath, orderApi, number, edited, 1, 35.4375m);

        await EditAsync(page);
        await page.GetByRole(AriaRole.Button, new() { Name = "Add line", Exact = true }).ClickAsync();
        await FillLineAsync(page.Locator("main fieldset").Nth(1), productId, "1", "7", "0", "0", $"Final purchase line {suffix}");
        var snapshot = await SaveEditAsync(page, baseUrl, orderPath, orderApi, number, edited, 2, 42.4375m);
        Assert.Equal(supplierId, snapshot.GetProperty("supplierId").GetGuid());
        Assert.Equal(supplierName, snapshot.GetProperty("supplierName").GetString());
        Assert.Equal(taxNumber, snapshot.GetProperty("supplierTaxNumber").GetString());
        Assert.Equal("30 Supplier Street", snapshot.GetProperty("supplierAddress").GetProperty("line1").GetString());
        Assert.Equal("12345", snapshot.GetProperty("supplierAddress").GetProperty("postalCode").GetString());
        Assert.Equal(date, snapshot.GetProperty("orderDate").GetString());
        Assert.Equal(delivery, snapshot.GetProperty("expectedDeliveryDate").GetString());
        Assert.Equal(44.5m, snapshot.GetProperty("subtotal").GetDecimal());
        Assert.Equal(3.75m, snapshot.GetProperty("discountTotal").GetDecimal());
        Assert.Equal(1.6875m, snapshot.GetProperty("taxTotal").GetDecimal());

        // Changing the live contact must not rewrite the already-saved document snapshot.
        var updateSupplier = await page.Context.APIRequest.PutAsync(Url(baseUrl, $"/api/v1/contacts/{supplierId}"), new()
        {
            DataObject = new
            {
                kind = 1,
                displayName = $"Changed supplier {suffix}",
                organizationName = $"Changed supplier {suffix}",
                isSupplier = true,
                taxNumber = $"CHANGED-{suffix}",
                concurrencyStamp = supplier.GetProperty("concurrencyStamp").GetString(),
                billingAddress = new { line1 = "99 Changed Avenue", city = "Changed City", countryCode = "US" }
            }
        });
        Assert.True(updateSupplier.Ok, $"Supplier update returned HTTP {updateSupplier.Status}.");
        AssertSnapshot(snapshot, await GetAsync(page, baseUrl, orderApi));

        await FollowAsync(page, "Send/confirm");
        await page.GetByRole(AriaRole.Button, new() { Name = "Send", Exact = true }).ClickAsync();
        await ExpectPathAsync(page, orderPath);
        await ExpectDocumentAsync(page, number, "Sent");
        var sent = await GetAsync(page, baseUrl, orderApi);
        Assert.NotEqual(snapshot.GetProperty("concurrencyStamp").GetString(), sent.GetProperty("concurrencyStamp").GetString());
        AssertSnapshot(snapshot, sent);
        await RejectTransitionAsync(page, baseUrl, $"{orderApi}/confirm", snapshot, 409);
        Assert.Equal(sent.GetRawText(), (await GetAsync(page, baseUrl, orderApi)).GetRawText());

        await FollowAsync(page, "Send/confirm");
        await page.GetByRole(AriaRole.Button, new() { Name = "Confirm", Exact = true }).ClickAsync();
        await ExpectPathAsync(page, orderPath);
        await ExpectDocumentAsync(page, number, "Confirmed");
        var confirmed = await GetAsync(page, baseUrl, orderApi);
        Assert.NotEqual(sent.GetProperty("concurrencyStamp").GetString(), confirmed.GetProperty("concurrencyStamp").GetString());
        AssertSnapshot(snapshot, confirmed);
        await RejectTransitionAsync(page, baseUrl, $"{orderApi}/send", confirmed, 400);
        await RejectTransitionAsync(page, baseUrl, $"{orderApi}/archive", confirmed, 400);
        Assert.Equal(confirmed.GetRawText(), (await GetAsync(page, baseUrl, orderApi)).GetRawText());

        await FollowAsync(page, "Cancel");
        await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await ExpectPathAsync(page, orderPath);
        await ExpectDocumentAsync(page, number, "Cancelled");
        var cancelled = await GetAsync(page, baseUrl, orderApi);
        Assert.NotEqual(confirmed.GetProperty("concurrencyStamp").GetString(), cancelled.GetProperty("concurrencyStamp").GetString());
        AssertSnapshot(snapshot, cancelled);

        var current = cancelled;
        foreach (var action in new[] { (Name: "archive", Archived: true), (Name: "reactivate", Archived: false) })
        {
            var result = await PostAsync(page, baseUrl, $"{orderApi}/{action.Name}", new { concurrencyStamp = current.GetProperty("concurrencyStamp").GetString() });
            Assert.NotEqual(current.GetProperty("concurrencyStamp").GetString(), result.GetProperty("concurrencyStamp").GetString());
            current = await GetAsync(page, baseUrl, orderApi);
            Assert.Equal(action.Archived, current.GetProperty("isArchived").GetBoolean());
            Assert.Equal(5, current.GetProperty("state").GetInt32());
            AssertSnapshot(snapshot, current);
            var visible = await GetAsync(page, baseUrl, $"/api/v1/purchasing/orders/?search={number}&includeArchived=false&sort=number&descending=false&page=1&pageSize=25");
            Assert.Equal(action.Archived ? 0 : 1, visible.GetProperty("total").GetInt32());
            var all = await GetAsync(page, baseUrl, $"/api/v1/purchasing/orders/?search={number}&includeArchived=true&sort=number&descending=false&page=1&pageSize=25");
            Assert.Equal(orderId, all.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
        }

        await VisitAsync(page, baseUrl, orderPath, number);
        await VerifyViewportAsync(page, number);
        await VisitAsync(page, baseUrl, "/purchasing/orders", "Purchase orders");
        await page.GetByLabel("Search", new() { Exact = true }).FillAsync(number);
        await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Link, new() { Name = number, Exact = true })).ToBeVisibleAsync();
        await VerifyViewportAsync(page, "Purchase orders");
    }

    private static void AssertSnapshot(JsonElement expected, JsonElement actual)
    {
        foreach (var field in new[] { "id", "number", "supplierId", "supplierName", "supplierTaxNumber", "supplierAddress", "currencyCode", "baseCurrencyCode", "exchangeRate", "orderDate", "expectedDeliveryDate", "subtotal", "discountTotal", "taxTotal", "total", "notes", "createdAtUtc", "lines" })
            Assert.Equal(expected.GetProperty(field).GetRawText(), actual.GetProperty(field).GetRawText());
    }

    private static async Task<JsonElement> SaveEditAsync(IPage page, string baseUrl, string path, string api, string number, JsonElement previous, int count, decimal total)
    {
        await SaveAsync(page);
        await ExpectPathAsync(page, path);
        await ExpectDocumentAsync(page, number, "Draft");
        var edited = await GetAsync(page, baseUrl, api);
        Assert.Equal(count, edited.GetProperty("lines").GetArrayLength());
        Assert.Equal(total, edited.GetProperty("total").GetDecimal());
        Assert.NotEqual(previous.GetProperty("concurrencyStamp").GetString(), edited.GetProperty("concurrencyStamp").GetString());
        var previousIds = previous.GetProperty("lines").EnumerateArray().Select(line => line.GetProperty("id").GetGuid()).ToHashSet();
        Assert.All(edited.GetProperty("lines").EnumerateArray(), line => Assert.DoesNotContain(line.GetProperty("id").GetGuid(), previousIds));
        return edited;
    }

    private static Task SaveAsync(IPage page) => page.GetByRole(AriaRole.Button, new() { Name = "Save purchase order", Exact = true }).ClickAsync();

    private static async Task EditAsync(IPage page)
    {
        await FollowAsync(page, "Edit");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Edit purchase order", Level = 1, Exact = true })).ToBeVisibleAsync();
    }

    private static async Task RejectTransitionAsync(IPage page, string baseUrl, string path, JsonElement document, int status)
    {
        var response = await page.Context.APIRequest.PostAsync(Url(baseUrl, path), new() { DataObject = new { concurrencyStamp = document.GetProperty("concurrencyStamp").GetString() } });
        Assert.Equal(status, response.Status);
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
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), $"Purchasing page {context} overflows at width {width}.");
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
        Assert.True(response.Ok, $"Purchasing navigation returned HTTP {response.Status}.");
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
        Assert.True(response.Ok, $"Purchasing read returned HTTP {response.Status}.");
        using var document = JsonDocument.Parse(await response.TextAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> PostAsync(IPage page, string baseUrl, string path, object request)
    {
        var response = await page.Context.APIRequest.PostAsync(Url(baseUrl, path), new() { DataObject = request });
        Assert.True(response.Ok, $"Purchasing setup or transition returned HTTP {response.Status}.");
        using var document = JsonDocument.Parse(await response.TextAsync());
        return document.RootElement.Clone();
    }

    private static string Url(string baseUrl, string path) => new Uri(new Uri(baseUrl), path).ToString();

    [GeneratedRegex("/purchasing/orders/[0-9a-f-]{36}$")]
    private static partial Regex OrderUrl();
}
