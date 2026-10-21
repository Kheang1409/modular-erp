using System.Globalization;
using System.Text.Json;
using Deque.AxeCore.Playwright;
using Microsoft.Playwright;

namespace Erp.EndToEndTests;

internal static class AccountingWorkflow
{
    public static async Task<ReportingWorkflow.AccountingFixture> VerifyAsync(IPage page, string baseUrl, string productSku)
    {
        var products = await GetAsync(page, baseUrl, $"/api/v1/products/?search={productSku}");
        var product = products.GetProperty("items").EnumerateArray().Single(item => Text(item, "sku") == productSku);
        var fixture = await ConfigureAsync(page, baseUrl, Text(product, "currencyCode"));
        var party = await PostAsync(page, baseUrl, "/api/v1/contacts/", new
        {
            kind = 1,
            displayName = $"Browser accounting party {fixture.Suffix}",
            organizationName = $"Browser accounting party {fixture.Suffix}",
            isCustomer = true,
            isSupplier = true,
            taxNumber = $"TAX-{fixture.Suffix}",
            billingAddress = new { line1 = "60 Accounting Street", city = "Test City", countryCode = "US" }
        });
        var sources = await CreateSourcesAsync(page, baseUrl, product, party, fixture, false);
        var cancellationSources = await CreateSourcesAsync(page, baseUrl, product, party, fixture, true);
        await VerifyJournalAsync(page, baseUrl, fixture);

        var invoice = await CreateDocumentUiAsync(page, baseUrl, fixture, sources.Sales, false);
        var bill = await CreateDocumentUiAsync(page, baseUrl, fixture, sources.Purchase, true);
        await CancelDocumentUiAsync(page, baseUrl, fixture, cancellationSources.Sales, false);
        await CancelDocumentUiAsync(page, baseUrl, fixture, cancellationSources.Purchase, true);
        invoice = await PostDocumentUiAsync(page, baseUrl, fixture, invoice, false);
        bill = await PostDocumentUiAsync(page, baseUrl, fixture, bill, true);
        await AssertJournalAsync(page, baseUrl, fixture, invoice.GetProperty("journalEntryId").GetGuid(),
            (fixture.Accounts["AR"], 110m, 0m), (fixture.Accounts["REVENUE"], 0m, 100m), (fixture.Accounts["TAX-OUT"], 0m, 10m));
        await AssertJournalAsync(page, baseUrl, fixture, bill.GetProperty("journalEntryId").GetGuid(),
            (fixture.Accounts["EXPENSE"], 60m, 0m), (fixture.Accounts["TAX-IN"], 6m, 0m), (fixture.Accounts["AP"], 0m, 66m));

        var paymentsBefore = await GetAsync(page, baseUrl, "/api/v1/accounting/payments");
        var overAllocation = await page.Context.APIRequest.PostAsync(Url(baseUrl, "/api/v1/accounting/payments"), new()
        {
            DataObject = new
            {
                direction = 0,
                partyId = Id(party),
                partyName = Text(party, "displayName"),
                paymentDate = fixture.Date,
                currencyCode = fixture.Currency,
                exchangeRate = 1,
                amount = 111,
                reference = $"Rejected allocation {fixture.Suffix}",
                allocations = new[] { new { documentId = Id(invoice), amount = 111 } },
                idempotencyKey = $"over-allocation-{fixture.Suffix}"
            }
        });
        Assert.Equal(400, overAllocation.Status);
        Assert.Equal(paymentsBefore.GetRawText(), (await GetAsync(page, baseUrl, "/api/v1/accounting/payments")).GetRawText());
        Assert.Equal(invoice.GetRawText(), (await GetAsync(page, baseUrl, $"/api/v1/accounting/invoices/{Id(invoice)}")).GetRawText());

        var partial = await CreateAndPostPaymentUiAsync(page, baseUrl, fixture, party, invoice, false, 40, "partial");
        await AssertDocumentBalanceAsync(page, baseUrl, invoice, false, 40, 70, 2);
        await VisitAsync(page, baseUrl, "/accounting/payments", "Payments");
        await Row(page, "Payment register", Number(partial)).GetByRole(AriaRole.Button, new() { Name = "Reverse", Exact = true }).ClickAsync();
        await SuccessAsync(page, $"{Number(partial)} reversed.");
        var reversed = await GetAsync(page, baseUrl, $"/api/v1/accounting/payments/{Id(partial)}");
        Assert.Equal(2, reversed.GetProperty("state").GetInt32());
        var originalPaymentEntry = await GetAsync(page, baseUrl, $"/api/v1/accounting/entries/{partial.GetProperty("journalEntryId").GetGuid()}");
        await AssertJournalAsync(page, baseUrl, fixture, reversed.GetProperty("reversalJournalEntryId").GetGuid(),
            (fixture.Accounts["BANK"], 0m, 40m), (fixture.Accounts["AR"], 40m, 0m));
        Assert.Equal(reversed.GetProperty("reversalJournalEntryId").GetGuid(), originalPaymentEntry.GetProperty("reversedByEntryId").GetGuid());
        await AssertDocumentBalanceAsync(page, baseUrl, invoice, false, 0, 110, 1);

        var receipt = await CreateAndPostPaymentUiAsync(page, baseUrl, fixture, party, invoice, false, 110, "settlement");
        var supplierPayment = await CreateAndPostPaymentUiAsync(page, baseUrl, fixture, party, bill, true, 66, "supplier");
        await AssertDocumentBalanceAsync(page, baseUrl, invoice, false, 110, 0, 3);
        await AssertDocumentBalanceAsync(page, baseUrl, bill, true, 66, 0, 3);
        await AssertJournalAsync(page, baseUrl, fixture, receipt.GetProperty("journalEntryId").GetGuid(),
            (fixture.Accounts["BANK"], 110m, 0m), (fixture.Accounts["AR"], 0m, 110m));
        await AssertJournalAsync(page, baseUrl, fixture, supplierPayment.GetProperty("journalEntryId").GetGuid(),
            (fixture.Accounts["AP"], 66m, 0m), (fixture.Accounts["BANK"], 0m, 66m));
        AssertCommercialSnapshot(invoice, await GetAsync(page, baseUrl, $"/api/v1/accounting/invoices/{Id(invoice)}"));
        AssertCommercialSnapshot(bill, await GetAsync(page, baseUrl, $"/api/v1/accounting/bills/{Id(bill)}"));
        Assert.Equal(sources.Sales.GetRawText(), (await GetAsync(page, baseUrl, $"/api/v1/sales/orders/{Id(sources.Sales)}")).GetRawText());
        Assert.Equal(sources.Purchase.GetRawText(), (await GetAsync(page, baseUrl, $"/api/v1/purchasing/orders/{Id(sources.Purchase)}")).GetRawText());
        await VerifyReconciliationAsync(page, baseUrl, fixture, receipt, supplierPayment);
        await VerifyControlsAsync(page, baseUrl, fixture);
        await VerifyPeriodAsync(page, baseUrl, fixture);

        foreach (var route in new[]
        {
            (Path: "/accounting/setup", Heading: "Accounting setup"), (Path: "/accounting/entries", Heading: "General ledger"),
            (Path: "/accounting/invoices", Heading: "Customer invoices"), (Path: "/accounting/bills", Heading: "Supplier bills"),
            (Path: "/accounting/payments", Heading: "Payments"), (Path: "/accounting/reconciliation", Heading: "Bank reconciliation"),
            (Path: "/accounting/controls", Heading: "Tax and currency controls")
        })
        {
            await VisitAsync(page, baseUrl, route.Path, route.Heading, direct: true);
            await Assertions.Expect(page.Locator("main table tbody tr").First).ToBeVisibleAsync();
            foreach (var region in await page.Locator("main .table-scroll[role='region']").AllAsync())
            {
                await region.FocusAsync();
                await Assertions.Expect(region).ToBeFocusedAsync();
                Assert.False(string.IsNullOrWhiteSpace(await region.GetAttributeAsync("aria-label")));
            }
            await VerifyViewportAsync(page, route.Heading);
        }
        return new(DateOnly.Parse(fixture.Date, CultureInfo.InvariantCulture), fixture.Currency, fixture.JournalId, fixture.Accounts);
    }

    private static async Task<Fixture> ConfigureAsync(IPage page, string baseUrl, string currency)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var date = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var accounts = new Dictionary<string, Guid>();
        foreach (var account in new[]
        {
            (Key: "BANK", Code: "1000", Type: 0, Normal: 0), (Key: "AR", Code: "1100", Type: 0, Normal: 0),
            (Key: "TAX-IN", Code: "1200", Type: 0, Normal: 0), (Key: "AP", Code: "2000", Type: 1, Normal: 1),
            (Key: "TAX-OUT", Code: "2100", Type: 1, Normal: 1), (Key: "REVENUE", Code: "4000", Type: 3, Normal: 1),
            (Key: "EXPENSE", Code: "5000", Type: 4, Normal: 0)
        })
        {
            var created = await PostAsync(page, baseUrl, "/api/v1/accounting/accounts", new { code = $"{account.Code}-{suffix}", name = $"Browser {account.Key} {suffix}", type = account.Type, normalBalance = account.Normal });
            accounts.Add(account.Key, Id(created));
        }
        await VisitAsync(page, baseUrl, "/accounting/setup", "Accounting setup");
        var journalForm = Form(page, "Add journal");
        var journalCode = $"E2E-{suffix}";
        await journalForm.GetByLabel("Code", new() { Exact = true }).FillAsync(journalCode);
        await journalForm.GetByLabel("Name", new() { Exact = true }).FillAsync($"Browser journal {suffix}");
        await journalForm.GetByRole(AriaRole.Button, new() { Name = "Add journal", Exact = true }).ClickAsync();
        await SuccessAsync(page, "Journal added.");
        var journal = (await GetAsync(page, baseUrl, "/api/v1/accounting/journals")).EnumerateArray().Single(item => Text(item, "code") == journalCode);
        var periods = await GetAsync(page, baseUrl, "/api/v1/accounting/periods");
        var matching = periods.EnumerateArray().Where(item => string.CompareOrdinal(Text(item, "startsOn"), date) <= 0 && string.CompareOrdinal(Text(item, "endsOn"), date) >= 0).ToArray();
        JsonElement period;
        if (matching.Length == 0)
        {
            var periodForm = Form(page, "Add period");
            var periodName = $"Browser fiscal year {suffix}";
            await periodForm.GetByLabel("Name", new() { Exact = true }).FillAsync(periodName);
            await periodForm.GetByLabel("Starts", new() { Exact = true }).FillAsync($"{DateTime.UtcNow.Year}-01-01");
            await periodForm.GetByLabel("Ends", new() { Exact = true }).FillAsync($"{DateTime.UtcNow.Year}-12-31");
            await periodForm.GetByRole(AriaRole.Button, new() { Name = "Add period", Exact = true }).ClickAsync();
            await SuccessAsync(page, "Fiscal period added.");
            period = (await GetAsync(page, baseUrl, "/api/v1/accounting/periods")).EnumerateArray().Single(item => Text(item, "name") == periodName);
        }
        else { period = Assert.Single(matching); Assert.Equal(0, period.GetProperty("state").GetInt32()); }
        return new(suffix, date, currency, accounts, Id(journal), period);
    }

    private static async Task<(JsonElement Sales, JsonElement Purchase)> CreateSourcesAsync(IPage page, string baseUrl, JsonElement product, JsonElement party, Fixture fixture, bool cancellation)
    {
        var productId = Id(product); var partyId = Id(party);
        var quotation = await PostAsync(page, baseUrl, "/api/v1/sales/quotations/", new
        {
            customerId = partyId,
            currencyCode = fixture.Currency,
            quotationDate = fixture.Date,
            lines = new[] { new { productId, quantity = cancellation ? 1 : 2, unitPrice = cancellation ? 1 : 50, taxPercent = cancellation ? 0 : 10 } }
        });
        var quoteApi = $"/api/v1/sales/quotations/{Id(quotation)}";
        foreach (var action in new[] { "sent", "accepted" }) quotation = await PostAsync(page, baseUrl, $"{quoteApi}/{action}", new { concurrencyStamp = Stamp(quotation) });
        var sales = await PostAsync(page, baseUrl, $"{quoteApi}/convert", new { concurrencyStamp = Stamp(quotation), orderDate = fixture.Date });
        await PostAsync(page, baseUrl, $"/api/v1/sales/orders/{Id(sales)}/confirm", new { concurrencyStamp = Stamp(sales) });
        var purchase = await PostAsync(page, baseUrl, "/api/v1/purchasing/orders/", new
        {
            supplierId = partyId,
            currencyCode = fixture.Currency,
            orderDate = fixture.Date,
            lines = new[] { new { productId, quantity = cancellation ? 1 : 3, unitPrice = cancellation ? 1 : 20, taxPercent = cancellation ? 0 : 10 } }
        });
        await PostAsync(page, baseUrl, $"/api/v1/purchasing/orders/{Id(purchase)}/confirm", new { concurrencyStamp = Stamp(purchase) });
        return (await GetAsync(page, baseUrl, $"/api/v1/sales/orders/{Id(sales)}"), await GetAsync(page, baseUrl, $"/api/v1/purchasing/orders/{Id(purchase)}"));
    }

    private static async Task VerifyJournalAsync(IPage page, string baseUrl, Fixture fixture)
    {
        await VisitAsync(page, baseUrl, "/accounting/entries", "General ledger");
        var form = Form(page, "Save draft");
        var description = $"Browser manual entry {fixture.Suffix}";
        await SelectAsync(form, "Journal", fixture.JournalId);
        await form.GetByLabel("Business date", new() { Exact = true }).FillAsync(fixture.Date);
        await form.GetByLabel("Currency", new() { Exact = true }).FillAsync(fixture.Currency);
        await form.GetByLabel("Exchange rate", new() { Exact = true }).FillAsync("1");
        await form.GetByLabel("Description", new() { Exact = true }).FillAsync(description);
        await form.GetByLabel("Line 1 account", new() { Exact = true }).SelectOptionAsync(fixture.Accounts["BANK"].ToString());
        await form.GetByLabel("Line 1 description", new() { Exact = true }).FillAsync("Manual bank debit");
        await form.GetByLabel("Line 1 debit", new() { Exact = true }).FillAsync("25");
        await form.GetByLabel("Line 1 credit", new() { Exact = true }).FillAsync("0");
        await form.GetByLabel("Line 2 account", new() { Exact = true }).SelectOptionAsync(fixture.Accounts["REVENUE"].ToString());
        await form.GetByLabel("Line 2 description", new() { Exact = true }).FillAsync("Manual revenue credit");
        await form.GetByLabel("Line 2 debit", new() { Exact = true }).FillAsync("0");
        await form.GetByLabel("Line 2 credit", new() { Exact = true }).FillAsync("25");
        await VerifyViewportAsync(page, "New journal entry");
        await form.GetByRole(AriaRole.Button, new() { Name = "Save draft", Exact = true }).ClickAsync();
        await SuccessAsync(page, "Draft created.");
        var entry = (await GetAsync(page, baseUrl, "/api/v1/accounting/entries")).EnumerateArray().Single(item => Text(item, "description") == description);
        Assert.Equal(0, entry.GetProperty("state").GetInt32());
        await Row(page, "Journal entries", Number(entry)).GetByRole(AriaRole.Button, new() { Name = "Post", Exact = true }).ClickAsync();
        await SuccessAsync(page, $"{Number(entry)} posted.");
        var posted = await AssertJournalAsync(page, baseUrl, fixture, Id(entry), (fixture.Accounts["BANK"], 25m, 0m), (fixture.Accounts["REVENUE"], 0m, 25m));
        await Row(page, "Journal entries", Number(entry)).GetByRole(AriaRole.Button, new() { Name = "Reverse", Exact = true }).ClickAsync();
        await SuccessAsync(page, $"{Number(entry)} reversed.");
        var original = await GetAsync(page, baseUrl, $"/api/v1/accounting/entries/{Id(entry)}");
        var reversalId = original.GetProperty("reversedByEntryId").GetGuid();
        var reversal = await AssertJournalAsync(page, baseUrl, fixture, reversalId, (fixture.Accounts["BANK"], 0m, 25m), (fixture.Accounts["REVENUE"], 25m, 0m));
        Assert.Equal(Id(entry), reversal.GetProperty("reversesEntryId").GetGuid());
        foreach (var field in posted.EnumerateObject().Where(field => field.Name != "reversedByEntryId")) Assert.Equal(field.Value.GetRawText(), original.GetProperty(field.Name).GetRawText());
        var before = await GetAsync(page, baseUrl, "/api/v1/accounting/entries");
        var invalid = await page.Context.APIRequest.PostAsync(Url(baseUrl, "/api/v1/accounting/entries"), new() { DataObject = EntryRequest(fixture, "Unbalanced browser rejection", 1, 2) });
        Assert.Equal(400, invalid.Status);
        Assert.Equal(before.GetRawText(), (await GetAsync(page, baseUrl, "/api/v1/accounting/entries")).GetRawText());
    }

    private static async Task<JsonElement> CreateDocumentUiAsync(IPage page, string baseUrl, Fixture fixture, JsonElement source, bool bill)
    {
        var route = bill ? "bills" : "invoices";
        await VisitAsync(page, baseUrl, $"/accounting/{route}", bill ? "Supplier bills" : "Customer invoices");
        await page.GetByLabel(bill ? "Purchase order ID" : "Sales order ID", new() { Exact = true }).FillAsync(Id(source).ToString());
        if (bill) await page.GetByLabel("Supplier invoice reference", new() { Exact = true }).FillAsync($"SUP-{Id(source):N}");
        await page.GetByLabel(bill ? "Bill date" : "Invoice date", new() { Exact = true }).FillAsync(fixture.Date);
        await page.GetByLabel("Due date", new() { Exact = true }).FillAsync(fixture.Date);
        await page.GetByRole(AriaRole.Button, new() { Name = "Create draft", Exact = true }).ClickAsync();
        await SuccessAsync(page, bill ? "Bill draft created." : "Invoice draft created.");
        var document = (await GetAsync(page, baseUrl, $"/api/v1/accounting/{route}")).EnumerateArray().Single(item => item.GetProperty(bill ? "purchaseOrderId" : "salesOrderId").GetGuid() == Id(source));
        Assert.Equal(0, document.GetProperty("state").GetInt32());
        foreach (var field in new[] { "currencyCode", "baseCurrencyCode" }) Assert.Equal(Text(source, field), Text(document, field));
        foreach (var field in new[] { "exchangeRate", "subtotal", "discountTotal", "taxTotal", "total" }) Assert.Equal(source.GetProperty(field).GetDecimal(), document.GetProperty(field).GetDecimal());
        Assert.Equal(source.GetProperty(bill ? "supplierId" : "customerId").GetGuid(), document.GetProperty(bill ? "supplierId" : "customerId").GetGuid());
        return document;
    }

    private static async Task CancelDocumentUiAsync(IPage page, string baseUrl, Fixture fixture, JsonElement source, bool bill)
    {
        var document = await CreateDocumentUiAsync(page, baseUrl, fixture, source, bill);
        await Row(page, bill ? "Payables" : "Receivables", Number(document)).GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await SuccessAsync(page, $"{Number(document)} cancelled.");
        var persisted = await GetAsync(page, baseUrl, $"/api/v1/accounting/{(bill ? "bills" : "invoices")}/{Id(document)}");
        Assert.Equal(4, persisted.GetProperty("state").GetInt32());
        Assert.Equal(JsonValueKind.Null, persisted.GetProperty("journalEntryId").ValueKind);
        AssertCommercialSnapshot(document, persisted);
    }

    private static async Task<JsonElement> PostDocumentUiAsync(IPage page, string baseUrl, Fixture fixture, JsonElement document, bool bill)
    {
        var noun = bill ? "bill" : "invoice";
        await VisitAsync(page, baseUrl, $"/accounting/{(bill ? "bills" : "invoices")}", bill ? "Supplier bills" : "Customer invoices");
        await Row(page, bill ? "Payables" : "Receivables", Number(document)).GetByRole(AriaRole.Button, new() { Name = "Post", Exact = true }).ClickAsync();
        var heading = page.GetByRole(AriaRole.Heading, new() { Name = $"Post {noun} {Number(document)}", Exact = true });
        await Assertions.Expect(heading).ToBeFocusedAsync();
        var form = Form(page, $"Post {noun}");
        foreach (var selector in await form.GetByRole(AriaRole.Combobox).AllAsync()) Assert.Contains(await selector.InputValueAsync(), new[] { Guid.Empty.ToString(), "" });
        await form.GetByRole(AriaRole.Button, new() { Name = $"Post {noun}", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator("main [role='alert']")).ToContainTextAsync("Select an active journal");
        await Assertions.Expect(form).ToBeVisibleAsync();
        Assert.Equal(document.GetRawText(), (await GetAsync(page, baseUrl, $"/api/v1/accounting/{(bill ? "bills" : "invoices")}/{Id(document)}")).GetRawText());
        await SelectAsync(form, "Posting journal", fixture.JournalId);
        await SelectAsync(form, bill ? "Payable account" : "Receivable account", fixture.Accounts[bill ? "AP" : "AR"]);
        await SelectAsync(form, bill ? "Expense account" : "Revenue account", fixture.Accounts[bill ? "EXPENSE" : "REVENUE"]);
        await SelectAsync(form, bill ? "Input tax account" : "Output tax account", fixture.Accounts[bill ? "TAX-IN" : "TAX-OUT"]);
        await VerifyViewportAsync(page, $"Post {noun} choices");
        await form.GetByRole(AriaRole.Button, new() { Name = $"Post {noun}", Exact = true }).ClickAsync();
        await SuccessAsync(page, $"{Number(document)} posted.");
        var posted = await GetAsync(page, baseUrl, $"/api/v1/accounting/{(bill ? "bills" : "invoices")}/{Id(document)}");
        Assert.Equal(1, posted.GetProperty("state").GetInt32());
        AssertCommercialSnapshot(document, posted);
        return posted;
    }

    private static async Task<JsonElement> CreateAndPostPaymentUiAsync(IPage page, string baseUrl, Fixture fixture, JsonElement party, JsonElement document, bool supplier, decimal amount, string suffix)
    {
        await VisitAsync(page, baseUrl, "/accounting/payments", "Payments");
        var form = Form(page, "Save draft");
        await form.GetByRole(AriaRole.Combobox, new() { Name = "Direction", Exact = true }).SelectOptionAsync(supplier ? "SupplierPayment" : "CustomerReceipt");
        await form.GetByLabel("Customer or supplier ID", new() { Exact = true }).FillAsync(Id(party).ToString());
        await form.GetByLabel("Name", new() { Exact = true }).FillAsync(Text(party, "displayName"));
        await form.GetByLabel("Date", new() { Exact = true }).FillAsync(fixture.Date);
        await form.GetByLabel("Currency", new() { Exact = true }).FillAsync(fixture.Currency);
        await form.GetByLabel("Exchange rate", new() { Exact = true }).FillAsync("1");
        await form.GetByLabel("Amount", new() { Exact = true }).FillAsync(amount.ToString(CultureInfo.InvariantCulture));
        var reference = $"Browser-{fixture.Suffix}-{suffix}";
        await form.GetByLabel("Reference", new() { Exact = true }).FillAsync(reference);
        await form.GetByLabel("Invoice or bill ID (optional)", new() { Exact = true }).FillAsync(Id(document).ToString());
        await form.GetByLabel("Allocated amount", new() { Exact = true }).FillAsync(amount.ToString(CultureInfo.InvariantCulture));
        await form.GetByRole(AriaRole.Button, new() { Name = "Save draft", Exact = true }).ClickAsync();
        await SuccessAsync(page, "Payment draft created.");
        var payment = (await GetAsync(page, baseUrl, "/api/v1/accounting/payments")).EnumerateArray().Single(item => Text(item, "reference") == reference);
        Assert.Equal(0, payment.GetProperty("state").GetInt32());
        Assert.Equal(amount, payment.GetProperty("allocatedAmount").GetDecimal());
        Assert.Equal(0, payment.GetProperty("unallocatedAmount").GetDecimal());
        Assert.Equal(Id(document), Assert.Single(payment.GetProperty("allocations").EnumerateArray()).GetProperty("documentId").GetGuid());
        await Row(page, "Payment register", Number(payment)).GetByRole(AriaRole.Button, new() { Name = "Post", Exact = true }).ClickAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = $"Post payment {Number(payment)}", Exact = true })).ToBeFocusedAsync();
        var posting = Form(page, "Post payment");
        foreach (var selector in await posting.GetByRole(AriaRole.Combobox).AllAsync()) await Assertions.Expect(selector).ToHaveValueAsync(Guid.Empty.ToString());
        await SelectAsync(posting, "Posting journal", fixture.JournalId);
        await SelectAsync(posting, "Bank account", fixture.Accounts["BANK"]);
        var control = posting.GetByRole(AriaRole.Combobox, new() { Name = "Control account", Exact = true });
        await Assertions.Expect(control.Locator($"option[value='{fixture.Accounts["BANK"]}']")).ToHaveCountAsync(0);
        await Assertions.Expect(control.Locator($"option[value='{fixture.Accounts[supplier ? "AR" : "AP"]}']")).ToHaveCountAsync(0);
        await SelectAsync(posting, "Control account", fixture.Accounts[supplier ? "AP" : "AR"]);
        if (!supplier)
        {
            await SelectAsync(posting, "Bank account", fixture.Accounts["AR"]);
            await Assertions.Expect(posting.GetByRole(AriaRole.Combobox, new() { Name = "Control account", Exact = true })).ToHaveValueAsync(Guid.Empty.ToString());
            await SelectAsync(posting, "Bank account", fixture.Accounts["BANK"]);
            await SelectAsync(posting, "Control account", fixture.Accounts["AR"]);
        }
        await VerifyViewportAsync(page, "Explicit payment posting accounts");
        await posting.GetByRole(AriaRole.Button, new() { Name = "Post payment", Exact = true }).ClickAsync();
        await SuccessAsync(page, $"{Number(payment)} posted.");
        var posted = await GetAsync(page, baseUrl, $"/api/v1/accounting/payments/{Id(payment)}");
        Assert.Equal(1, posted.GetProperty("state").GetInt32());
        foreach (var field in new[] { "number", "direction", "partyId", "partyName", "paymentDate", "currencyCode", "exchangeRate", "amount", "reference", "allocations" }) Assert.Equal(payment.GetProperty(field).GetRawText(), posted.GetProperty(field).GetRawText());
        return posted;
    }

    private static async Task VerifyReconciliationAsync(IPage page, string baseUrl, Fixture fixture, JsonElement receipt, JsonElement payment)
    {
        await VisitAsync(page, baseUrl, "/accounting/reconciliation", "Bank reconciliation");
        await SelectAsync(page.Locator("main"), "Bank account", fixture.Accounts["BANK"]);
        await page.GetByLabel("Statement date", new() { Exact = true }).FillAsync(fixture.Date);
        await page.GetByLabel("Opening balance", new() { Exact = true }).FillAsync("0");
        await page.GetByLabel("Closing balance", new() { Exact = true }).FillAsync("44");
        var receiptEntry = receipt.GetProperty("journalEntryId").GetGuid();
        var paymentEntry = payment.GetProperty("journalEntryId").GetGuid();
        await page.GetByLabel("Journal entry IDs", new() { Exact = false }).FillAsync($"{receiptEntry},{paymentEntry}");
        await page.GetByRole(AriaRole.Button, new() { Name = "Create reconciliation", Exact = true }).ClickAsync();
        await SuccessAsync(page, "Reconciliation created.");
        var reconciliation = (await GetAsync(page, baseUrl, "/api/v1/accounting/reconciliations")).EnumerateArray().Single(item => item.GetProperty("bankAccountId").GetGuid() == fixture.Accounts["BANK"]);
        Assert.Equal(0m, reconciliation.GetProperty("difference").GetDecimal());
        Assert.Equal(44m, reconciliation.GetProperty("clearedChange").GetDecimal());
        var lines = reconciliation.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Equal(110m, lines.Single(line => line.GetProperty("journalEntryId").GetGuid() == receiptEntry).GetProperty("amount").GetDecimal());
        Assert.Equal(-66m, lines.Single(line => line.GetProperty("journalEntryId").GetGuid() == paymentEntry).GetProperty("amount").GetDecimal());
        await Row(page, "Reconciliation history", Number(reconciliation)).GetByRole(AriaRole.Button, new() { Name = "Finalize", Exact = true }).ClickAsync();
        await SuccessAsync(page, $"{Number(reconciliation)} finalized.");
        var finalized = (await GetAsync(page, baseUrl, "/api/v1/accounting/reconciliations")).EnumerateArray().Single(item => Id(item) == Id(reconciliation));
        Assert.Equal(1, finalized.GetProperty("state").GetInt32());
        Assert.NotEqual(Stamp(reconciliation), Stamp(finalized));
        Assert.Equal(reconciliation.GetProperty("lines").GetRawText(), finalized.GetProperty("lines").GetRawText());
    }

    private static async Task VerifyControlsAsync(IPage page, string baseUrl, Fixture fixture)
    {
        await VisitAsync(page, baseUrl, "/accounting/controls", "Tax and currency controls");
        var taxForm = Form(page, "Add tax code");
        var code = $"E2E-{fixture.Suffix}";
        await taxForm.GetByLabel("Code", new() { Exact = true }).FillAsync(code);
        await taxForm.GetByLabel("Name", new() { Exact = true }).FillAsync($"Browser ten percent {fixture.Suffix}");
        await taxForm.GetByLabel("Rate %", new() { Exact = true }).FillAsync("10");
        await SelectAsync(taxForm, "Output tax account", fixture.Accounts["TAX-OUT"]);
        await SelectAsync(taxForm, "Input tax account", fixture.Accounts["TAX-IN"]);
        await taxForm.GetByRole(AriaRole.Button, new() { Name = "Add tax code", Exact = true }).ClickAsync();
        await SuccessAsync(page, "Tax code added.");
        var tax = (await GetAsync(page, baseUrl, "/api/v1/accounting/tax-codes")).EnumerateArray().Single(item => Text(item, "code") == code);
        Assert.Equal(10m, tax.GetProperty("rate").GetDecimal());
        Assert.Equal(fixture.Accounts["TAX-OUT"], tax.GetProperty("outputAccountId").GetGuid());
        Assert.Equal(fixture.Accounts["TAX-IN"], tax.GetProperty("inputAccountId").GetGuid());
        var quote = fixture.Currency == "EUR" ? "USD" : "EUR";
        var rates = await GetAsync(page, baseUrl, "/api/v1/accounting/currency-rates");
        var effective = DateOnly.FromDateTime(DateTime.UtcNow);
        while (rates.EnumerateArray().Any(item => Text(item, "baseCurrencyCode") == fixture.Currency && Text(item, "quoteCurrencyCode") == quote && Text(item, "effectiveDate") == effective.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))) effective = effective.AddDays(-1);
        var rateForm = Form(page, "Add exchange rate");
        await rateForm.GetByLabel("Base currency", new() { Exact = true }).FillAsync(fixture.Currency);
        await rateForm.GetByLabel("Quote currency", new() { Exact = true }).FillAsync(quote);
        await rateForm.GetByLabel("Effective date", new() { Exact = true }).FillAsync(effective.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        await rateForm.GetByLabel("Rate", new() { Exact = true }).FillAsync("1.125");
        await rateForm.GetByRole(AriaRole.Button, new() { Name = "Add exchange rate", Exact = true }).ClickAsync();
        await SuccessAsync(page, "Exchange rate added.");
        var resolved = await GetAsync(page, baseUrl, $"/api/v1/accounting/currency-rates/resolve?baseCurrency={fixture.Currency}&quoteCurrency={quote}&date={effective:yyyy-MM-dd}");
        Assert.Equal(1.125m, resolved.GetProperty("rate").GetDecimal());
    }

    private static async Task VerifyPeriodAsync(IPage page, string baseUrl, Fixture fixture)
    {
        await VisitAsync(page, baseUrl, "/accounting/setup", "Accounting setup");
        var name = Text(fixture.Period, "name");
        await Row(page, "Fiscal periods", name).GetByRole(AriaRole.Button, new() { Name = "Close", Exact = true }).ClickAsync();
        await SuccessAsync(page, "Period closed.");
        var closed = (await GetAsync(page, baseUrl, "/api/v1/accounting/periods")).EnumerateArray().Single(item => Id(item) == Id(fixture.Period));
        Assert.Equal(1, closed.GetProperty("state").GetInt32());
        // Draft creation remains allowed; only posting is rejected by the closed period.
        var draft = await PostAsync(page, baseUrl, "/api/v1/accounting/entries", EntryRequest(fixture, $"Closed-period retry {fixture.Suffix}", 1, 1));
        var postRequest = new { concurrencyStamp = Stamp(draft), idempotencyKey = $"closed-retry-{fixture.Suffix}" };
        var before = await GetAsync(page, baseUrl, $"/api/v1/accounting/entries/{Id(draft)}");
        var failed = await page.Context.APIRequest.PostAsync(Url(baseUrl, $"/api/v1/accounting/entries/{Id(draft)}/post"), new() { DataObject = postRequest });
        Assert.Equal(400, failed.Status);
        Assert.Equal(before.GetRawText(), (await GetAsync(page, baseUrl, $"/api/v1/accounting/entries/{Id(draft)}")).GetRawText());
        await Row(page, "Fiscal periods", name).GetByRole(AriaRole.Button, new() { Name = "Reopen", Exact = true }).ClickAsync();
        await SuccessAsync(page, "Period reopened.");
        var reopened = (await GetAsync(page, baseUrl, "/api/v1/accounting/periods")).EnumerateArray().Single(item => Id(item) == Id(fixture.Period));
        Assert.Equal(0, reopened.GetProperty("state").GetInt32());
        await PostAsync(page, baseUrl, $"/api/v1/accounting/entries/{Id(draft)}/post", postRequest);
        await AssertJournalAsync(page, baseUrl, fixture, Id(draft), (fixture.Accounts["EXPENSE"], 1m, 0m), (fixture.Accounts["REVENUE"], 0m, 1m));
    }

    private static object EntryRequest(Fixture fixture, string description, decimal debit, decimal credit) => new
    {
        journalId = fixture.JournalId,
        businessDate = fixture.Date,
        description,
        currencyCode = fixture.Currency,
        exchangeRate = 1,
        idempotencyKey = Guid.NewGuid().ToString("N"),
        lines = new[] { new { accountId = fixture.Accounts["EXPENSE"], description = "Browser debit", debit, credit = 0m }, new { accountId = fixture.Accounts["REVENUE"], description = "Browser credit", debit = 0m, credit } }
    };

    private static async Task<JsonElement> AssertJournalAsync(IPage page, string baseUrl, Fixture fixture, Guid entryId, params (Guid Account, decimal Debit, decimal Credit)[] expected)
    {
        var entry = await GetAsync(page, baseUrl, $"/api/v1/accounting/entries/{entryId}");
        Assert.Equal(1, entry.GetProperty("state").GetInt32());
        Assert.Equal(fixture.JournalId, entry.GetProperty("journalId").GetGuid());
        Assert.Equal(fixture.Currency, Text(entry, "currencyCode"));
        Assert.Equal(1m, entry.GetProperty("exchangeRate").GetDecimal());
        var lines = entry.GetProperty("lines").EnumerateArray().ToArray();
        Assert.Equal(expected.Length, lines.Length);
        foreach (var item in expected)
        {
            var line = Assert.Single(lines, line => line.GetProperty("accountId").GetGuid() == item.Account);
            Assert.Equal(item.Debit, line.GetProperty("debit").GetDecimal()); Assert.Equal(item.Credit, line.GetProperty("credit").GetDecimal());
            Assert.Equal(item.Debit, line.GetProperty("baseDebit").GetDecimal()); Assert.Equal(item.Credit, line.GetProperty("baseCredit").GetDecimal());
        }
        Assert.Equal(lines.Sum(line => line.GetProperty("debit").GetDecimal()), lines.Sum(line => line.GetProperty("credit").GetDecimal()));
        return entry;
    }

    private static async Task AssertDocumentBalanceAsync(IPage page, string baseUrl, JsonElement original, bool bill, decimal paid, decimal due, int state)
    {
        var document = await GetAsync(page, baseUrl, $"/api/v1/accounting/{(bill ? "bills" : "invoices")}/{Id(original)}");
        Assert.Equal(paid, document.GetProperty("amountPaid").GetDecimal()); Assert.Equal(due, document.GetProperty("amountDue").GetDecimal());
        Assert.Equal(state, document.GetProperty("state").GetInt32()); AssertCommercialSnapshot(original, document);
    }

    private static void AssertCommercialSnapshot(JsonElement expected, JsonElement actual)
    {
        foreach (var field in expected.EnumerateObject().Where(field => field.Name is not ("state" or "amountPaid" or "amountDue" or "journalEntryId" or "updatedAtUtc" or "concurrencyStamp"))) Assert.Equal(field.Value.GetRawText(), actual.GetProperty(field.Name).GetRawText());
    }

    private static ILocator Form(IPage page, string submit) => page.Locator("main form").Filter(new() { Has = page.GetByRole(AriaRole.Button, new() { Name = submit, Exact = true }) });
    private static ILocator Row(IPage page, string region, string number) => page.GetByRole(AriaRole.Region, new() { Name = region, Exact = true }).Locator("tbody tr").Filter(new() { Has = page.Locator("td:first-child").Filter(new() { HasText = number }) });
    private static Task<IReadOnlyList<string>> SelectAsync(ILocator scope, string label, Guid value) => scope.GetByRole(AriaRole.Combobox, new() { Name = label, Exact = true }).SelectOptionAsync(value.ToString());
    private static async Task SuccessAsync(IPage page, string message)
    {
        await Assertions.Expect(page.Locator("main .notice-success[role='status']")).ToHaveTextAsync(message);
        Assert.Equal(0, await page.Locator(".error-panel, main [role='alert']").CountAsync());
    }
    private static async Task VerifyViewportAsync(IPage page, string context)
    {
        foreach (var width in new[] { 1440, 390 })
        {
            await page.SetViewportSizeAsync(width, 900);
            Assert.True(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth <= window.innerWidth + 1"), $"Accounting page {context} overflows at width {width}.");
            Assert.Empty((await page.RunAxe()).Violations ?? []);
        }
        await page.SetViewportSizeAsync(1440, 1000);
    }
    private static async Task VisitAsync(IPage page, string baseUrl, string path, string heading, bool direct = false)
    {
        if (direct || new Uri(page.Url).AbsolutePath == path)
        {
            var response = await page.GotoAsync(Url(baseUrl, path)); Assert.NotNull(response);
            Assert.True(response.Ok, $"Accounting navigation returned HTTP {response.Status}.");
        }
        else
        {
            var link = page.Locator($"#primary-sidebar a[href='{path}']");
            var group = link.Locator("xpath=ancestor::details[1]");
            if (await group.GetAttributeAsync("open") is null) await group.Locator("summary").ClickAsync();
            await link.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(Url(baseUrl, path));
        }
        await Assertions.Expect(page.Locator(".shell")).ToHaveAttributeAsync("data-interactive", "true");
        await Assertions.Expect(page.GetByRole(AriaRole.Heading, new() { Name = heading, Level = 1, Exact = true })).ToBeVisibleAsync();
        Assert.Equal(0, await page.Locator(".error-panel, main [role='alert']").CountAsync());
    }
    private static async Task<JsonElement> GetAsync(IPage page, string baseUrl, string path)
    {
        var response = await page.Context.APIRequest.GetAsync(Url(baseUrl, path)); Assert.True(response.Ok, $"Accounting read returned HTTP {response.Status}.");
        using var json = JsonDocument.Parse(await response.TextAsync()); return json.RootElement.Clone();
    }
    private static async Task<JsonElement> PostAsync(IPage page, string baseUrl, string path, object request)
    {
        var response = await page.Context.APIRequest.PostAsync(Url(baseUrl, path), new() { DataObject = request }); Assert.True(response.Ok, $"Accounting setup or action returned HTTP {response.Status}.");
        using var json = JsonDocument.Parse(await response.TextAsync()); return json.RootElement.Clone();
    }
    private static string Url(string baseUrl, string path) => new Uri(new Uri(baseUrl), path).ToString();
    private static string Text(JsonElement item, string field) => item.GetProperty(field).GetString()!;
    private static Guid Id(JsonElement item) => item.GetProperty("id").GetGuid();
    private static string Number(JsonElement item) => Text(item, "number");
    private static string Stamp(JsonElement item) => Text(item, "concurrencyStamp");
    private sealed record Fixture(string Suffix, string Date, string Currency, IReadOnlyDictionary<string, Guid> Accounts, Guid JournalId, JsonElement Period);
}
