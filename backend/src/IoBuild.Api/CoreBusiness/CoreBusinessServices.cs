using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IoBuild.Api.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace IoBuild.Api.CoreBusiness;

public sealed record StripeWebhookRequest(string EventId, string EventType, string Payload, string Signature);

public static class StripeRestrictedKeyResolver
{
    public static string? Resolve(IConfiguration configuration)
    {
        var key = configuration["Stripe:RestrictedApiKey"];
        return IsRestrictedKey(key) ? key : null;
    }

    public static bool IsRestrictedKey(string? key) => !string.IsNullOrWhiteSpace(key) && key.StartsWith("rk_", StringComparison.Ordinal);
}

public sealed record StripeIntegrationOptions(string RestrictedApiKey, bool UsesDynamicPaymentMethods)
{
    public static StripeIntegrationOptions Create(string restrictedApiKey)
    {
        if (!StripeRestrictedKeyResolver.IsRestrictedKey(restrictedApiKey))
            throw new InvalidOperationException("Stripe integrations require a restricted API key.");

        return new StripeIntegrationOptions(restrictedApiKey, UsesDynamicPaymentMethods: true);
    }
}

public sealed class StripeWebhookProcessor(IoBuildDbContext dbContext, string webhookSecret, TimeProvider? clock = null)
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;
    public async Task<bool> ProcessAsync(StripeWebhookRequest request, CancellationToken cancellationToken = default)
    {
        if (!HasValidSignature(request.Payload, request.Signature)) return false;
        if (await dbContext.SubscriptionWebhooks.AnyAsync(webhook => webhook.EventId == request.EventId, cancellationToken)) return true;

        using var document = JsonDocument.Parse(request.Payload);

        dbContext.SubscriptionWebhooks.Add(new SubscriptionWebhook
        {
            EventId = request.EventId,
            EventType = request.EventType,
            ReceivedAt = DateTimeOffset.UtcNow
        });
        if (string.Equals(request.EventType, "checkout.session.completed", StringComparison.Ordinal)
            && TryGetSubscriptionIdentifiers(document.RootElement, out var builderId, out var planId)
            && IsPaid(document.RootElement))
        {
            dbContext.Subscriptions.Add(new Subscription
            {
                BuilderId = builderId,
                PlanId = planId,
                Status = "active"
            });
        }

        try { await dbContext.SaveChangesAsync(cancellationToken); return true; }
        catch (DbUpdateException)
        {
            dbContext.ChangeTracker.Clear();
            if (await dbContext.SubscriptionWebhooks.AsNoTracking().AnyAsync(webhook => webhook.EventId == request.EventId, cancellationToken)) return true;
            throw;
        }
    }

    private bool HasValidSignature(string payload, string signature)
    {
        var values = signature.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2)).Where(part => part.Length == 2)
            .ToDictionary(part => part[0], part => part[1], StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(webhookSecret) || !values.TryGetValue("t", out var timestamp) || !values.TryGetValue("v1", out var supplied)
            || !long.TryParse(timestamp, out var unixTimestamp) || Math.Abs((clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(unixTimestamp)).TotalMinutes) > 5) return false;

        var signedPayload = Encoding.UTF8.GetBytes($"{timestamp}.{payload}");
        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(webhookSecret), signedPayload);
        try { return CryptographicOperations.FixedTimeEquals(expected, Convert.FromHexString(supplied)); }
        catch (FormatException) { return false; }
    }

    private static bool IsPaid(JsonElement root) => !root.TryGetProperty("data", out var data) || !data.TryGetProperty("object", out var session)
        || !session.TryGetProperty("payment_status", out var status) || string.Equals(status.GetString(), "paid", StringComparison.OrdinalIgnoreCase);

    private static bool TryGetSubscriptionIdentifiers(JsonElement root, out int builderId, out int planId)
    {
        if (root.TryGetProperty("builderId", out var builder) && root.TryGetProperty("planId", out var plan)
            && builder.TryGetInt32(out builderId) && plan.TryGetInt32(out planId)) return true;

        if (root.TryGetProperty("data", out var data) && data.TryGetProperty("object", out var session)
            && session.TryGetProperty("metadata", out var metadata)
            && metadata.TryGetProperty("builder_id", out var legacyBuilder)
            && metadata.TryGetProperty("plan_id", out var legacyPlan)
            && int.TryParse(legacyBuilder.GetString(), out builderId)
            && int.TryParse(legacyPlan.GetString(), out planId)) return true;

        builderId = default;
        planId = default;
        return false;
    }
}

public interface ICloudinaryUploader
{
    Task<string?> UploadAsync(string content, CancellationToken cancellationToken = default);
}

public sealed class CloudinaryHttpUploader(HttpClient client, IConfiguration configuration, TimeProvider? clock = null) : ICloudinaryUploader
{
    private readonly TimeProvider clock = clock ?? TimeProvider.System;

    public async Task<string?> UploadAsync(string content, CancellationToken cancellationToken = default)
    {
        var cloudName = configuration["Cloudinary:CloudName"];
        var apiKey = configuration["Cloudinary:ApiKey"];
        var apiSecret = configuration["Cloudinary:ApiSecret"];
        var baseUrl = configuration["Cloudinary:UploadBaseUrl"] ?? "https://api.cloudinary.com";
        if (string.IsNullOrWhiteSpace(cloudName) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(apiSecret)
            || !Uri.TryCreate(baseUrl.TrimEnd('/') + $"/v1_1/{Uri.EscapeDataString(cloudName)}/auto/upload", UriKind.Absolute, out var uri)) return null;

        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var signature = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes($"timestamp={timestamp}{apiSecret}"))).ToLowerInvariant();
        using var body = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(content));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        body.Add(file, "file", "upload.bin");
        body.Add(new StringContent(apiKey), "api_key");
        body.Add(new StringContent(timestamp), "timestamp");
        body.Add(new StringContent(signature), "signature");

        try
        {
            using var response = await client.PostAsync(uri, body, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return document.RootElement.TryGetProperty("secure_url", out var reference) ? reference.GetString() : null;
        }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
    }
}

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

public sealed class ProfilePhotoWorkflow(IoBuildDbContext dbContext, ICloudinaryUploader uploader)
{
    public async Task<bool> ReplaceAsync(int userId, string expectedReference, string imageContent, CancellationToken cancellationToken = default)
    {
        var uploadedReference = await uploader.UploadAsync(imageContent, cancellationToken);
        if (string.IsNullOrWhiteSpace(uploadedReference)) return false;

        var profile = await dbContext.Profiles.SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (profile is null || !string.Equals(profile.PhotoReference, expectedReference, StringComparison.Ordinal)) return false;
        profile.PhotoReference = $"sha256:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(imageContent))).ToLowerInvariant()}";
        profile.CloudinaryReference = uploadedReference;
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}

public sealed class CoreBusinessService(IoBuildDbContext dbContext)
{
    public async Task<Project> CreateProjectAsync(string name, string description, string location, int totalUnits, int builderId, string? imageUrl, CancellationToken cancellationToken = default)
    {
        var project = new Project { Name = name, Description = description, Location = location, TotalUnits = totalUnits, BuilderId = builderId, ImageUrl = imageUrl };
        dbContext.Projects.Add(project);
        await dbContext.SaveChangesAsync(cancellationToken);
        return project;
    }

    public async Task<Profile> CreateProfileAsync(int userId, string name, string username, CancellationToken cancellationToken = default)
    {
        var profile = new Profile { UserId = userId, Name = name, Username = username };
        dbContext.Profiles.Add(profile);
        await dbContext.SaveChangesAsync(cancellationToken);
        return profile;
    }
}
