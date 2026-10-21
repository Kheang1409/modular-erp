using System.Globalization;
using System.Text;
using System.Text.Json;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Erp.EndToEndTests;

internal static class ReportingWorkflow
{
    internal sealed record AccountingFixture(DateOnly BusinessDate, string Currency, Guid JournalId, IReadOnlyDictionary<string, Guid> Accounts);

    public static async Task VerifyAsync(IPage page, string baseUrl, string productSku, AccountingFixture accounting)
    {
        var products = await GetAsync(page, baseUrl, $"/api/v1/products/?search={productSku}");
        var product = products.GetProperty("items").EnumerateArray().Single(item => Text(item, "sku") == productSku);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        await SeedLedgerAsync(page, baseUrl, accounting, suffix);
        await SeedMovementsAsync(page, baseUrl, product, accounting.BusinessDate, suffix);
        var aging = await SeedAgingAsync(page, baseUrl, product, accounting, suffix);
        var from = new DateOnly(accounting.BusinessDate.Year, 1, 1);
        var to = new DateOnly(accounting.BusinessDate.Year, 12, 31);

        await VerifyPagedReportAsync(page, baseUrl, "/reports/financial", "Financial statements", "Run reports",
            "General ledger", "Stream ledger CSV", "/api/v1/reports/financial/general-ledger", from, to, 202, inventory: false);
        await VerifyPagedReportAsync(page, baseUrl, "/reports/inventory", "Inventory reconciliation", "Run report",
            "Movement history", "Stream movement CSV", "/api/v1/reports/inventory/movements", from, to, 26, inventory: true);
        await VerifyAgingAsync(page, baseUrl, accounting, aging, supplier: false);
        await VerifyAgingAsync(page, baseUrl, accounting, aging, supplier: true);
        await page.SetViewportSizeAsync(1440, 1000);
    }

    private static async Task SeedLedgerAsync(IPage page, string baseUrl, AccountingFixture fixture, string suffix)
    {
        // Repeated accounts deliberately cross page boundaries; distinct amounts expose unstable tie ordering.
        var lines = Enumerable.Range(1, 101).SelectMany(amount => new[]
        {
            new { accountId = fixture.Accounts["BANK"], description = $"Report debit {amount}", debit = (decimal)amount, credit = 0m },
            new { accountId = fixture.Accounts["REVENUE"], description = $"Report credit {amount}", debit = 0m, credit = (decimal)amount }
        }).ToArray();
        var draft = await PostAsync(page, baseUrl, "ledger-create", "/api/v1/accounting/entries", new
        {
            journalId = fixture.JournalId,
            businessDate = Date(fixture.BusinessDate),
            description = $"Browser reporting, \"pagination\" {suffix}",
            currencyCode = fixture.Currency,
            exchangeRate = 1,
            lines,
            idempotencyKey = $"report-entry-{suffix}"
        });
        Assert.Equal(202, draft.GetProperty("lines").GetArrayLength());
        var posted = await PostAsync(page, baseUrl, "ledger-post", $"/api/v1/accounting/entries/{Id(draft)}/post",
            new { concurrencyStamp = Stamp(draft), idempotencyKey = $"report-post-{suffix}" });
        Assert.Equal(1, posted.GetProperty("state").GetInt32());
        Assert.Equal(202, posted.GetProperty("lines").GetArrayLength());
    }

    private static async Task SeedMovementsAsync(IPage page, string baseUrl, JsonElement product, DateOnly date, string suffix)
    {
        var productId = Id(product);
        var moves = await GetAsync(page, baseUrl, $"/api/v1/inventory/moves?productId={productId}&state=Completed&sort=number&descending=false&page=1&pageSize=100");
        var locations = (await GetAsync(page, baseUrl, "/api/v1/inventory/locations")).EnumerateArray().ToDictionary(Id);
        var gain = moves.GetProperty("items").EnumerateArray().First(move =>
            move.GetProperty("sourceLocationId").ValueKind == JsonValueKind.String &&
            locations[move.GetProperty("sourceLocationId").GetGuid()].GetProperty("type").GetInt32() == 4 &&
            locations[move.GetProperty("destinationLocationId").GetGuid()].GetProperty("type").GetInt32() == 0);
        for (var index = 0; index < 26; index++)
        {
            var draft = await PostAsync(page, baseUrl, "movement-create", "/api/v1/inventory/moves", new
            {
                type = 3,
                productId,
                quantity = index + 1,
                sourceLocationId = gain.GetProperty("sourceLocationId").GetGuid(),
                destinationLocationId = gain.GetProperty("destinationLocationId").GetGuid(),
                businessDate = Date(date),
                notes = $"Browser report movement {suffix}-{index}"
            });
            var completed = await PostAsync(page, baseUrl, "movement-complete", $"/api/v1/inventory/moves/{Id(draft)}/complete",
                new { concurrencyStamp = Stamp(draft), idempotencyKey = $"report-move-{suffix}-{index}" });
            Assert.Equal(1, completed.GetProperty("state").GetInt32());
        }
    }

    private static async Task<AgingFixture> SeedAgingAsync(IPage page, string baseUrl, JsonElement product, AccountingFixture fixture, string suffix)
    {
        var start = new DateOnly(fixture.BusinessDate.Year, 1, 1);
        var party = await PostAsync(page, baseUrl, "aging-party-create", "/api/v1/contacts/", new
        {
            kind = 1,
            displayName = $"Browser report, \"aging\" {suffix}",
            organizationName = $"Browser report, \"aging\" {suffix}",
            isCustomer = true,
            isSupplier = true,
            taxNumber = $"REPORT-{suffix}",
            billingAddress = new { line1 = "70 Reporting Street", city = "Test City", countryCode = "US" }
        });
        var quote = await PostAsync(page, baseUrl, "aging-quotation-create", "/api/v1/sales/quotations/", new
        {
            customerId = Id(party),
            currencyCode = fixture.Currency,
            quotationDate = Date(start),
            lines = new[] { new { productId = Id(product), quantity = 1, unitPrice = 100, taxPercent = 0 } }
        });
        foreach (var action in new[] { "sent", "accepted" })
            quote = await PostAsync(page, baseUrl, action == "sent" ? "aging-quotation-send" : "aging-quotation-accept", $"/api/v1/sales/quotations/{Id(quote)}/{action}", new { concurrencyStamp = Stamp(quote) });
        var order = await PostAsync(page, baseUrl, "aging-order-convert", $"/api/v1/sales/quotations/{Id(quote)}/convert", new { concurrencyStamp = Stamp(quote), orderDate = Date(start) });
        await PostAsync(page, baseUrl, "aging-order-confirm", $"/api/v1/sales/orders/{Id(order)}/confirm", new { concurrencyStamp = Stamp(order) });
        var invoice = await PostAsync(page, baseUrl, "aging-invoice-create", "/api/v1/accounting/invoices/from-sales-order", new
        {
            salesOrderId = Id(order),
            invoiceDate = Date(start),
            dueDate = Date(start),
            idempotencyKey = $"report-invoice-{suffix}"
        });
        invoice = await PostAsync(page, baseUrl, "aging-invoice-post", $"/api/v1/accounting/invoices/{Id(invoice)}/post", new
        {
            journalId = fixture.JournalId,
            receivableAccountId = fixture.Accounts["AR"],
            revenueAccountId = fixture.Accounts["REVENUE"],
            concurrencyStamp = Stamp(invoice),
            idempotencyKey = $"report-invoice-post-{suffix}"
        });
        var purchase = await PostAsync(page, baseUrl, "aging-purchase-create", "/api/v1/purchasing/orders/", new
        {
            supplierId = Id(party),
            currencyCode = fixture.Currency,
            orderDate = Date(start),
            lines = new[] { new { productId = Id(product), quantity = 1, unitPrice = 60, taxPercent = 0 } }
        });
        await PostAsync(page, baseUrl, "aging-purchase-confirm", $"/api/v1/purchasing/orders/{Id(purchase)}/confirm", new { concurrencyStamp = Stamp(purchase) });
        var bill = await PostAsync(page, baseUrl, "aging-bill-create", "/api/v1/accounting/bills/from-purchase-order", new
        {
            purchaseOrderId = Id(purchase),
            supplierReference = $"REPORT-{suffix}",
            billDate = Date(start),
            dueDate = Date(start),
            idempotencyKey = $"report-bill-{suffix}"
        });
        bill = await PostAsync(page, baseUrl, "aging-bill-post", $"/api/v1/accounting/bills/{Id(bill)}/post", new
        {
            journalId = fixture.JournalId,
            payableAccountId = fixture.Accounts["AP"],
            expenseAccountId = fixture.Accounts["EXPENSE"],
            concurrencyStamp = Stamp(bill),
            idempotencyKey = $"report-bill-post-{suffix}"
        });
        foreach (var supplier in new[] { false, true })
        {
            var document = supplier ? bill : invoice;
            var partial = await PaymentAsync(page, baseUrl, fixture, party, document, start.AddDays(1), supplier ? 20m : 40m, supplier, $"report-partial-{supplier}-{suffix}");
            await PostAsync(page, baseUrl, supplier ? "aging-supplier-payment-reverse" : "aging-customer-payment-reverse", $"/api/v1/accounting/payments/{Id(partial)}/reverse",
                new { businessDate = Date(start.AddDays(2)), idempotencyKey = $"report-reverse-{supplier}-{suffix}" });
            await PaymentAsync(page, baseUrl, fixture, party, document, start.AddDays(3), supplier ? 60m : 100m, supplier, $"report-settle-{supplier}-{suffix}");
        }
        return new(Id(party), Text(party, "displayName"), start);
    }

    private static async Task<JsonElement> PaymentAsync(IPage page, string baseUrl, AccountingFixture fixture, JsonElement party,
        JsonElement document, DateOnly date, decimal amount, bool supplier, string key)
    {
        var draft = await PostAsync(page, baseUrl, supplier ? "aging-supplier-payment-create" : "aging-customer-payment-create", "/api/v1/accounting/payments", new
        {
            direction = supplier ? 1 : 0,
            partyId = Id(party),
            partyName = Text(party, "displayName"),
            paymentDate = Date(date),
            currencyCode = fixture.Currency,
            exchangeRate = 1,
            amount,
            reference = key,
            allocations = new[] { new { documentId = Id(document), amount } },
            idempotencyKey = key
        });
        return await PostAsync(page, baseUrl, supplier ? "aging-supplier-payment-post" : "aging-customer-payment-post", $"/api/v1/accounting/payments/{Id(draft)}/post", new
        {
            journalId = fixture.JournalId,
            bankAccountId = fixture.Accounts["BANK"],
            controlAccountId = fixture.Accounts[supplier ? "AP" : "AR"],
            concurrencyStamp = Stamp(draft),
            idempotencyKey = $"post-{key}"
        });
    }

    private static async Task VerifyPagedReportAsync(IPage page, string baseUrl, string route, string heading, string submit,
        string report, string csvLink, string endpoint, DateOnly from, DateOnly to, int minimumRows, bool inventory)
    {
        var rows = await ReadAllRowsAsync(page, baseUrl, endpoint, from, to);
        Assert.True(rows.Count >= minimumRows, $"The {report} fixture did not populate enough rows.");
        await VisitAsync(page, baseUrl, route, heading);
        await page.GetByLabel("From", new() { Exact = true }).FillAsync(Date(from));
        await page.GetByLabel("To / as of", new() { Exact = true }).FillAsync(Date(to));
        await page.GetByRole(AriaRole.Button, new() { Name = submit, Exact = true }).ClickAsync();
        await WaitForReportAsync(page);
        var pager = page.GetByRole(AriaRole.Navigation, new() { Name = $"{report} pagination", Exact = true });
        var size = page.GetByLabel($"{report} rows per page", new() { Exact = true });
        await Assertions.Expect(size).ToHaveValueAsync("100");
        await AssertPageAsync(page, report, rows, 1, 100, inventory);

        if (inventory)
        {
            await size.SelectOptionAsync("25");
            await WaitForReportAsync(page);
            await AssertPageAsync(page, report, rows, 1, 25, inventory);
        }
        var pageSize = inventory ? 25 : 100;
        var pageCount = (rows.Count + pageSize - 1) / pageSize;
        Assert.True(pageCount > 1, "The report must span multiple visible pages.");
        await Assertions.Expect(pager.GetByRole(AriaRole.Button, new() { Name = "Previous", Exact = true })).ToBeDisabledAsync();

        // Editing a date is not applying it: paging and export must retain the previous applied range.
        var pendingTo = from.AddDays(-1);
        await page.GetByLabel("To / as of", new() { Exact = true }).FillAsync(Date(pendingTo));
        var href = await page.GetByRole(AriaRole.Link, new() { Name = csvLink, Exact = true }).GetAttributeAsync("href");
        Assert.NotNull(href);
        Assert.Contains($"to={Date(to)}", href, StringComparison.Ordinal);
        for (var number = 2; number <= pageCount; number++)
        {
            await pager.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true }).ClickAsync();
            await WaitForReportAsync(page);
            await AssertPageAsync(page, report, rows, number, pageSize, inventory);
        }
        await Assertions.Expect(pager.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true })).ToBeDisabledAsync();
        await pager.GetByRole(AriaRole.Button, new() { Name = "Previous", Exact = true }).ClickAsync();
        await WaitForReportAsync(page);
        await AssertPageAsync(page, report, rows, pageCount - 1, pageSize, inventory);
        await AssertCsvAsync(page, baseUrl, href, rows, inventory ? "MoveId" : "EntryId", inventory ? "moveId" : "entryId", inventory);
        await VerifyViewportAsync(page, heading);

        await page.GetByRole(AriaRole.Button, new() { Name = submit, Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Alert)).ToContainTextAsync("From date must be on or before To date.");
        await page.GetByLabel("From", new() { Exact = true }).FillAsync(Date(pendingTo));
        await page.GetByRole(AriaRole.Button, new() { Name = submit, Exact = true }).ClickAsync();
        await WaitForReportAsync(page);
        await AssertPageAsync(page, report, [], 1, pageSize, inventory);
        await Assertions.Expect(pager.GetByRole(AriaRole.Button, new() { Name = "Previous", Exact = true })).ToBeDisabledAsync();
        await Assertions.Expect(pager.GetByRole(AriaRole.Button, new() { Name = "Next", Exact = true })).ToBeDisabledAsync();
        await page.GetByLabel("From", new() { Exact = true }).FillAsync(Date(from));
        await page.GetByLabel("To / as of", new() { Exact = true }).FillAsync(Date(to));
        await page.GetByRole(AriaRole.Button, new() { Name = submit, Exact = true }).ClickAsync();
        await WaitForReportAsync(page);
        await AssertPageAsync(page, report, rows, 1, pageSize, inventory);
    }

    private static async Task<List<JsonElement>> ReadAllRowsAsync(IPage page, string baseUrl, string endpoint, DateOnly from, DateOnly to)
    {
        List<JsonElement> rows = [];
        var number = 1;
        var total = int.MaxValue;
        while (rows.Count < total)
        {
            var result = (await GetAsync(page, baseUrl, $"{endpoint}?from={Date(from)}&to={Date(to)}&page={number++}&pageSize=100")).GetProperty("data");
            total = result.GetProperty("total").GetInt32();
            var batch = result.GetProperty("items").EnumerateArray().ToArray();
            Assert.True(batch.Length > 0 || total == 0, "Report pagination ended before its reported total.");
            rows.AddRange(batch);
        }
        Assert.Equal(total, rows.Count);
        return rows;
    }

    private static async Task AssertPageAsync(IPage page, string report, List<JsonElement> allRows, int number, int pageSize, bool inventory)
    {
        var total = allRows.Count;
        var first = total == 0 ? 0 : (number - 1) * pageSize + 1;
        var last = Math.Min(number * pageSize, total);
        var pages = Math.Max(1, (total + pageSize - 1) / pageSize);
        await Assertions.Expect(page.GetByRole(AriaRole.Status).Filter(new() { HasText = "Rows " }))
            .ToHaveTextAsync($"Rows {first}-{last} of {total} · Page {number} of {pages}");
        var region = page.GetByRole(AriaRole.Region, new() { Name = $"{report} table", Exact = true });
        var visible = region.Locator("tbody tr");
        var expected = allRows.Skip((number - 1) * pageSize).Take(pageSize).ToArray();
        if (expected.Length == 0)
        {
            Assert.DoesNotContain(await visible.AllTextContentsAsync(), value => value.Contains("JE-", StringComparison.Ordinal) || value.Contains("MOVE-", StringComparison.Ordinal));
            return;
        }
        await Assertions.Expect(visible).ToHaveCountAsync(expected.Length);
        for (var index = 0; index < expected.Length; index++)
        {
            var cells = visible.Nth(index).Locator("td");
            await Assertions.Expect(cells.Nth(1)).ToHaveTextAsync(Text(expected[index], inventory ? "number" : "entryNumber"));
            await Assertions.Expect(cells.Nth(2)).ToHaveTextAsync(inventory ? Text(expected[index], "type") : $"{Text(expected[index], "accountCode")} - {Text(expected[index], "accountName")}");
            if (inventory)
            {
                await Assertions.Expect(cells.Nth(3)).ToHaveTextAsync(Text(expected[index], "productSku"));
                await Assertions.Expect(cells.Nth(4)).ToHaveTextAsync($"{expected[index].GetProperty("quantity").GetDecimal().ToString(CultureInfo.InvariantCulture)} {Text(expected[index], "unitCode")}");
            }
            else
            {
                await Assertions.Expect(cells.Nth(3)).ToHaveTextAsync(expected[index].GetProperty("baseDebit").GetDecimal().ToString("N2", CultureInfo.InvariantCulture));
                await Assertions.Expect(cells.Nth(4)).ToHaveTextAsync(expected[index].GetProperty("baseCredit").GetDecimal().ToString("N2", CultureInfo.InvariantCulture));
            }
        }
    }

    private static async Task VerifyAgingAsync(IPage page, string baseUrl, AccountingFixture accounting, AgingFixture fixture, bool supplier)
    {
        var noun = supplier ? "payables" : "receivables";
        var report = supplier ? "Payables aging" : "Receivables aging";
        await VisitAsync(page, baseUrl, $"/reports/{noun}", supplier ? "Accounts payable aging" : "Accounts receivable aging");
        // All four reports run after settlement, so they cannot accidentally pass using today's AmountPaid.
        foreach (var offset in new[] { 0, 1, 3, 2 })
        {
            var date = fixture.Start.AddDays(offset);
            var expected = offset switch { 1 => supplier ? 40m : 60m, 3 => 0m, _ => supplier ? 60m : 100m };
            await page.GetByLabel("As of", new() { Exact = true }).FillAsync(Date(date));
            await page.GetByRole(AriaRole.Button, new() { Name = "Run report", Exact = true }).ClickAsync();
            await WaitForReportAsync(page);
            var rows = (await GetAsync(page, baseUrl, $"/api/v1/reports/aging/{noun}?asOf={Date(date)}")).GetProperty("data").EnumerateArray().ToArray();
            var own = rows.Where(row => row.GetProperty("partyId").GetGuid() == fixture.PartyId && Text(row, "currencyCode") == accounting.Currency).ToArray();
            var row = page.GetByRole(AriaRole.Region, new() { Name = $"{report} table", Exact = true })
                .Locator("tbody tr").Filter(new() { Has = page.GetByRole(AriaRole.Cell, new() { Name = fixture.PartyName, Exact = true }) });
            if (expected == 0)
            {
                Assert.Empty(own);
                await Assertions.Expect(row).ToHaveCountAsync(0);
                continue;
            }
            var data = Assert.Single(own);
            Assert.Equal(expected, data.GetProperty("total").GetDecimal());
            Assert.Equal(offset == 0 ? expected : 0m, data.GetProperty("current").GetDecimal());
            Assert.Equal(offset == 0 ? 0m : expected, data.GetProperty("days1To30").GetDecimal());
            foreach (var field in new[] { "days31To60", "days61To90", "over90" }) Assert.Equal(0m, data.GetProperty(field).GetDecimal());
            await Assertions.Expect(row).ToHaveCountAsync(1);
            await Assertions.Expect(row.Locator("td").Last).ToHaveTextAsync(expected.ToString("N2", CultureInfo.InvariantCulture));
            var href = await page.GetByRole(AriaRole.Link, new() { Name = "Export CSV", Exact = true }).GetAttributeAsync("href");
            Assert.NotNull(href);
            Assert.Contains($"asOf={Date(date)}", href, StringComparison.Ordinal);
            var csv = await FetchCsvAsync(page, baseUrl, href);
            var header = csv[0];
            var ownCsv = Assert.Single(csv.Skip(1), item => item[Array.IndexOf(header, "PartyId")] == fixture.PartyId.ToString());
            Assert.Equal(fixture.PartyName, ownCsv[Array.IndexOf(header, "PartyName")]);
            Assert.Equal(expected, decimal.Parse(ownCsv[Array.IndexOf(header, "Total")], CultureInfo.InvariantCulture));
        }
        await VerifyViewportAsync(page, report);
    }

    private static async Task AssertCsvAsync(IPage page, string baseUrl, string href, List<JsonElement> expected,
        string idColumn, string idField, bool inventory)
    {
        var csv = await FetchCsvAsync(page, baseUrl, href);
        var header = csv[0];
        var body = csv.Skip(1).ToArray();
        Assert.Equal(expected.Count, body.Length);
        Assert.All(body, row => Assert.Equal(header.Length, row.Length));
        for (var index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].GetProperty(idField).GetGuid().ToString(), body[index][Array.IndexOf(header, idColumn)]);
            Assert.Equal(Text(expected[index], inventory ? "number" : "entryNumber"), body[index][Array.IndexOf(header, inventory ? "Number" : "EntryNumber")]);
            Assert.Equal(Text(expected[index], "businessDate"), body[index][Array.IndexOf(header, "BusinessDate")]);
            if (inventory)
                Assert.Equal(expected[index].GetProperty("quantity").GetDecimal(), decimal.Parse(body[index][Array.IndexOf(header, "Quantity")], CultureInfo.InvariantCulture));
            else
            {
                Assert.Equal(expected[index].GetProperty("accountId").GetGuid().ToString(), body[index][Array.IndexOf(header, "AccountId")]);
                Assert.Equal(expected[index].GetProperty("baseDebit").GetDecimal(), decimal.Parse(body[index][Array.IndexOf(header, "BaseDebit")], CultureInfo.InvariantCulture));
                Assert.Equal(expected[index].GetProperty("baseCredit").GetDecimal(), decimal.Parse(body[index][Array.IndexOf(header, "BaseCredit")], CultureInfo.InvariantCulture));
                Assert.Equal(Text(expected[index], "description"), body[index][Array.IndexOf(header, "Description")]);
            }
        }
    }

    private static async Task<List<string[]>> FetchCsvAsync(IPage page, string baseUrl, string href)
    {
        var target = new Uri(new Uri(baseUrl), href);
        Assert.Equal(new Uri(baseUrl).GetLeftPart(UriPartial.Authority), target.GetLeftPart(UriPartial.Authority));
        var response = await page.Context.APIRequest.GetAsync(target.ToString());
        Assert.Equal(200, response.Status);
        Assert.StartsWith("text/csv", response.Headers["content-type"], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("attachment", response.Headers["content-disposition"], StringComparison.OrdinalIgnoreCase);
        var rows = ParseCsv(Encoding.UTF8.GetString(await response.BodyAsync()));
        Assert.NotEmpty(rows);
        return rows;
    }

    private static List<string[]> ParseCsv(string text)
    {
        List<string[]> rows = [];
        List<string> cells = [];
        var value = new StringBuilder();
        var quoted = false;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            if (character == '"')
            {
                if (quoted && index + 1 < text.Length && text[index + 1] == '"') { value.Append('"'); index++; }
                else quoted = !quoted;
            }
            else if (!quoted && character == ',') { cells.Add(value.ToString()); value.Clear(); }
            else if (!quoted && character is '\r' or '\n')
            {
                if (character == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                cells.Add(value.ToString()); value.Clear(); rows.Add(cells.ToArray()); cells.Clear();
            }
            else value.Append(character);
        }
        Assert.False(quoted, "The CSV ended inside a quoted field.");
        if (value.Length != 0 || cells.Count != 0) { cells.Add(value.ToString()); rows.Add(cells.ToArray()); }
        return rows;
    }

    private static async Task VerifyViewportAsync(IPage page, string report)
    {
        foreach (var width in new[] { 1440, 390 })
        {
            await page.SetViewportSizeAsync(width, 900);
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), $"The {report} page overflows at width {width}.");
            foreach (var region in await page.Locator("main .table-scroll[role='region']").AllAsync())
            {
                await region.FocusAsync();
                await Assertions.Expect(region).ToBeFocusedAsync();
                Assert.False(string.IsNullOrWhiteSpace(await region.GetAttributeAsync("aria-label")));
                Assert.Equal(1, await region.Locator("caption").CountAsync());
                Assert.Equal(0, await region.Locator("thead th:not([scope='col'])").CountAsync());
            }
            Assert.Empty((await page.RunAxe()).Violations ?? []);
        }
        await page.SetViewportSizeAsync(1440, 1000);
    }

    private static async Task VisitAsync(IPage page, string baseUrl, string route, string heading)
    {
        var link = page.Locator($"#primary-sidebar a[href='{route}']");
        var group = link.Locator("xpath=ancestor::details[1]");
        if (await group.GetAttributeAsync("open") is null) await group.Locator("summary").ClickAsync();
        await link.ClickAsync();
        await Assertions.Expect(page).ToHaveURLAsync(Url(baseUrl, route));
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1, Exact = true })).ToBeVisibleAsync();
        await WaitForReportAsync(page);
    }

    private static async Task WaitForReportAsync(IPage page)
    {
        var errors = page.Locator(".error-panel, main [role='alert']");
        // A corrected filter can still show the previous validation alert until its event is rendered.
        await Assertions.Expect(errors).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator("main [role='status']").Filter(new() { HasText = "Loading" })).ToHaveCountAsync(0);
        await Assertions.Expect(errors).ToHaveCountAsync(0);
    }

    private static async Task<JsonElement> GetAsync(IPage page, string baseUrl, string path)
    {
        var response = await page.Context.APIRequest.GetAsync(Url(baseUrl, path));
        Assert.True(response.Ok, $"Reporting read returned HTTP {response.Status}.");
        using var json = JsonDocument.Parse(await response.TextAsync());
        return json.RootElement.Clone();
    }

    private static async Task<JsonElement> PostAsync(IPage page, string baseUrl, string operation, string path, object request)
    {
        var response = await page.Context.APIRequest.PostAsync(Url(baseUrl, path), new() { DataObject = request });
        Assert.True(response.Ok, $"Reporting fixture operation '{operation}' returned HTTP {response.Status}.");
        using var json = JsonDocument.Parse(await response.TextAsync());
        return json.RootElement.Clone();
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    private static string Url(string baseUrl, string path) => new Uri(new Uri(baseUrl), path).ToString();
    private static string Text(JsonElement item, string field) => item.GetProperty(field).GetString()!;
    private static Guid Id(JsonElement item) => item.GetProperty("id").GetGuid();
    private static string Stamp(JsonElement item) => Text(item, "concurrencyStamp");
    private sealed record AgingFixture(Guid PartyId, string PartyName, DateOnly Start);
}
