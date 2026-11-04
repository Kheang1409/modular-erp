using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Erp.Abstractions.Context;
using Erp.SharedKernel;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;

namespace Erp.Modules.Accounting;

public sealed class PayWayOptions
{
    public string MerchantId { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string PublicBaseUrl { get; set; } = "";
    public string CheckoutUrl { get; set; } = "https://checkout-sandbox.payway.com.kh/api/payment-gateway/v1/payments/purchase";
    public string CheckTransactionUrl { get; set; } = "https://checkout-sandbox.payway.com.kh/api/payment-gateway/v1/payments/check-transaction-2";
}

public sealed record CreatePayWayCheckoutRequest(Guid InvoiceId);
public sealed record PayWayCheckoutDto(string CheckoutUrl, string TransactionId, IReadOnlyDictionary<string, string> Fields);
public sealed record ConfirmPayWayPaymentRequest(string Ticket);
public sealed record PayWayPaymentStatusDto(string Status, PaymentDto? Payment);

public interface IPayWayService
{
    Task<PayWayCheckoutDto> CreateCheckoutAsync(Guid invoiceId, CancellationToken cancellationToken);
    Task<PayWayPaymentStatusDto> ConfirmAsync(string ticket, CancellationToken cancellationToken);
}

public sealed class PayWayService(
    HttpClient http,
    ICustomerInvoiceService invoices,
    IPaymentService payments,
    IOptions<PayWayOptions> options,
    IDataProtectionProvider protectionProvider,
    IClock clock) : IPayWayService
{
    private static readonly string[] SignedFields =
    [
        "req_time", "merchant_id", "tran_id", "amount", "items", "shipping",
        "firstname", "lastname", "email", "phone", "type", "payment_option",
        "return_url", "cancel_url", "continue_success_url", "return_deeplink",
        "currency", "custom_fields", "return_params", "payout", "lifetime",
        "google_pay_token", "skip_success_page", "payment_gate", "additional_params"
    ];

    private readonly PayWayOptions _options = options.Value;
    private readonly IDataProtector _protector = protectionProvider.CreateProtector("Erp.Accounting.PayWay.Checkout.v1");

    public async Task<PayWayCheckoutDto> CreateCheckoutAsync(Guid invoiceId, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        ValidateProviderUrl(_options.CheckoutUrl);
        ValidatePublicBaseUrl();

        var invoice = await invoices.GetAsync(invoiceId, cancellationToken);
        if (invoice.State is not (CustomerInvoiceState.Posted or CustomerInvoiceState.PartiallyPaid) || invoice.AmountDue <= 0)
            throw new AccountingValidationException(new Dictionary<string, string[]>
            {
                ["invoiceId"] = ["Only posted invoices with an outstanding balance can be paid online."]
            });
        if (invoice.CurrencyCode is not ("USD" or "KHR"))
            throw new AccountingValidationException(new Dictionary<string, string[]>
            {
                ["currencyCode"] = ["ABA PayWay supports USD and KHR invoices."]
            });

        var precision = invoice.CurrencyCode == "KHR" ? 0 : 2;
        if (invoice.AmountDue != decimal.Round(invoice.AmountDue, precision))
            throw new AccountingValidationException(new Dictionary<string, string[]>
            {
                ["amount"] = [$"The outstanding balance must have no more than {precision} decimal places for {invoice.CurrencyCode}."]
            });

        var transactionId = $"{clock.UtcNow:yyyyMMddHHmmss}{RandomNumberGenerator.GetInt32(0, 1_000_000):D6}";
        var ticket = _protector.Protect(JsonSerializer.Serialize(new CheckoutTicket(
            transactionId,
            invoice.Id,
            invoice.AmountDue,
            invoice.CurrencyCode,
            clock.UtcNow)));
        var paymentUrl = new Uri(_options.PublicBaseUrl.TrimEnd('/') + "/accounting/payments?checkout=" + Uri.EscapeDataString(ticket));
        var callbackUrl = new Uri(_options.PublicBaseUrl.TrimEnd('/') + "/api/v1/accounting/payments/payway/callback");
        var returnUrl = Convert.ToBase64String(Encoding.UTF8.GetBytes(callbackUrl.AbsoluteUri));
        var item = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(
            new[] { new CheckoutItem(invoice.Number, 1, invoice.AmountDue) }));
        var amount = invoice.AmountDue.ToString(invoice.CurrencyCode == "KHR" ? "0" : "0.00", CultureInfo.InvariantCulture);

        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["req_time"] = clock.UtcNow.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture),
            ["merchant_id"] = _options.MerchantId,
            ["tran_id"] = transactionId,
            ["amount"] = amount,
            ["items"] = item,
            ["shipping"] = "0",
            ["firstname"] = "",
            ["lastname"] = "",
            ["email"] = "",
            ["phone"] = "",
            ["type"] = "purchase",
            ["payment_option"] = "",
            ["return_url"] = returnUrl,
            ["cancel_url"] = paymentUrl.AbsoluteUri,
            ["continue_success_url"] = paymentUrl.AbsoluteUri,
            ["return_deeplink"] = "",
            ["currency"] = invoice.CurrencyCode,
            ["custom_fields"] = "",
            ["return_params"] = "",
            ["payout"] = "",
            ["lifetime"] = "",
            ["google_pay_token"] = "",
            ["skip_success_page"] = "1",
            ["payment_gate"] = "0",
            ["additional_params"] = ""
        };
        fields["hash"] = Sign(string.Concat(SignedFields.Select(field => fields[field])));
        return new(_options.CheckoutUrl, transactionId, fields);
    }

    public async Task<PayWayPaymentStatusDto> ConfirmAsync(string ticket, CancellationToken cancellationToken)
    {
        EnsureConfigured();
        ValidateProviderUrl(_options.CheckTransactionUrl);
        var checkout = ReadTicket(ticket);
        if (clock.UtcNow - checkout.CreatedAtUtc > TimeSpan.FromDays(7) || checkout.CreatedAtUtc > clock.UtcNow.AddMinutes(2))
            throw new PayWayTicketException();

        var requestTime = clock.UtcNow.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var request = new CheckTransactionRequest(
            requestTime,
            _options.MerchantId,
            checkout.TransactionId,
            Sign(requestTime + _options.MerchantId + checkout.TransactionId));
        using var response = await http.PostAsJsonAsync(_options.CheckTransactionUrl, request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CheckTransactionResponse>(cancellationToken);
        if (result?.Status?.Code != "00" ||
            !StringComparer.Ordinal.Equals(result.Status.TransactionId, checkout.TransactionId) ||
            result.Data is null)
            throw new HttpRequestException("ABA PayWay returned an invalid transaction status.");

        if (!StringComparer.Ordinal.Equals(result.Data.PaymentStatus, "APPROVED"))
            return new(result.Data.PaymentStatus ?? "PENDING", null);

        if (result.Data.TotalAmount != checkout.Amount)
            throw new HttpRequestException("ABA PayWay transaction details do not match the checkout.");

        var invoice = await invoices.GetAsync(checkout.InvoiceId, cancellationToken);
        if (!StringComparer.OrdinalIgnoreCase.Equals(invoice.CurrencyCode, checkout.CurrencyCode))
            throw new AccountingValidationException(new Dictionary<string, string[]>
            {
                ["invoiceId"] = ["The invoice currency changed after checkout. Review the payment before posting it."]
            });

        PaymentDto payment;
        try
        {
            payment = await payments.CreateAsync(
                new CreatePaymentRequest(
                    PaymentDirection.CustomerReceipt,
                    invoice.CustomerId,
                    invoice.CustomerName,
                    DateOnly.FromDateTime(clock.UtcNow.UtcDateTime),
                    checkout.CurrencyCode,
                    invoice.ExchangeRate,
                    checkout.Amount,
                    $"ABA PayWay {checkout.TransactionId}",
                    [new PaymentAllocationRequest(invoice.Id, checkout.Amount)],
                    $"payway-{checkout.TransactionId}"),
                cancellationToken);
        }
        catch (AccountingValidationException) when (invoice.AmountDue < checkout.Amount)
        {
            throw new AccountingValidationException(new Dictionary<string, string[]>
            {
                ["invoiceId"] = ["The invoice balance changed after checkout. Review the payment before posting it."]
            });
        }

        return new("APPROVED", payment);
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.MerchantId) ||
            string.IsNullOrWhiteSpace(_options.ApiKey) ||
            string.IsNullOrWhiteSpace(_options.PublicBaseUrl))
            throw new PayWayNotConfiguredException();
    }

    private void ValidatePublicBaseUrl()
    {
        if (!Uri.TryCreate(_options.PublicBaseUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new PayWayNotConfiguredException();
    }

    private static void ValidateProviderUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !uri.Host.EndsWith(".payway.com.kh", StringComparison.OrdinalIgnoreCase))
            throw new PayWayNotConfiguredException();
    }

    private CheckoutTicket ReadTicket(string ticket)
    {
        try
        {
            var value = JsonSerializer.Deserialize<CheckoutTicket>(_protector.Unprotect(ticket));
            if (value is null ||
                string.IsNullOrWhiteSpace(value.TransactionId) ||
                value.InvoiceId == Guid.Empty ||
                value.Amount <= 0 ||
                value.CurrencyCode is not ("USD" or "KHR"))
                throw new PayWayTicketException();
            return value;
        }
        catch (CryptographicException)
        {
            throw new PayWayTicketException();
        }
        catch (JsonException)
        {
            throw new PayWayTicketException();
        }
    }

    private string Sign(string value) =>
        Convert.ToBase64String(HMACSHA512.HashData(Encoding.UTF8.GetBytes(_options.ApiKey), Encoding.UTF8.GetBytes(value)));

    private sealed record CheckoutTicket(string TransactionId, Guid InvoiceId, decimal Amount, string CurrencyCode, DateTimeOffset CreatedAtUtc);
    private sealed record CheckoutItem(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("quantity")] int Quantity,
        [property: JsonPropertyName("price")] decimal Price);
    private sealed record CheckTransactionRequest(
        [property: JsonPropertyName("req_time")] string RequestTime,
        [property: JsonPropertyName("merchant_id")] string MerchantId,
        [property: JsonPropertyName("tran_id")] string TransactionId,
        [property: JsonPropertyName("hash")] string Hash);
    private sealed record CheckTransactionResponse(
        [property: JsonPropertyName("status")] CheckTransactionStatus? Status,
        [property: JsonPropertyName("data")] CheckTransactionData? Data);
    private sealed record CheckTransactionStatus(
        [property: JsonPropertyName("code")] string? Code,
        [property: JsonPropertyName("tran_id")] string? TransactionId);
    private sealed record CheckTransactionData(
        [property: JsonPropertyName("total_amount")] decimal TotalAmount,
        [property: JsonPropertyName("payment_status")] string? PaymentStatus);
}

public sealed class PayWayNotConfiguredException() : Exception("ABA PayWay is not configured.");
public sealed class PayWayTicketException() : Exception("The payment checkout ticket is invalid or expired.");
