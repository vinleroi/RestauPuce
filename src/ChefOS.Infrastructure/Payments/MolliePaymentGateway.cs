using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ChefOS.Infrastructure.Payments;

/// <summary>La clé API Mollie (jamais journalisée, jamais incluse dans un message d'erreur).</summary>
public sealed record MollieSettings(string ApiKey)
{
    public override string ToString() => "MollieSettings { ApiKey = *** }";
}

/// <summary>
/// Passerelle Mollie par HttpClient typé (base <c>https://api.mollie.com/v2/</c>). Les erreurs (statut non 2xx, délai dépassé, réseau)
/// deviennent des <see cref="InvalidOperationException"/> en français qui ne contiennent ni la clé ni le corps de la réponse.
/// </summary>
public sealed class MolliePaymentGateway : IPaymentGateway
{
    public const string BaseAddress = "https://api.mollie.com/v2/";

    private readonly HttpClient _http;
    private readonly string _apiKey;

    public MolliePaymentGateway(HttpClient http, MollieSettings settings)
    {
        _http = http;
        _apiKey = settings.ApiKey;
        _http.BaseAddress ??= new Uri(BaseAddress);
    }

    public string Name => "Mollie";

    public async Task<GatewayPayment> CreateAsync(decimal amount, string description, string redirectUrl, string webhookUrl,
        IDictionary<string, string> metadata, CancellationToken ct)
    {
        var body = new Dictionary<string, object?>
        {
            ["amount"] = Amount(amount),
            ["description"] = description,
            ["redirectUrl"] = redirectUrl,
            ["metadata"] = metadata
        };
        if (!string.IsNullOrWhiteSpace(webhookUrl)) // absent en développement (adresse locale inaccessible à Mollie)
            body["webhookUrl"] = webhookUrl;

        using var json = await SendAsync(HttpMethod.Post, "payments", body, ct);
        var root = json.RootElement;
        try
        {
            var id = root.GetProperty("id").GetString();
            var checkout = root.GetProperty("_links").GetProperty("checkout").GetProperty("href").GetString();
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(checkout))
                throw new KeyNotFoundException();
            return new GatewayPayment(id, checkout);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or JsonException)
        {
            throw new InvalidOperationException(UnexpectedResponse);
        }
    }

    public async Task<GatewayStatus> GetStatusAsync(string id, CancellationToken ct)
    {
        using var json = await SendAsync(HttpMethod.Get, $"payments/{Uri.EscapeDataString(id)}", null, ct);
        string? status;
        try
        {
            status = json.RootElement.GetProperty("status").GetString();
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException(UnexpectedResponse);
        }

        return status switch
        {
            "open" or "pending" or "authorized" => GatewayStatus.Open,
            "paid" => GatewayStatus.Paid,
            "failed" => GatewayStatus.Failed,
            "expired" => GatewayStatus.Expired,
            "canceled" => GatewayStatus.Canceled,
            _ => throw new InvalidOperationException(UnexpectedResponse)
        };
    }

    public async Task RefundAsync(string id, decimal amount, string idempotencyKey, CancellationToken ct)
    {
        using var _ = await SendAsync(HttpMethod.Post, $"payments/{Uri.EscapeDataString(id)}/refunds",
            new Dictionary<string, object?> { ["amount"] = Amount(amount) }, ct, idempotencyKey);
    }

    public async Task<decimal> GetRefundedAmountAsync(string id, CancellationToken ct)
    {
        using var json = await SendAsync(HttpMethod.Get, $"payments/{Uri.EscapeDataString(id)}", null, ct);
        if (!json.RootElement.TryGetProperty("amountRefunded", out var refunded) || refunded.ValueKind != JsonValueKind.Object)
            return 0m;
        try
        {
            return decimal.Parse(refunded.GetProperty("value").GetString()!, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or FormatException or NullReferenceException or InvalidOperationException)
        {
            throw new InvalidOperationException(UnexpectedResponse);
        }
    }

    private const string UnexpectedResponse = "Réponse inattendue de Mollie, réessayez";

    private static Dictionary<string, string> Amount(decimal amount) => new()
    {
        ["currency"] = "EUR",
        ["value"] = amount.ToString("0.00", CultureInfo.InvariantCulture)
    };

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (idempotencyKey is not null)
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

        int code;
        string text;
        try
        {
            using var response = await _http.SendAsync(request, ct);
            code = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Mollie ne répond pas (délai dépassé), réessayez");
        }
        catch (HttpRequestException)
        {
            throw new InvalidOperationException("Impossible de joindre Mollie, réessayez");
        }

        if (code is < 200 or > 299)
            throw new InvalidOperationException(Translate(code));

        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(UnexpectedResponse);
        }
    }

    private static string Translate(int code) => code switch
    {
        401 or 403 => "Mollie a refusé l'authentification (clé API invalide)",
        404 => "Paiement introuvable chez Mollie",
        422 => "Mollie a refusé la demande (données invalides)",
        429 => "Mollie est momentanément saturé, réessayez",
        >= 500 => "Mollie est momentanément indisponible, réessayez",
        _ => $"Mollie a refusé la demande (code {code})"
    };
}

/// <summary>Passerelle enregistrée en mode Mollie sans clé : le site démarre, mais tout paiement échoue avec un message explicite.</summary>
public sealed class MissingKeyPaymentGateway : IPaymentGateway
{
    private const string Message = "Clé API Mollie non configurée : renseignez Mollie:ApiKey ou la variable d'environnement MOLLIE_API_KEY";

    public string Name => "Mollie";

    public Task<GatewayPayment> CreateAsync(decimal amount, string description, string redirectUrl, string webhookUrl,
        IDictionary<string, string> metadata, CancellationToken ct) => throw new InvalidOperationException(Message);

    public Task<GatewayStatus> GetStatusAsync(string id, CancellationToken ct) => throw new InvalidOperationException(Message);

    public Task RefundAsync(string id, decimal amount, string idempotencyKey, CancellationToken ct) => throw new InvalidOperationException(Message);

    public Task<decimal> GetRefundedAmountAsync(string id, CancellationToken ct) => throw new InvalidOperationException(Message);
}
