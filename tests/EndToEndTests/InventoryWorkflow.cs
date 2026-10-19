using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Erp.EndToEndTests;

internal static partial class InventoryWorkflow
{
    public static async Task VerifyAsync(IPage page, string baseUrl, string productSku)
    {
        var products = await GetAsync(page, baseUrl, $"/api/v1/products/?search={productSku}");
        var product = products.GetProperty("items").EnumerateArray().Single(item => item.GetProperty("sku").GetString() == productSku);
        var productId = product.GetProperty("id").GetGuid();
        var tracked = await page.Context.APIRequest.PutAsync(Url(baseUrl, $"/api/v1/products/{productId}"), new()
        {
            DataObject = new
            {
                type = product.GetProperty("type").GetInt32(),
                sku = productSku,
                name = product.GetProperty("name").GetString(),
                description = product.GetProperty("description").GetString(),
                barcode = product.GetProperty("barcode").GetString(),
                salePrice = product.GetProperty("salePrice").GetDecimal(),
                cost = product.GetProperty("cost").GetDecimal(),
                currencyCode = product.GetProperty("currencyCode").GetString(),
                salesUnitCode = product.GetProperty("salesUnitCode").GetString(),
                purchaseUnitCode = product.GetProperty("purchaseUnitCode").GetString(),
                tracksInventory = true,
                concurrencyStamp = product.GetProperty("concurrencyStamp").GetString()
            }
        });
        Assert.True(tracked.Ok, $"Inventory product setup returned HTTP {tracked.Status}.");
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var warehouse = await PostAsync(page, baseUrl, "/api/v1/inventory/warehouses", new { code = $"E2E-{suffix}", name = $"Browser warehouse {suffix}" });
        var warehouseId = warehouse.GetProperty("id").GetGuid();
        var locations = new Dictionary<string, Guid>();
        foreach (var location in new[] { (Name: "A", Type: 0), (Name: "B", Type: 0), (Name: "SUPPLIER", Type: 1), (Name: "CUSTOMER", Type: 2), (Name: "LOSS", Type: 3), (Name: "GAIN", Type: 4) })
        {
            var result = await PostAsync(page, baseUrl, "/api/v1/inventory/locations", new
            {
                warehouseId = location.Type == 0 ? (Guid?)warehouseId : null,
                code = $"{location.Name}-{suffix}",
                name = $"Browser {location.Name} {suffix}",
                type = location.Type
            });
            locations[location.Name] = result.GetProperty("id").GetGuid();
        }
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var documents = await CreateSourceDocumentsAsync(page, baseUrl, product, suffix, date);
        var purchaseId = documents.Purchase.GetProperty("id").GetGuid();
        var salesId = documents.Sales.GetProperty("id").GetGuid();
        var purchaseNumber = documents.Purchase.GetProperty("number").GetString()!;
        var salesNumber = documents.Sales.GetProperty("number").GetString()!;

        await VerifyWarehouseFilterUiAsync(page, baseUrl, suffix);
        var receipt = await CreateMoveUiAsync(page, baseUrl, productSku, productId, "Receipt", 10, locations["SUPPLIER"], locations["A"], date, purchaseId, purchaseNumber);
        receipt = await CompleteUiAsync(page, baseUrl, receipt);
        await AssertBalanceAsync(page, baseUrl, productId, locations["A"], 10, 0);
        var transfer = await CreateMoveUiAsync(page, baseUrl, productSku, productId, "Transfer", 3, locations["A"], locations["B"], date);
        await CompleteUiAsync(page, baseUrl, transfer);
        await AssertBalanceAsync(page, baseUrl, productId, locations["A"], 7, 0);
        await AssertBalanceAsync(page, baseUrl, productId, locations["B"], 3, 0);
        var gain = await CreateMoveUiAsync(page, baseUrl, productSku, productId, "Adjustment", 2, locations["GAIN"], locations["A"], date);
        await CompleteUiAsync(page, baseUrl, gain);
        await AssertBalanceAsync(page, baseUrl, productId, locations["A"], 9, 0);
        var loss = await CreateMoveUiAsync(page, baseUrl, productSku, productId, "Adjustment", 1, locations["A"], locations["LOSS"], date);
        loss = await CompleteUiAsync(page, baseUrl, loss);
        await AssertBalanceAsync(page, baseUrl, productId, locations["A"], 8, 0);

        var originalPath = MovePath(loss);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create correction", Exact = true }).ClickAsync();
        await Assertions.Expect(page).Not.ToHaveURLAsync(Url(baseUrl, originalPath), new() { Timeout = 30_000 });
        await Assertions.Expect(page).ToHaveURLAsync(MoveUrl(), new() { Timeout = 30_000 });
        await ReadyAsync(page);
        var correction = await GetAsync(page, baseUrl, $"/api/v1/inventory/moves/{new Uri(page.Url).Segments[^1]}");
        Assert.Equal(loss.GetProperty("id").GetGuid(), correction.GetProperty("reversesMoveId").GetGuid());
        Assert.Equal(locations["LOSS"], correction.GetProperty("sourceLocationId").GetGuid());
        Assert.Equal(locations["A"], correction.GetProperty("destinationLocationId").GetGuid());
        var sameCorrection = await PostAsync(page, baseUrl, $"/api/v1/inventory/moves/{loss.GetProperty("id").GetGuid()}/correct");
        Assert.Equal(correction.GetProperty("id").GetGuid(), sameCorrection.GetProperty("id").GetGuid());
        await CompleteUiAsync(page, baseUrl, correction);
        await AssertBalanceAsync(page, baseUrl, productId, locations["A"], 9, 0);
        Assert.Equal(loss.GetRawText(), (await GetAsync(page, baseUrl, $"/api/v1/inventory/moves/{loss.GetProperty("id").GetGuid()}")).GetRawText());

        // These known keys exercise retries; UI completion deliberately generates its own keys.
        var idempotent = await PostAsync(page, baseUrl, "/api/v1/inventory/moves", MoveRequest(0, productId, 1, locations["GAIN"], locations["B"], date));
        var completeRequest = new { concurrencyStamp = Stamp(idempotent), idempotencyKey = $"browser-complete-{suffix}" };
        var idempotentPath = $"/api/v1/inventory/moves/{idempotent.GetProperty("id").GetGuid()}/complete";
        var completed = await PostAsync(page, baseUrl, idempotentPath, completeRequest);
        var retried = await PostAsync(page, baseUrl, idempotentPath, completeRequest);
        Assert.Equal(completed.GetProperty("id").GetGuid(), retried.GetProperty("id").GetGuid());
        Assert.Equal(Stamp(completed), Stamp(retried));
        await AssertBalanceAsync(page, baseUrl, productId, locations["B"], 4, 0);

        var temporaryRequest = ReservationRequest(productId, locations["A"], 1, salesId, $"browser-reserve-retry-{suffix}");
        var temporary = await PostAsync(page, baseUrl, "/api/v1/inventory/reservations", temporaryRequest);
        var repeated = await PostAsync(page, baseUrl, "/api/v1/inventory/reservations", temporaryRequest);
        Assert.Equal(temporary.GetProperty("id").GetGuid(), repeated.GetProperty("id").GetGuid());
        Assert.Equal(Stamp(temporary), Stamp(repeated));
        await AssertBalanceAsync(page, baseUrl, productId, locations["A"], 9, 1);
        var releasePath = $"/api/v1/inventory/reservations/{temporary.GetProperty("id").GetGuid()}/release";
        var releaseRequest = new { concurrencyStamp = Stamp(temporary), idempotencyKey = $"browser-release-retry-{suffix}" };
        var released = await PostAsync(page, baseUrl, releasePath, releaseRequest);
        var releasedAgain = await PostAsync(page, baseUrl, releasePath, releaseRequest);
        Assert.Equal(Stamp(released), Stamp(releasedAgain));
        Assert.Equal(1, releasedAgain.GetProperty("state").GetInt32());
        await AssertBalanceAsync(page, baseUrl, productId, locations["A"], 9, 0);

        var reservation = await PostAsync(page, baseUrl, "/api/v1/inventory/reservations", ReservationRequest(productId, locations["A"], 4, salesId, $"browser-delivery-{suffix}"));
        await AssertBalanceAsync(page, baseUrl, productId, locations["A"], 9, 4);
        var firstDelivery = await CreateMoveUiAsync(page, baseUrl, productSku, productId, "Delivery", 2, locations["A"], locations["CUSTOMER"], date, salesId, salesNumber);
        await CompleteUiAsync(page, baseUrl, firstDelivery);
        await AssertBalanceAsync(page, baseUrl, productId, locations["A"], 7, 2);
        var reservationPath = $"/inventory/reservations/{reservation.GetProperty("id").GetGuid()}";
        await VisitAsync(page, baseUrl, reservationPath, productId.ToString());
        await Assertions.Expect(page.Locator("main dd").GetByText("Active", new() { Exact = true })).ToBeVisibleAsync();
        await VerifyViewportAsync(page, "Stock reservation details");
        await page.GetByRole(AriaRole.Button, new() { Name = "Release remaining quantity", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("main dd").GetByText("Released", new() { Exact = true })).ToBeVisibleAsync();
        var partiallyReleased = await GetAsync(page, baseUrl, $"/api/v1/inventory/reservations/{reservation.GetProperty("id").GetGuid()}");
        Assert.Equal(2m, partiallyReleased.GetProperty("consumedQuantity").GetDecimal());
        Assert.Equal(2m, partiallyReleased.GetProperty("remainingQuantity").GetDecimal());
        await AssertBalanceAsync(page, baseUrl, productId, locations["A"], 7, 0);

        var finalReservation = await PostAsync(page, baseUrl, "/api/v1/inventory/reservations", ReservationRequest(productId, locations["A"], 2, salesId, $"browser-final-delivery-{suffix}"));
        var lastDelivery = await CreateMoveUiAsync(page, baseUrl, productSku, productId, "Delivery", 2, locations["A"], locations["CUSTOMER"], date, salesId, salesNumber);
        await CompleteUiAsync(page, baseUrl, lastDelivery);
        var consumed = await GetAsync(page, baseUrl, $"/api/v1/inventory/reservations/{finalReservation.GetProperty("id").GetGuid()}");
        Assert.Equal(2, consumed.GetProperty("state").GetInt32());
        Assert.Equal(2m, consumed.GetProperty("consumedQuantity").GetDecimal());
        Assert.Equal(0m, consumed.GetProperty("remainingQuantity").GetDecimal());
        await AssertBalanceAsync(page, baseUrl, productId, locations["A"], 5, 0);

        foreach (var request in new[]
        {
            MoveRequest(2, productId, 100, locations["A"], locations["B"], date),
            MoveRequest(0, productId, 1, locations["SUPPLIER"], locations["A"], date, "PurchaseOrder", purchaseId),
            MoveRequest(1, productId, 1, locations["A"], locations["CUSTOMER"], date, "SalesOrder", salesId)
        })
        {
            var draft = await PostAsync(page, baseUrl, "/api/v1/inventory/moves", request);
            var api = $"/api/v1/inventory/moves/{draft.GetProperty("id").GetGuid()}";
            var before = await GetAsync(page, baseUrl, api);
            var failure = await page.Context.APIRequest.PostAsync(Url(baseUrl, $"{api}/complete"), new() { DataObject = new { concurrencyStamp = Stamp(draft), idempotencyKey = Guid.NewGuid().ToString("N") } });
            Assert.Equal(400, failure.Status);
            Assert.Equal(before.GetRawText(), (await GetAsync(page, baseUrl, api)).GetRawText());
            await VisitAsync(page, baseUrl, MovePath(draft), draft.GetProperty("number").GetString()!);
            await page.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
            await ExpectStateAsync(page, "Cancelled");
        }
        await AssertBalanceAsync(page, baseUrl, productId, locations["A"], 5, 0);
        await AssertBalanceAsync(page, baseUrl, productId, locations["B"], 4, 0);
        await AssertFulfilledAsync(page, baseUrl, productId, "PurchaseOrder", purchaseId, 10);
        await AssertFulfilledAsync(page, baseUrl, productId, "SalesOrder", salesId, 4);
        Assert.Equal(documents.Purchase.GetRawText(), (await GetAsync(page, baseUrl, $"/api/v1/purchasing/orders/{purchaseId}")).GetRawText());
        Assert.Equal(documents.Sales.GetRawText(), (await GetAsync(page, baseUrl, $"/api/v1/sales/orders/{salesId}")).GetRawText());
        var balances = await GetAsync(page, baseUrl, $"/api/v1/inventory/balances?productId={productId}&warehouseId={warehouseId}&includeZero=true&sort=location&descending=false&page=1&pageSize=100");
        Assert.Equal(2, balances.GetProperty("total").GetInt32());
        foreach (var balance in balances.GetProperty("items").EnumerateArray())
        {
            Assert.Equal(9m, balance.GetProperty("warehouseOnHand").GetDecimal());
            Assert.Equal(0m, balance.GetProperty("warehouseReserved").GetDecimal());
            Assert.Equal(9m, balance.GetProperty("warehouseAvailable").GetDecimal());
        }
        await VerifyBatchUiAsync(page, baseUrl, product, locations["GAIN"], locations["A"], date, suffix);
        foreach (var route in new[] { (Path: "/inventory/moves", Heading: "Inventory movements"), (Path: "/inventory/balances", Heading: "Stock balances"), (Path: "/inventory/reservations", Heading: "Stock reservations") })
        {
            await VisitAsync(page, baseUrl, route.Path, route.Heading);
            await Assertions.Expect(page.Locator("main table tbody tr").First).ToBeVisibleAsync();
            var tableRegion = page.GetByRole(AriaRole.Region, new() { Name = route.Heading, Exact = true });
            await tableRegion.FocusAsync();
            await Assertions.Expect(tableRegion).ToBeFocusedAsync();
            await VerifyViewportAsync(page, route.Heading);
        }
        await VisitAsync(page, baseUrl, MovePath(receipt), receipt.GetProperty("number").GetString()!);
        await VerifyViewportAsync(page, "Inventory movement details");
    }

    private static async Task VerifyWarehouseFilterUiAsync(IPage page, string baseUrl, string suffix)
    {
        await VisitAsync(page, baseUrl, "/inventory/warehouses", "Warehouses and locations");
        var filter = page.GetByLabel("Filter locations", new() { Exact = true });
        await filter.FillAsync($"A-{suffix}");
        var locations = page.GetByRole(AriaRole.Region, new() { Name = "Locations", Exact = true });
        await Assertions.Expect(locations.Locator("tbody tr")).ToHaveCountAsync(1);
        await Assertions.Expect(locations.GetByRole(AriaRole.Cell, new() { Name = $"A-{suffix}", Exact = true })).ToBeVisibleAsync();
        await filter.FillAsync("");
        await Assertions.Expect(locations.GetByRole(AriaRole.Cell, new() { Name = $"GAIN-{suffix}", Exact = true })).ToBeVisibleAsync();
        foreach (var name in new[] { "Warehouses", "Locations" })
        {
            var region = page.GetByRole(AriaRole.Region, new() { Name = name, Exact = true });
            await region.FocusAsync();
            await Assertions.Expect(region).ToBeFocusedAsync();
        }
        await VerifyViewportAsync(page, "Warehouses and locations");
        Assert.Equal(0, await page.Locator(".error-panel, main [role='alert']").CountAsync());
    }

    private static async Task VerifyBatchUiAsync(IPage page, string baseUrl, JsonElement product, Guid source, Guid destination, string date, string suffix)
    {
        var productId = product.GetProperty("id").GetGuid();
        var productSku = product.GetProperty("sku").GetString()!;
        var secondProduct = await PostAsync(page, baseUrl, "/api/v1/products/", new
        {
            type = 0,
            sku = $"E2E-BATCH-{suffix}",
            name = $"Browser batch product {suffix}",
            salePrice = 0,
            cost = 0,
            currencyCode = product.GetProperty("currencyCode").GetString(),
            salesUnitCode = product.GetProperty("salesUnitCode").GetString(),
            purchaseUnitCode = product.GetProperty("purchaseUnitCode").GetString(),
            tracksInventory = true
        });
        var secondId = secondProduct.GetProperty("id").GetGuid();
        var notes = $"Browser draft batch {suffix}";
        await VisitAsync(page, baseUrl, "/inventory/moves/batch/new", "New movement batch");
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Movement type", Exact = true }).SelectOptionAsync("Receipt");
        await page.GetByLabel("Business date", new() { Exact = true }).FillAsync(date);
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Source location", Exact = true }).SelectOptionAsync(source.ToString());
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Destination location", Exact = true }).SelectOptionAsync(destination.ToString());
        var filter = page.GetByLabel("Filter products", new() { Exact = true });
        await filter.FillAsync(productSku);
        var first = page.Locator("main .line-editor").First;
        await Assertions.Expect(first.Locator($"option[value='{productId}']")).ToHaveCountAsync(1);
        await Assertions.Expect(first.Locator($"option[value='{secondId}']")).ToHaveCountAsync(0);
        await filter.FillAsync("");
        await Assertions.Expect(first.Locator($"option[value='{secondId}']")).ToHaveCountAsync(1);
        await first.GetByRole(AriaRole.Combobox, new() { Name = "Product", Exact = true }).SelectOptionAsync(productId.ToString());
        await first.GetByLabel("Quantity", new() { Exact = true }).FillAsync("2");
        await page.GetByRole(AriaRole.Button, new() { Name = "Add line", Exact = true }).ClickAsync();
        var second = page.Locator("main .line-editor").Nth(1);
        await second.GetByRole(AriaRole.Combobox, new() { Name = "Product", Exact = true }).SelectOptionAsync(secondId.ToString());
        await second.GetByLabel("Quantity", new() { Exact = true }).FillAsync("3");
        await Assertions.Expect(first.Locator($"option[value='{secondId}']")).ToHaveCountAsync(0);
        var firstProductOptions = await first.Locator("option").CountAsync();
        var secondProductOptions = await second.Locator("option").CountAsync();
        await filter.FillAsync($"NO-MATCH-{suffix}");
        await Assertions.Expect(first.Locator("option")).ToHaveCountAsync(2);
        await Assertions.Expect(second.Locator("option")).ToHaveCountAsync(2);
        await Assertions.Expect(first.Locator($"option[value='{productId}']")).ToHaveCountAsync(1);
        await Assertions.Expect(second.Locator($"option[value='{secondId}']")).ToHaveCountAsync(1);
        await Assertions.Expect(first.GetByRole(AriaRole.Combobox, new() { Name = "Product", Exact = true })).ToHaveValueAsync(productId.ToString());
        await Assertions.Expect(second.GetByRole(AriaRole.Combobox, new() { Name = "Product", Exact = true })).ToHaveValueAsync(secondId.ToString());
        await filter.FillAsync("");
        await Assertions.Expect(first.Locator("option")).ToHaveCountAsync(firstProductOptions);
        await Assertions.Expect(second.Locator("option")).ToHaveCountAsync(secondProductOptions);
        await Assertions.Expect(first.GetByRole(AriaRole.Combobox, new() { Name = "Product", Exact = true })).ToHaveValueAsync(productId.ToString());
        await Assertions.Expect(second.GetByRole(AriaRole.Combobox, new() { Name = "Product", Exact = true })).ToHaveValueAsync(secondId.ToString());
        await page.GetByLabel("Notes", new() { Exact = true }).FillAsync(notes);
        await VerifyViewportAsync(page, "New movement batch");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create all drafts", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("main .notice-success[role='status'] strong")).ToHaveTextAsync("2 drafts created.");
        await Assertions.Expect(page.Locator("main .created-item")).ToHaveCountAsync(2);
        var expected = new Dictionary<Guid, decimal> { [productId] = 2, [secondId] = 3 };
        var seenMoves = new HashSet<Guid>();
        var seenProducts = new HashSet<Guid>();
        foreach (var link in await page.Locator("main .created-item").AllAsync())
        {
            var href = await link.GetAttributeAsync("href");
            Assert.NotNull(href);
            var moveId = Guid.Parse(new Uri(new Uri(baseUrl), href).Segments[^1]);
            Assert.True(seenMoves.Add(moveId));
            var draft = await GetAsync(page, baseUrl, $"/api/v1/inventory/moves/{moveId}");
            var draftProduct = draft.GetProperty("productId").GetGuid();
            Assert.True(seenProducts.Add(draftProduct));
            Assert.Equal(expected[draftProduct], draft.GetProperty("quantity").GetDecimal());
            Assert.StartsWith("MOVE-", draft.GetProperty("number").GetString()!, StringComparison.Ordinal);
            Assert.Equal(0, draft.GetProperty("state").GetInt32());
            Assert.Equal(0, draft.GetProperty("type").GetInt32());
            Assert.Equal(source, draft.GetProperty("sourceLocationId").GetGuid());
            Assert.Equal(destination, draft.GetProperty("destinationLocationId").GetGuid());
            Assert.Equal(date, draft.GetProperty("businessDate").GetString());
            Assert.Equal(notes, draft.GetProperty("notes").GetString());
        }
        await AssertBalanceAsync(page, baseUrl, productId, destination, 5, 0);
        await AssertBalanceAsync(page, baseUrl, secondId, destination, 0, 0);
        await VerifyViewportAsync(page, "Created movement batch drafts");
    }

    private static async Task<(JsonElement Purchase, JsonElement Sales)> CreateSourceDocumentsAsync(IPage page, string baseUrl, JsonElement product, string suffix, string date)
    {
        var contact = await PostAsync(page, baseUrl, "/api/v1/contacts/", new { kind = 1, displayName = $"Browser inventory party {suffix}", organizationName = $"Browser inventory party {suffix}", isCustomer = true, isSupplier = true, taxNumber = $"INV-{suffix}", billingAddress = new { line1 = "40 Inventory Street", city = "Test City", countryCode = "US" }, shippingAddress = new { line1 = "50 Inventory Avenue", city = "Test City", countryCode = "US" } });
        var contactId = contact.GetProperty("id").GetGuid();
        var productId = product.GetProperty("id").GetGuid();
        var currencyCode = product.GetProperty("currencyCode").GetString();
        var purchase = await PostAsync(page, baseUrl, "/api/v1/purchasing/orders/", new { supplierId = contactId, currencyCode, orderDate = date, lines = new[] { new { productId, quantity = 10, unitPrice = 5 } } });
        var purchaseApi = $"/api/v1/purchasing/orders/{purchase.GetProperty("id").GetGuid()}";
        await PostAsync(page, baseUrl, $"{purchaseApi}/confirm", new { concurrencyStamp = Stamp(purchase) });
        var quotation = await PostAsync(page, baseUrl, "/api/v1/sales/quotations/", new { customerId = contactId, currencyCode, quotationDate = date, lines = new[] { new { productId, quantity = 4, unitPrice = 10 } } });
        var quotationApi = $"/api/v1/sales/quotations/{quotation.GetProperty("id").GetGuid()}";
        foreach (var state in new[] { "sent", "accepted" }) quotation = await PostAsync(page, baseUrl, $"{quotationApi}/{state}", new { concurrencyStamp = Stamp(quotation) });
        var sales = await PostAsync(page, baseUrl, $"{quotationApi}/convert", new { concurrencyStamp = Stamp(quotation), orderDate = date });
        var salesApi = $"/api/v1/sales/orders/{sales.GetProperty("id").GetGuid()}";
        await PostAsync(page, baseUrl, $"{salesApi}/confirm", new { concurrencyStamp = Stamp(sales) });
        return (await GetAsync(page, baseUrl, purchaseApi), await GetAsync(page, baseUrl, salesApi));
    }

    private static async Task<JsonElement> CreateMoveUiAsync(IPage page, string baseUrl, string sku, Guid productId, string type, decimal quantity, Guid source, Guid destination, string date, Guid? documentId = null, string? documentNumber = null)
    {
        await VisitAsync(page, baseUrl, "/inventory/moves/new", "New inventory movement");
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Create draft", Exact = true })).ToBeVisibleAsync();
        await page.GetByRole(AriaRole.Combobox, new() { Name = "Type", Exact = true }).SelectOptionAsync(type);
        if (documentId is not null)
        {
            await page.GetByLabel("Search source documents", new() { Exact = true }).FillAsync(documentNumber!);
            await page.GetByRole(AriaRole.Button, new() { Name = "Search", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Combobox, new() { Name = "Source document", Exact = true }).SelectOptionAsync(documentId.Value.ToString());
        }
        var productFilter = page.GetByLabel("Search products", new() { Exact = true });
        var productSelect = page.GetByRole(AriaRole.Combobox, new() { Name = "Product", Exact = true });
        await productFilter.FillAsync(sku);
        await Assertions.Expect(productSelect.Locator($"option[value='{productId}']")).ToHaveCountAsync(1);
        await productSelect.SelectOptionAsync(productId.ToString());
        await productFilter.FillAsync($"NO-MATCH-{productId:N}");
        await Assertions.Expect(productSelect.Locator($"option[value='{productId}']")).ToHaveCountAsync(1);
        await Assertions.Expect(productSelect).ToHaveValueAsync(productId.ToString());
        await productFilter.FillAsync("");
        await Assertions.Expect(productSelect).ToHaveValueAsync(productId.ToString());
        await page.GetByLabel("Quantity", new() { Exact = true }).FillAsync(quantity.ToString(CultureInfo.InvariantCulture));
        await page.GetByLabel("Business date", new() { Exact = true }).FillAsync(date);
        var sourceSelect = page.GetByRole(AriaRole.Combobox, new() { Name = "Source location", Exact = true });
        var destinationSelect = page.GetByRole(AriaRole.Combobox, new() { Name = "Destination location", Exact = true });
        await sourceSelect.SelectOptionAsync(source.ToString());
        await destinationSelect.SelectOptionAsync(destination.ToString());
        var sourceOptions = await sourceSelect.Locator("option").CountAsync();
        var destinationOptions = await destinationSelect.Locator("option").CountAsync();
        var retainedSourceOptions = 1 + await sourceSelect.Locator($"option[value='{source}'], option[value='{destination}']").CountAsync();
        var retainedDestinationOptions = 1 + await destinationSelect.Locator($"option[value='{source}'], option[value='{destination}']").CountAsync();
        var locationFilter = page.GetByLabel("Filter locations", new() { Exact = true });
        await locationFilter.FillAsync($"NO-MATCH-{source:N}");
        await Assertions.Expect(sourceSelect.Locator("option")).ToHaveCountAsync(retainedSourceOptions);
        await Assertions.Expect(destinationSelect.Locator("option")).ToHaveCountAsync(retainedDestinationOptions);
        await Assertions.Expect(sourceSelect.Locator($"option[value='{source}']")).ToHaveCountAsync(1);
        await Assertions.Expect(destinationSelect.Locator($"option[value='{destination}']")).ToHaveCountAsync(1);
        await Assertions.Expect(sourceSelect).ToHaveValueAsync(source.ToString());
        await Assertions.Expect(destinationSelect).ToHaveValueAsync(destination.ToString());
        await locationFilter.FillAsync("");
        await Assertions.Expect(sourceSelect.Locator("option")).ToHaveCountAsync(sourceOptions);
        await Assertions.Expect(destinationSelect.Locator("option")).ToHaveCountAsync(destinationOptions);
        await Assertions.Expect(sourceSelect).ToHaveValueAsync(source.ToString());
        await Assertions.Expect(destinationSelect).ToHaveValueAsync(destination.ToString());
        if (type == "Receipt") await VerifyViewportAsync(page, "New inventory movement");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create draft", Exact = true }).ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(MoveUrl(), new() { Timeout = 30_000 });
        await ReadyAsync(page);
        var move = await GetAsync(page, baseUrl, $"/api/v1/inventory/moves/{new Uri(page.Url).Segments[^1]}");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = move.GetProperty("number").GetString(), Level = 1, Exact = true })).ToBeVisibleAsync();
        await ExpectStateAsync(page, "Draft");
        return move;
    }

    private static async Task<JsonElement> CompleteUiAsync(IPage page, string baseUrl, JsonElement draft)
    {
        var complete = page.GetByRole(AriaRole.Button, new() { Name = "Complete", Exact = true });
        await Assertions.Expect(complete).ToBeEnabledAsync();
        await complete.ClickAsync();
        await ExpectStateAsync(page, "Completed");
        var completed = await GetAsync(page, baseUrl, $"/api/v1/inventory/moves/{draft.GetProperty("id").GetGuid()}");
        Assert.NotEqual(Stamp(draft), Stamp(completed));
        foreach (var field in new[] { "id", "number", "type", "productId", "productSku", "unitCode", "quantity", "sourceLocationId", "destinationLocationId", "referenceDocumentType", "referenceDocumentId", "businessDate", "notes", "reversesMoveId", "createdAtUtc" })
            Assert.Equal(draft.GetProperty(field).GetRawText(), completed.GetProperty(field).GetRawText());
        return completed;
    }

    private static async Task AssertBalanceAsync(IPage page, string baseUrl, Guid productId, Guid locationId, decimal onHand, decimal reserved)
    {
        var balance = await GetAsync(page, baseUrl, $"/api/v1/inventory/balances/{productId}/{locationId}");
        Assert.Equal(onHand, balance.GetProperty("onHand").GetDecimal());
        Assert.Equal(reserved, balance.GetProperty("reserved").GetDecimal());
        Assert.Equal(onHand - reserved, balance.GetProperty("available").GetDecimal());
    }

    private static async Task AssertFulfilledAsync(IPage page, string baseUrl, Guid productId, string documentType, Guid documentId, decimal quantity)
    {
        var moves = await GetAsync(page, baseUrl, $"/api/v1/inventory/moves?productId={productId}&referenceDocumentType={documentType}&referenceDocumentId={documentId}&state=Completed&sort=number&descending=false&page=1&pageSize=100");
        Assert.Equal(quantity, moves.GetProperty("items").EnumerateArray().Sum(move => move.GetProperty("quantity").GetDecimal()));
    }

    private static object MoveRequest(int type, Guid productId, decimal quantity, Guid sourceLocationId, Guid destinationLocationId, string businessDate, string? referenceDocumentType = null, Guid? referenceDocumentId = null) =>
        new { type, productId, quantity, sourceLocationId, destinationLocationId, businessDate, referenceDocumentType, referenceDocumentId };

    private static object ReservationRequest(Guid productId, Guid locationId, decimal quantity, Guid referenceDocumentId, string idempotencyKey) =>
        new { productId, locationId, quantity, idempotencyKey, referenceDocumentType = "SalesOrder", referenceDocumentId };

    private static async Task ExpectStateAsync(IPage page, string state)
    {
        await ReadyAsync(page);
        await Assertions.Expect(page.Locator("main dd").GetByText(state, new() { Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, await page.Locator(".error-panel, main [role='alert']").CountAsync());
    }

    private static async Task VerifyViewportAsync(IPage page, string context)
    {
        foreach (var width in new[] { 1440, 390 })
        {
            await page.SetViewportSizeAsync(width, 900);
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), $"Inventory page {context} overflows at width {width}.");
            Assert.Empty((await page.RunAxe()).Violations ?? []);
        }
        await page.SetViewportSizeAsync(1440, 1000);
    }

    private static async Task VisitAsync(IPage page, string baseUrl, string path, string heading)
    {
        var response = await page.GotoAsync(Url(baseUrl, path));
        Assert.NotNull(response);
        Assert.True(response.Ok, $"Inventory navigation returned HTTP {response.Status}.");
        await ReadyAsync(page);
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1, Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, await page.Locator(".error-panel, main [role='alert']").CountAsync());
    }

    private static Task ReadyAsync(IPage page) => Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");

    private static async Task<JsonElement> GetAsync(IPage page, string baseUrl, string path)
    {
        var response = await page.Context.APIRequest.GetAsync(Url(baseUrl, path));
        Assert.True(response.Ok, $"Inventory read returned HTTP {response.Status}.");
        using var json = JsonDocument.Parse(await response.TextAsync());
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> PostAsync(IPage page, string baseUrl, string path, object? request = null)
    {
        var response = await page.Context.APIRequest.PostAsync(Url(baseUrl, path), new() { DataObject = request });
        Assert.True(response.Ok, $"Inventory setup or action returned HTTP {response.Status}.");
        using var json = JsonDocument.Parse(await response.TextAsync());
        return json.RootElement.Clone();
    }

    private static string Stamp(JsonElement item) => item.GetProperty("concurrencyStamp").GetString()!;
    private static string MovePath(JsonElement item) => $"/inventory/moves/{item.GetProperty("id").GetGuid()}";
    private static string Url(string baseUrl, string path) => new Uri(new Uri(baseUrl), path).ToString();

    [GeneratedRegex("/inventory/moves/[0-9a-f-]{36}$")]
    private static partial Regex MoveUrl();
}
