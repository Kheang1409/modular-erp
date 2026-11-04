using System.Net;
using System.Security.Cryptography;
using System.Text;
using Erp.Abstractions.Context;
using Erp.Modules.Accounting;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Erp.UnitTests;

public sealed class PayWayServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid InvoiceId = Guid.NewGuid();
    private static readonly string[] SignedFields =
    [
        "req_time", "merchant_id", "tran_id", "amount", "items", "shipping",
        "firstname", "lastname", "email", "phone", "type", "payment_option",
        "return_url", "cancel_url", "continue_success_url", "return_deeplink",
        "currency", "custom_fields", "return_params", "payout", "lifetime",
        "google_pay_token", "skip_success_page", "payment_gate", "additional_params"
    ];

    [Fact]
    public async Task CreateCheckoutSignsServerCalculatedInvoiceAmountAndUsesHostedSandbox()
    {
        var service = CreateService(new InvoiceDirectory(Invoice()), new PaymentLedger(), new StubHandler((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK))));

        var checkout = await service.CreateCheckoutAsync(InvoiceId, default);
        var fields = checkout.Fields;
        var signedValue = string.Concat(SignedFields.Select(field => fields[field]));
        var expectedHash = Convert.ToBase64String(HMACSHA512.HashData(Encoding.UTF8.GetBytes("sandbox-secret"), Encoding.UTF8.GetBytes(signedValue)));
        var successUri = new Uri(fields["continue_success_url"]);
        var itemJson = Encoding.UTF8.GetString(Convert.FromBase64String(fields["items"]));

        Assert.Equal("https://checkout-sandbox.payway.com.kh/api/payment-gateway/v1/payments/purchase", checkout.CheckoutUrl);
        Assert.Equal(expectedHash, fields["hash"]);
        Assert.Equal("125.50", fields["amount"]);
        Assert.Equal("USD", fields["currency"]);
        Assert.StartsWith("/api/v1/accounting/payments/payway/callback", new Uri(Encoding.UTF8.GetString(Convert.FromBase64String(fields["return_url"]))).AbsolutePath);
        Assert.Contains(Uri.EscapeDataString(QueryHelpers.ParseQuery(successUri.Query)["checkout"].ToString()), fields["continue_success_url"], StringComparison.Ordinal);
        Assert.Contains("\"price\":125.50", itemJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmApprovedTransactionCreatesDraftReceiptOnlyAfterProviderAmountMatch()
    {
        var ledger = new PaymentLedger();
        var transactionId = "";
        var handler = new StubHandler((request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://checkout-sandbox.payway.com.kh/api/payment-gateway/v1/payments/check-transaction-2", request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(StatusResponse(transactionId, "APPROVED", 125.50m), Encoding.UTF8, "application/json")
            });
        });
        var service = CreateService(new InvoiceDirectory(Invoice()), ledger, handler);
        var checkout = await service.CreateCheckoutAsync(InvoiceId, default);
        transactionId = checkout.TransactionId;
        var ticket = QueryHelpers.ParseQuery(new Uri(checkout.Fields["continue_success_url"]).Query)["checkout"].ToString();

        var result = await service.ConfirmAsync(ticket, default);

        Assert.Equal("APPROVED", result.Status);
        Assert.NotNull(result.Payment);
        Assert.Equal(PaymentDirection.CustomerReceipt, ledger.LastRequest!.Direction);
        Assert.Equal(125.50m, ledger.LastRequest.Amount);
        Assert.Equal(InvoiceId, ledger.LastRequest.Allocations![0].DocumentId);
        Assert.Equal($"payway-{checkout.TransactionId}", ledger.LastRequest.IdempotencyKey);
    }

    [Fact]
    public async Task ConfirmPendingTransactionDoesNotCreatePayment()
    {
        var ledger = new PaymentLedger();
        var transactionId = "";
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(StatusResponse(transactionId, "PENDING", 125.50m), Encoding.UTF8, "application/json")
        }));
        var service = CreateService(new InvoiceDirectory(Invoice()), ledger, handler);
        var checkout = await service.CreateCheckoutAsync(InvoiceId, default);
        transactionId = checkout.TransactionId;
        var ticket = QueryHelpers.ParseQuery(new Uri(checkout.Fields["continue_success_url"]).Query)["checkout"].ToString();

        var result = await service.ConfirmAsync(ticket, default);

        Assert.Equal("PENDING", result.Status);
        Assert.Null(result.Payment);
        Assert.Null(ledger.LastRequest);
    }

    [Fact]
    public async Task ConfirmApprovedTransactionRejectsAmountMismatch()
    {
        var transactionId = "";
        var handler = new StubHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(StatusResponse(transactionId, "APPROVED", 124.50m), Encoding.UTF8, "application/json")
        }));
        var ledger = new PaymentLedger();
        var service = CreateService(new InvoiceDirectory(Invoice()), ledger, handler);
        var checkout = await service.CreateCheckoutAsync(InvoiceId, default);
        transactionId = checkout.TransactionId;
        var ticket = QueryHelpers.ParseQuery(new Uri(checkout.Fields["continue_success_url"]).Query)["checkout"].ToString();

        await Assert.ThrowsAsync<HttpRequestException>(() => service.ConfirmAsync(ticket, default));
        Assert.Null(ledger.LastRequest);
    }

    private static PayWayService CreateService(InvoiceDirectory invoices, PaymentLedger payments, HttpMessageHandler handler)
    {
        var provider = new EphemeralDataProtectionProvider();
        return new PayWayService(
            new HttpClient(handler),
            invoices,
            payments,
            Options.Create(new PayWayOptions
            {
                MerchantId = "ec000002",
                ApiKey = "sandbox-secret",
                PublicBaseUrl = "https://erp.example.com"
            }),
            provider,
            new FixedClock());
    }

    private static CustomerInvoiceDto Invoice() => new(
        InvoiceId, "INV-001", Guid.NewGuid(), "SO-001", Guid.NewGuid(), "Example Customer", null,
        new(2026, 11, 1), new(2026, 11, 30), "USD", "USD", 1m,
        125.50m, 0m, 0m, 125.50m, 0m, 125.50m, CustomerInvoiceState.Posted, null,
        Now, Now, "stamp", []);

    private static string StatusResponse(string transactionId, string status, decimal totalAmount) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            status = new { code = "00", tran_id = transactionId },
            data = new { total_amount = totalAmount, payment_currency = "USD", payment_status = status }
        });

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = Now;
    }

    private sealed class InvoiceDirectory(CustomerInvoiceDto invoice) : ICustomerInvoiceService
    {
        public Task<IReadOnlyList<CustomerInvoiceDto>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CustomerInvoiceDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
            id == invoice.Id ? Task.FromResult(invoice) : throw new KeyNotFoundException();
        public Task<CustomerInvoiceDto> CreateFromSalesOrderAsync(CreateCustomerInvoiceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CustomerInvoiceDto> PostAsync(Guid id, PostCustomerInvoiceRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CustomerInvoiceDto> CancelAsync(Guid id, AccountingActionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class PaymentLedger : IPaymentService
    {
        public CreatePaymentRequest? LastRequest { get; private set; }
        public Task<IReadOnlyList<PaymentDto>> ListAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentDto> GetAsync(Guid id, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentDto> CreateAsync(CreatePaymentRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new PaymentDto(Guid.NewGuid(), "PAY-001", request.Direction, request.PartyId,
                request.PartyName!, request.PaymentDate, request.CurrencyCode!, request.ExchangeRate, request.Amount,
                request.Amount, 0, request.Reference!, PaymentState.Draft, null, null, Now, Now, "stamp", []));
        }
        public Task<PaymentDto> PostAsync(Guid id, PostPaymentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PaymentDto> ReverseAsync(Guid id, ReversePaymentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
