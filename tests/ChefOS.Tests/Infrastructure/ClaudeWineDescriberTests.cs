using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Wines;

namespace ChefOS.Tests.Infrastructure;

/// <summary>
/// <see cref="ClaudeWineDescriber"/> face à une fausse API : le <see cref="HttpClient"/> du SDK est remplacé par un gestionnaire qui enregistre la requête
/// et renvoie une réponse préparée. Aucun appel réseau, aucune clé réelle.
/// </summary>
public class ClaudeWineDescriberTests
{
    private const string DraftJson =
        """{"appellation":"Chablis","region":"Bourgogne","country":"France","color":"Blanc","grapes":"Chardonnay","robe":"Or pâle.","nez":"Agrumes.","bouche":"Vive et minérale.","garde":"3 à 6 ans.","service_temperature":"10-12 °C","general_pairings":"Poissons et fruits de mer.","suggested_pairing_dish_names":["Sole meunière"]}""";

    private static readonly WineDescribeRequest Request = new("Chablis", 2022, "Domaine X", null, ["Sole meunière", "Entrecôte"]);

    private sealed record Call(string Path, string? Beta, string Body);

    private sealed class FakeApi(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            request.Headers.TryGetValues("anthropic-beta", out var beta);
            Calls.Add(new Call(request.RequestUri!.AbsolutePath, beta is null ? null : string.Join(",", beta), body));
            return respond();
        }
    }

    private sealed class ThrowingApi(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(exception);
    }

    private static ClaudeWineDescriber DescriberFor(HttpMessageHandler handler) =>
        new(new AnthropicClient { ApiKey = "sk-ant-test", HttpClient = new HttpClient(handler), MaxRetries = 0 });

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage MessageResponse(string? text, string stopReason = "end_turn") =>
        Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model = "claude-opus-5-5",
            content = text is null ? [] : new[] { new { type = "text", text } },
            stop_reason = stopReason,
            stop_sequence = (string?)null,
            usage = new { input_tokens = 10, output_tokens = 20 }
        }));

    private static HttpResponseMessage ErrorResponse(HttpStatusCode status, string type, string message) =>
        Json(status, JsonSerializer.Serialize(new { type = "error", error = new { type, message }, request_id = "req_test" }));

    [Fact]
    public async Task Describe_ParsesTheStructuredAnswer()
    {
        var api = new FakeApi(() => MessageResponse(DraftJson));

        var draft = await DescriberFor(api).DescribeAsync(Request);

        Assert.Equal("Chablis", draft.Appellation);
        Assert.Equal(WineColor.Blanc, draft.Color);
        Assert.Equal("Vive et minérale.", draft.Bouche);
        Assert.Equal(["Sole meunière"], draft.SuggestedPairingDishNames);
    }

    [Fact]
    public async Task Describe_SendsTheExpectedRequest()
    {
        var api = new FakeApi(() => MessageResponse(DraftJson));

        await DescriberFor(api).DescribeAsync(Request);

        var call = Assert.Single(api.Calls);
        Assert.Equal("/v1/messages", call.Path);
        Assert.Contains("server-side-fallback-2026-07-01", call.Beta);

        using var body = JsonDocument.Parse(call.Body);
        var root = body.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal(16000, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal(JsonValueKind.String, root.GetProperty("fallbacks").ValueKind);
        Assert.Equal("default", root.GetProperty("fallbacks").GetString());
        Assert.Equal("medium", root.GetProperty("output_config").GetProperty("effort").GetString());

        // Réflexion adaptative implicite, pas d'échantillonnage, pas de préremplissage.
        Assert.False(root.TryGetProperty("thinking", out _));
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.False(root.TryGetProperty("top_p", out _));

        var format = root.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        Assert.True(JsonElement.DeepEquals(
            JsonSerializer.SerializeToElement(WineDescriptionJson.Schema), format.GetProperty("schema")));

        // Un seul message utilisateur, en texte seul (aucune image).
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        var parts = message.GetProperty("content");
        var text = parts.ValueKind == JsonValueKind.String
            ? parts.GetString()
            : Assert.Single(parts.EnumerateArray()).GetProperty("text").GetString();
        Assert.Equal(ClaudeWineDescriber.BuildMessage(Request), text);
    }

    [Fact]
    public void Message_CarriesTheWineAndTheDishes()
    {
        var text = ClaudeWineDescriber.BuildMessage(Request);

        Assert.Contains("Nom du vin : Chablis", text);
        Assert.Contains("Millésime : 2022", text);
        Assert.Contains("Producteur : Domaine X", text);
        Assert.Contains("Appellation : non précisée", text);
        Assert.Contains("- Sole meunière", text);
        Assert.Contains("- Entrecôte", text);
    }

    [Fact]
    public void Message_WithoutDishesOrDetails_SaysSo()
    {
        var text = ClaudeWineDescriber.BuildMessage(new WineDescribeRequest("Cuvée", null, null, "Médoc", []));

        Assert.Contains("Millésime : non précisé", text);
        Assert.Contains("Producteur : non précisé", text);
        Assert.Contains("Appellation : Médoc", text);
        Assert.Contains("(aucun)", text);
    }

    [Fact]
    public void Prompt_CarriesTheInstructionsOfTheBrief()
    {
        var prompt = ClaudeWineDescriber.Prompt;

        Assert.Contains("sommelier", prompt);
        Assert.Contains("carte des vins publique", prompt);
        Assert.Contains("N'invente jamais de producteur", prompt);
        Assert.Contains("récompense", prompt);
        Assert.Contains("caractéristiques typiques du millésime", prompt);
        Assert.Contains("2 à 3 phrases", prompt);
        Assert.Contains("uniquement avec les plats de la liste fournie", prompt);
        Assert.Contains("inconnu ou ambigu", prompt);
        Assert.Contains("ne dis rien de faux", prompt);
    }

    [Fact]
    public async Task Describe_Refusal_Throws()
    {
        var api = new FakeApi(() => MessageResponse(text: null, stopReason: "refusal"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DescriberFor(api).DescribeAsync(Request));

        Assert.Equal("La rédaction de la fiche a été refusée", ex.Message);
    }

    [Fact]
    public async Task Describe_MaxTokens_Throws_EvenWithPartialText()
    {
        var api = new FakeApi(() => MessageResponse("""{"appellation":"Chab""", stopReason: "max_tokens"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DescriberFor(api).DescribeAsync(Request));

        Assert.Equal("Fiche trop longue pour être rédigée", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Je ne connais pas ce vin.")]
    public async Task Describe_UnusableAnswer_IsUnreadable(string? text)
    {
        var api = new FakeApi(() => MessageResponse(text));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DescriberFor(api).DescribeAsync(Request));

        Assert.Equal("Réponse de l'assistant illisible", ex.Message);
    }

    [Fact]
    public async Task Describe_OkResponseThatIsNotJson_IsUnreadable()
    {
        var api = new FakeApi(() => Json(HttpStatusCode.OK, "<html>pas du json</html>"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DescriberFor(api).DescribeAsync(Request));

        Assert.Equal("Réponse de l'assistant illisible", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Theory]
    [InlineData(401, "authentication_error", "Clé API Anthropic refusée")]
    [InlineData(402, "billing_error", "Crédit Anthropic épuisé")]
    [InlineData(403, "permission_error", "n'a pas le droit")]
    [InlineData(404, "not_found_error", "Modèle Anthropic introuvable")]
    [InlineData(400, "invalid_request_error", "Anthropic a refusé la requête")]
    [InlineData(413, "request_too_large", "(HTTP 413)")]
    [InlineData(429, "rate_limit_error", "Quota Anthropic atteint")]
    [InlineData(500, "api_error", "indisponible ou surchargé")]
    [InlineData(529, "overloaded_error", "indisponible ou surchargé")]
    public async Task Describe_ApiErrors_AreTranslatedToFrench(int status, string type, string expectedFragment)
    {
        var api = new FakeApi(() => ErrorResponse((HttpStatusCode)status, type, "détail technique"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => DescriberFor(api).DescribeAsync(Request));

        Assert.Contains(expectedFragment, ex.Message);
        Assert.DoesNotContain("détail technique", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Describe_NetworkFailure_IsTranslatedToFrench()
    {
        var describer = DescriberFor(new ThrowingApi(new HttpRequestException("connexion refusée")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => describer.DescribeAsync(Request));

        Assert.Contains("Impossible de joindre Anthropic", ex.Message);
    }

    [Fact]
    public async Task Describe_ClientTimeout_IsTranslatedToFrench()
    {
        var describer = DescriberFor(new ThrowingApi(new TaskCanceledException("délai dépassé", new TimeoutException())));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => describer.DescribeAsync(Request));

        Assert.Contains("trop de temps", ex.Message);
    }

    [Fact]
    public async Task Describe_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var describer = DescriberFor(new FakeApi(() => MessageResponse(DraftJson)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => describer.DescribeAsync(Request, cts.Token));
    }

    [Fact]
    public async Task Describe_CancellationDuringTheCall_IsNotTranslatedEvenWhenTheSdkWrapsIt()
    {
        using var cts = new CancellationTokenSource();
        var describer = DescriberFor(new CancellingApi(cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => describer.DescribeAsync(Request, cts.Token));
    }

    /// <summary>Annule l'appelant en plein appel, puis échoue comme le ferait un transport interrompu (le SDK range cela dans ses propres exceptions).</summary>
    private sealed class CancellingApi(CancellationTokenSource cts) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await cts.CancelAsync();
            throw new HttpRequestException("transport interrompu");
        }
    }

    [Fact]
    public void Configure_GivesTheSharedClientATwoMinuteTimeout_AndKeepsItsOtherSettings()
    {
        var shared = new AnthropicClient { ApiKey = "sk-ant-test", MaxRetries = 1, Timeout = TimeSpan.FromMinutes(5) };

        var configured = ClaudeWineDescriber.Configure(shared);

        Assert.Equal(TimeSpan.FromMinutes(2), configured.Timeout);
        Assert.Equal(1, configured.MaxRetries);
        Assert.Equal("sk-ant-test", configured.ApiKey);
        Assert.Equal(TimeSpan.FromMinutes(5), shared.Timeout);
    }
}
