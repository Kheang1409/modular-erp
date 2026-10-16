using Erp.Abstractions.Context;
using Erp.Modules.Accounting;
using Erp.Modules.Purchasing;
using Erp.Modules.Sales;
using Erp.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace Erp.IntegrationTests;

public sealed class ReportingPostgresTests
{
    [Fact]
    public async Task OperationalAndFinancialReportsReconcileToCompanyScopedTransactions()
    {
        var postgres = await IntegrationContainers.StartPostgresAsync("reporting_tests");

        await using (postgres)
        {
            var tenantId = Guid.NewGuid();
            var companyId = Guid.NewGuid();
            var tenant = new Tenant(tenantId);
            var company = new Company(companyId);
            var salesOptions = new DbContextOptionsBuilder<SalesDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
            var purchasingOptions = new DbContextOptionsBuilder<PurchasingDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
            var accountingOptions = new DbContextOptionsBuilder<AccountingDbContext>().UseNpgsql(postgres.GetConnectionString()).Options;
            await using (var migration = new SalesDbContext(salesOptions)) await migration.Database.MigrateAsync();
            await using (var migration = new PurchasingDbContext(purchasingOptions)) await migration.Database.MigrateAsync();
            await using (var migration = new AccountingDbContext(accountingOptions)) await migration.Database.MigrateAsync();

            var customerId = Guid.NewGuid();
            var supplierId = Guid.NewGuid();
            var productId = Guid.NewGuid();
            await using var sales = new SalesDbContext(salesOptions, tenant, company);
            sales.SalesOrders.Add(Order(tenantId, companyId, customerId, productId, SalesOrderState.Confirmed, "SO-REPORT-1", 100, 10, 110));
            sales.SalesOrders.Add(Order(tenantId, companyId, customerId, productId, SalesOrderState.Draft, "SO-DRAFT", 900, 90, 990));
            await sales.SaveChangesAsync();
            var otherCompanyId = Guid.NewGuid();
            await using (var otherSales = new SalesDbContext(salesOptions, tenant, new Company(otherCompanyId)))
            {
                otherSales.SalesOrders.Add(Order(tenantId, otherCompanyId, customerId, productId, SalesOrderState.Confirmed, "SO-OTHER-COMPANY", 700, 70, 770));
                await otherSales.SaveChangesAsync();
            }

            await using var purchasing = new PurchasingDbContext(purchasingOptions, tenant, company);
            purchasing.PurchaseOrders.Add(Purchase(tenantId, companyId, supplierId, productId, PurchaseOrderState.Confirmed, "PO-REPORT-1", 50, 5, 55));
            purchasing.PurchaseOrders.Add(Purchase(tenantId, companyId, supplierId, productId, PurchaseOrderState.Cancelled, "PO-CANCELLED", 500, 50, 550));
            await purchasing.SaveChangesAsync();

            await using var accounting = new AccountingDbContext(accountingOptions, tenant, company);
            var journalId = Guid.NewGuid();
            var cashId = Guid.NewGuid();
            var receivableId = Guid.NewGuid();
            var payableId = Guid.NewGuid();
            var revenueId = Guid.NewGuid();
            var expenseId = Guid.NewGuid();
            var equityId = Guid.NewGuid();
            accounting.Journals.Add(new Journal { Id = journalId, TenantId = tenantId, CompanyId = companyId, Code = "GJ", Name = "General", IsActive = true, CreatedAtUtc = Now, UpdatedAtUtc = Now, ConcurrencyStamp = Stamp() });
            AddAccount(accounting, tenantId, companyId, cashId, "1000", "Cash", AccountType.Asset, NormalBalance.Debit);
            AddAccount(accounting, tenantId, companyId, receivableId, "1100", "Receivable", AccountType.Asset, NormalBalance.Debit);
            AddAccount(accounting, tenantId, companyId, payableId, "2000", "Payable", AccountType.Liability, NormalBalance.Credit);
            AddAccount(accounting, tenantId, companyId, equityId, "3000", "Equity", AccountType.Equity, NormalBalance.Credit);
            AddAccount(accounting, tenantId, companyId, revenueId, "4000", "Revenue", AccountType.Revenue, NormalBalance.Credit);
            AddAccount(accounting, tenantId, companyId, expenseId, "5000", "Expense", AccountType.Expense, NormalBalance.Debit);
            var entry = new JournalEntry { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, Number = "JE-REPORT-1", JournalId = journalId, BusinessDate = new(2026, 8, 15), Description = "Reporting fixture", CurrencyCode = "USD", ExchangeRate = 1, State = JournalEntryState.Posted, CreatedAtUtc = Now, PostedAtUtc = Now, ConcurrencyStamp = Stamp() };
            AddLine(entry, 1, cashId, "1000", "Cash", 200, 0);
            AddLine(entry, 2, receivableId, "1100", "Receivable", 110, 0);
            AddLine(entry, 3, expenseId, "5000", "Expense", 50, 0);
            AddLine(entry, 4, payableId, "2000", "Payable", 0, 55);
            AddLine(entry, 5, equityId, "3000", "Equity", 0, 255);
            AddLine(entry, 6, revenueId, "4000", "Revenue", 0, 50);
            accounting.JournalEntries.Add(entry);
            accounting.CustomerInvoices.Add(new CustomerInvoice { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, Number = "INV-REPORT-1", SalesOrderId = Guid.NewGuid(), SalesOrderNumber = "SO-REPORT-1", CustomerId = customerId, CustomerName = "Report Customer", InvoiceDate = new(2026, 7, 1), DueDate = new(2026, 8, 22), CurrencyCode = "USD", BaseCurrencyCode = "USD", ExchangeRate = 1, Subtotal = 100, TaxTotal = 10, Total = 110, AmountPaid = 60, State = CustomerInvoiceState.PartiallyPaid, CreatedAtUtc = Now, UpdatedAtUtc = Now, ConcurrencyStamp = Stamp() });
            accounting.SupplierBills.Add(new SupplierBill { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, Number = "BILL-REPORT-1", SupplierReference = "SUP-REPORT", PurchaseOrderId = Guid.NewGuid(), PurchaseOrderNumber = "PO-REPORT-1", SupplierId = supplierId, SupplierName = "Report Supplier", BillDate = new(2026, 6, 1), DueDate = new(2026, 7, 23), CurrencyCode = "USD", BaseCurrencyCode = "USD", ExchangeRate = 1, Subtotal = 50, TaxTotal = 5, Total = 55, AmountPaid = 5, State = SupplierBillState.PartiallyPaid, CreatedAtUtc = Now, UpdatedAtUtc = Now, ConcurrencyStamp = Stamp() });
            accounting.Payments.Add(new Payment { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, Number = "PAY-IN", Direction = PaymentDirection.CustomerReceipt, PartyId = customerId, PartyName = "Report Customer", PaymentDate = new(2026, 8, 10), CurrencyCode = "USD", ExchangeRate = 1, Amount = 60, Reference = "IN", State = PaymentState.Posted, CreatedAtUtc = Now, UpdatedAtUtc = Now, ConcurrencyStamp = Stamp() });
            accounting.Payments.Add(new Payment { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, Number = "PAY-OUT", Direction = PaymentDirection.SupplierPayment, PartyId = supplierId, PartyName = "Report Supplier", PaymentDate = new(2026, 8, 11), CurrencyCode = "USD", ExchangeRate = 1, Amount = 5, Reference = "OUT", State = PaymentState.Posted, CreatedAtUtc = Now, UpdatedAtUtc = Now, ConcurrencyStamp = Stamp() });
            accounting.Payments.Add(new Payment { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, Number = "PAY-REVERSED", Direction = PaymentDirection.CustomerReceipt, PartyId = customerId, PartyName = "Report Customer", PaymentDate = new(2026, 8, 12), CurrencyCode = "USD", ExchangeRate = 1, Amount = 999, Reference = "REVERSED", State = PaymentState.Reversed, CreatedAtUtc = Now, UpdatedAtUtc = Now, ConcurrencyStamp = Stamp() });
            await accounting.SaveChangesAsync();

            var salesReports = new SalesReportingData(sales);
            var salesSummary = Assert.Single(await salesReports.SummaryAsync(new(2026, 8, 1), new(2026, 8, 31), default));
            Assert.Equal((1, 100m, 10m, 110m), (salesSummary.OrderCount, salesSummary.Subtotal, salesSummary.Tax, salesSummary.Total));
            var byCustomer = Assert.Single(await salesReports.ByCustomerAsync(new(2026, 8, 1), new(2026, 8, 31), default));
            Assert.Equal(customerId, byCustomer.CustomerId);
            Assert.Equal(110m, byCustomer.Total);
            var byProduct = Assert.Single(await salesReports.ByProductAsync(new(2026, 8, 1), new(2026, 8, 31), default));
            Assert.Equal(productId, byProduct.ProductId);
            Assert.Equal(2m, byProduct.Quantity);
            Assert.Equal(110m, byProduct.Total);

            var purchaseSummary = Assert.Single(await new PurchasingReportingData(purchasing).SummaryAsync(new(2026, 8, 1), new(2026, 8, 31), default));
            Assert.Equal((1, 50m, 5m, 55m), (purchaseSummary.OrderCount, purchaseSummary.Subtotal, purchaseSummary.Tax, purchaseSummary.Total));

            var financial = new AccountingReportingData(accounting);
            var trialBalance = await financial.TrialBalanceAsync(new(2026, 9, 1), default);
            Assert.Equal(trialBalance.Sum(x => x.Debit), trialBalance.Sum(x => x.Credit));
            Assert.Equal(360m, trialBalance.Sum(x => x.Debit));
            Assert.Equal(6, (await financial.GeneralLedgerAsync(new(2026, 8, 1), new(2026, 8, 31), 1, 200, default)).Total);
            var profitAndLoss = await financial.ProfitAndLossAsync(new(2026, 8, 1), new(2026, 8, 31), default);
            Assert.Equal(50m, Assert.Single(profitAndLoss, x => x.Type == nameof(AccountType.Revenue)).Balance);
            Assert.Equal(50m, Assert.Single(profitAndLoss, x => x.Type == nameof(AccountType.Expense)).Balance);
            var balanceSheet = await financial.BalanceSheetAsync(new(2026, 9, 1), default);
            Assert.Equal(310m, balanceSheet.Where(x => x.Type == nameof(AccountType.Asset)).Sum(x => x.Balance));
            Assert.Equal(310m, balanceSheet.Where(x => x.Type is nameof(AccountType.Liability) or nameof(AccountType.Equity)).Sum(x => x.Balance));
            var receivables = Assert.Single(await financial.ReceivablesAgingAsync(new(2026, 9, 1), default));
            Assert.Equal(50m, receivables.Days1To30);
            Assert.Equal(50m, receivables.Total);
            var payables = Assert.Single(await financial.PayablesAgingAsync(new(2026, 9, 1), default));
            Assert.Equal(50m, payables.Days31To60);
            Assert.Equal(50m, payables.Total);
            var cash = await financial.CashSummaryAsync(new(2026, 8, 1), new(2026, 8, 31), default);
            Assert.Equal(60m, Assert.Single(cash, x => x.Direction == nameof(PaymentDirection.CustomerReceipt)).Amount);
            Assert.Equal(5m, Assert.Single(cash, x => x.Direction == nameof(PaymentDirection.SupplierPayment)).Amount);
        }
    }

    private static SalesOrder Order(Guid tenantId, Guid companyId, Guid customerId, Guid productId, SalesOrderState state, string number, decimal subtotal, decimal tax, decimal total)
    {
        var id = Guid.NewGuid();
        var order = new SalesOrder { Id = id, TenantId = tenantId, CompanyId = companyId, SourceQuotationId = Guid.NewGuid(), Number = number, CustomerId = customerId, CustomerName = "Report Customer", CurrencyCode = "USD", BaseCurrencyCode = "USD", ExchangeRate = 1, OrderDate = new(2026, 8, 5), State = state, Subtotal = subtotal, TaxTotal = tax, Total = total, CreatedAtUtc = Now, UpdatedAtUtc = Now, ConcurrencyStamp = Stamp() };
        order.Lines.Add(new SalesOrderLine { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, SalesOrderId = id, Sequence = 1, ProductId = productId, ProductSku = "SKU-REPORT", Description = "Report product", UnitCode = "EA", Quantity = 2, UnitPrice = subtotal / 2, Subtotal = subtotal, TaxAmount = tax, Total = total });
        return order;
    }

    private static PurchaseOrder Purchase(Guid tenantId, Guid companyId, Guid supplierId, Guid productId, PurchaseOrderState state, string number, decimal subtotal, decimal tax, decimal total)
    {
        var id = Guid.NewGuid();
        var order = new PurchaseOrder { Id = id, TenantId = tenantId, CompanyId = companyId, Number = number, SupplierId = supplierId, SupplierName = "Report Supplier", CurrencyCode = "USD", BaseCurrencyCode = "USD", ExchangeRate = 1, OrderDate = new(2026, 8, 6), State = state, Subtotal = subtotal, TaxTotal = tax, Total = total, CreatedAtUtc = Now, UpdatedAtUtc = Now, ConcurrencyStamp = Stamp() };
        order.Lines.Add(new PurchaseOrderLine { Id = Guid.NewGuid(), TenantId = tenantId, CompanyId = companyId, PurchaseOrderId = id, Sequence = 1, ProductId = productId, ProductSku = "SKU-REPORT", Description = "Report product", UnitCode = "EA", Quantity = 1, UnitPrice = subtotal, Subtotal = subtotal, TaxAmount = tax, Total = total });
        return order;
    }

    private static void AddAccount(AccountingDbContext db, Guid tenantId, Guid companyId, Guid id, string code, string name, AccountType type, NormalBalance balance) => db.Accounts.Add(new Account { Id = id, TenantId = tenantId, CompanyId = companyId, Code = code, Name = name, Type = type, NormalBalance = balance, AllowDirectPosting = true, IsActive = true, CreatedAtUtc = Now, UpdatedAtUtc = Now, ConcurrencyStamp = Stamp() });
    private static void AddLine(JournalEntry entry, int sequence, Guid accountId, string code, string name, decimal debit, decimal credit) => entry.Lines.Add(new JournalEntryLine { Id = Guid.NewGuid(), TenantId = entry.TenantId, CompanyId = entry.CompanyId, JournalEntryId = entry.Id, Sequence = sequence, AccountId = accountId, AccountCode = code, AccountName = name, Description = name, Debit = debit, Credit = credit, BaseDebit = debit, BaseCredit = credit });
    private static DateTimeOffset Now => new(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
    private static string Stamp() => Guid.NewGuid().ToString("N");
    private sealed class Tenant(Guid id) : ITenantContext { public TenantId? TenantId { get; } = new(id); }
    private sealed class Company(Guid id) : ICompanyContext { public CompanyId? CompanyId { get; } = new(id); }
}
