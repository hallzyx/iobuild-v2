using System.Text.Json;

namespace IoBuild.Api.CoreBusiness;

public sealed record PaymentCheckoutRequest(int BuilderId, int PlanId, string SuccessUrl, string CancelUrl);
public sealed record PaymentCheckoutSession(string Id, string Url, long AmountInCents);
public sealed record PaymentSessionConfirmation(string SessionId, string Status, int BuilderId, int PlanId);
public sealed record PaymentInvoice(string Id, string Status, long AmountInCents);

public interface IPaymentProvider
{
    Task<PaymentCheckoutSession?> CreateCheckoutSessionAsync(PaymentCheckoutRequest request, StripeIntegrationOptions options, CancellationToken cancellationToken = default);
    Task<PaymentSessionConfirmation?> ConfirmSessionAsync(string sessionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PaymentInvoice>?> GetInvoicesAsync(int builderId, CancellationToken cancellationToken = default);
}

public sealed class StripeHttpPaymentProvider(HttpClient client, IConfiguration configuration) : IPaymentProvider
{
    public async Task<PaymentCheckoutSession?> CreateCheckoutSessionAsync(PaymentCheckoutRequest request, StripeIntegrationOptions options, CancellationToken cancellationToken = default)
    {
        var price = configuration[$"Stripe:PlanPrices:{request.PlanId}"];
        var endpoint = Endpoint("/v1/checkout/sessions");
        if (endpoint is null || string.IsNullOrWhiteSpace(price) || !StripeRestrictedKeyResolver.IsRestrictedKey(options.RestrictedApiKey)) return null;
        using var body = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["mode"] = "subscription",
            ["success_url"] = request.SuccessUrl,
            ["cancel_url"] = request.CancelUrl,
            ["line_items[0][price]"] = price,
            ["line_items[0][quantity]"] = "1",
            ["metadata[builder_id]"] = request.BuilderId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["metadata[plan_id]"] = request.PlanId.ToString(System.Globalization.CultureInfo.InvariantCulture)
        });
        using var message = AuthorizedRequest(HttpMethod.Post, endpoint, options.RestrictedApiKey);
        message.Content = body;
        return await SendCheckoutAsync(message, cancellationToken);
    }

    public async Task<PaymentSessionConfirmation?> ConfirmSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var endpoint = Endpoint($"/v1/checkout/sessions/{Uri.EscapeDataString(sessionId)}");
        var key = StripeRestrictedKeyResolver.Resolve(configuration);
        if (endpoint is null || key is null) return null;
        using var message = AuthorizedRequest(HttpMethod.Get, endpoint, key);
        try
        {
            using var response = await client.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || !root.TryGetProperty("metadata", out var metadata)
                || !metadata.TryGetProperty("builder_id", out var builder) || !metadata.TryGetProperty("plan_id", out var plan)
                || !int.TryParse(builder.GetString(), out var builderId) || !int.TryParse(plan.GetString(), out var planId)) return null;
            var status = root.TryGetProperty("payment_status", out var paymentStatus) ? paymentStatus.GetString() : root.GetProperty("status").GetString();
            return string.IsNullOrWhiteSpace(status) ? null : new PaymentSessionConfirmation(id.GetString()!, status, builderId, planId);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    public async Task<IReadOnlyList<PaymentInvoice>?> GetInvoicesAsync(int builderId, CancellationToken cancellationToken = default)
    {
        var customer = configuration[$"Stripe:BuilderCustomers:{builderId}"];
        var key = StripeRestrictedKeyResolver.Resolve(configuration);
        var endpoint = string.IsNullOrWhiteSpace(customer) ? null : Endpoint($"/v1/invoices?customer={Uri.EscapeDataString(customer)}&limit=100");
        if (endpoint is null || key is null) return null;
        using var message = AuthorizedRequest(HttpMethod.Get, endpoint, key);
        try
        {
            using var response = await client.SendAsync(message, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return null;
            return data.EnumerateArray().Select(invoice => new PaymentInvoice(
                invoice.GetProperty("id").GetString()!,
                invoice.GetProperty("status").GetString()!,
                invoice.TryGetProperty("amount_paid", out var amount) ? amount.GetInt64() : 0)).ToList();
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }

    private Uri? Endpoint(string path)
    {
        var baseUrl = configuration["Stripe:ProviderBaseUrl"];
        return Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri) ? new Uri(baseUri, path) : null;
    }

    private static HttpRequestMessage AuthorizedRequest(HttpMethod method, Uri endpoint, string key)
    {
        var request = new HttpRequestMessage(method, endpoint);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        request.Headers.Add("Stripe-Version", "2026-05-27.dahlia");
        return request;
    }

    private async Task<PaymentCheckoutSession?> SendCheckoutAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            var root = document.RootElement;
            if (!root.TryGetProperty("id", out var id) || !root.TryGetProperty("url", out var url)) return null;
            var amount = root.TryGetProperty("amount_total", out var total) ? total.GetInt64() : 0;
            return new PaymentCheckoutSession(id.GetString()!, url.GetString()!, amount);
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }
}
