using System.Net;
using System.Text;
using System.Text.Json;
using Anthropic;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Receipts;

namespace ChefOS.Tests.Infrastructure;

/// <summary>
/// <see cref="ClaudeReceiptReader"/> face à une fausse API : le <see cref="HttpClient"/> du SDK est remplacé par un gestionnaire qui enregistre la requête
/// et renvoie une réponse préparée. Aucun appel réseau, aucune clé réelle.
/// </summary>
public class ClaudeReceiptReaderTests
{
    private const string ExtractionJson =
        """{"supplier_name":"Metro","date":"2026-11-03","invoice_number":null,"amounts_include_vat":false,"total":11.2,"lines":[{"label":"OIGNONS JAUNES FILET 5KG","quantity":10,"unit":"kg","unit_price":1.12,"line_total":11.2,"vat_rate":0.06}]}""";

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

    private static ClaudeReceiptReader ReaderFor(HttpMessageHandler handler) =>
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
    public async Task Read_ParsesTheStructuredAnswer()
    {
        var api = new FakeApi(() => MessageResponse(ExtractionJson));

        var extraction = await ReaderFor(api).ReadAsync([1, 2, 3], "image/jpeg");

        Assert.Equal("Metro", extraction.SupplierName);
        Assert.Equal(new DateOnly(2026, 11, 3), extraction.Date);
        var line = Assert.Single(extraction.Lines);
        Assert.Equal("OIGNONS JAUNES FILET 5KG", line.Label);
        Assert.Equal(10m, line.Quantity);
        Assert.Equal(MeasureUnit.Kg, line.Unit);
        Assert.Equal(0.06m, line.VatRate);
    }

    [Fact]
    public async Task Read_SendsTheExpectedRequest_ForAnImage()
    {
        var api = new FakeApi(() => MessageResponse(ExtractionJson));
        byte[] photo = [1, 2, 3, 4, 5];

        await ReaderFor(api).ReadAsync(photo, " Image/PNG ");

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
            JsonSerializer.SerializeToElement(ReceiptExtractionJson.Schema), format.GetProperty("schema")));

        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        var parts = message.GetProperty("content").EnumerateArray().ToList();
        Assert.Equal(2, parts.Count);

        Assert.Equal("image", parts[0].GetProperty("type").GetString());
        var source = parts[0].GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/png", source.GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(photo), source.GetProperty("data").GetString());

        Assert.Equal("text", parts[1].GetProperty("type").GetString());
        Assert.Equal(ClaudeReceiptReader.Prompt, parts[1].GetProperty("text").GetString());
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    public async Task Read_ImageTypes_AreSentAsBase64Images(string contentType)
    {
        var api = new FakeApi(() => MessageResponse(ExtractionJson));

        await ReaderFor(api).ReadAsync([9], contentType);

        using var body = JsonDocument.Parse(Assert.Single(api.Calls).Body);
        var first = body.RootElement.GetProperty("messages")[0].GetProperty("content")[0];
        Assert.Equal("image", first.GetProperty("type").GetString());
        Assert.Equal(contentType, first.GetProperty("source").GetProperty("media_type").GetString());
    }

    [Fact]
    public async Task Read_Pdf_IsSentAsADocument()
    {
        var api = new FakeApi(() => MessageResponse(ExtractionJson));
        byte[] pdf = [37, 80, 68, 70];

        await ReaderFor(api).ReadAsync(pdf, "application/pdf");

        using var body = JsonDocument.Parse(Assert.Single(api.Calls).Body);
        var parts = body.RootElement.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("document", parts[0].GetProperty("type").GetString());
        var source = parts[0].GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("application/pdf", source.GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(pdf), source.GetProperty("data").GetString());
        Assert.Equal("text", parts[1].GetProperty("type").GetString());
    }

    [Fact]
    public async Task Read_UnsupportedType_FailsBeforeAnyCall()
    {
        var api = new FakeApi(() => MessageResponse(ExtractionJson));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReaderFor(api).ReadAsync([1], "text/plain"));

        Assert.Equal("Format non pris en charge (JPEG, PNG, WebP ou PDF)", ex.Message);
        Assert.Empty(api.Calls);
    }

    [Fact]
    public void Prompt_CarriesTheInstructionsOfTheBrief()
    {
        var prompt = ClaudeReceiptReader.Prompt;

        Assert.Contains("fournisseur de restaurant", prompt);
        Assert.Contains("libellé", prompt);
        Assert.Contains("TTC", prompt);
        Assert.Contains("6 x 1L", prompt);
        Assert.Contains("2 x 2,5 kg", prompt);
        Assert.Contains("kg ou g", prompt);
        Assert.Contains("piece", prompt);
        Assert.Contains("0.06", prompt);
        Assert.Contains("remise", prompt);
        Assert.Contains("consigne", prompt);
        Assert.Contains("sous-total", prompt);
    }

    [Fact]
    public void Prompt_CoversBelgianWholesaleTickets()
    {
        var prompt = ClaudeReceiptReader.Prompt;

        // Dates jour/mois/année.
        Assert.Contains("jour/mois/année", prompt);
        Assert.Contains("3 novembre 2026, pas le 11 mars", prompt);
        // TVA par codes, résolue avec la légende du ticket.
        Assert.Contains("(A, B, C…)", prompt);
        Assert.Contains("légende imprimée sur le ticket", prompt);
        // Les lignes de vidange sont omises comme celles de consigne.
        Assert.Contains("de consigne, de vidange", prompt);
        // Quantité commandée × conditionnement du libellé.
        Assert.Contains("2 commandés de « FILET OIGNONS 5KG » donnent 10 avec l'unité kg", prompt);
    }

    [Fact]
    public async Task Read_Refusal_Throws()
    {
        var api = new FakeApi(() => MessageResponse(text: null, stopReason: "refusal"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReaderFor(api).ReadAsync([1], "image/jpeg"));

        Assert.Equal("La lecture du ticket a été refusée", ex.Message);
    }

    [Fact]
    public async Task Read_MaxTokens_Throws_EvenWithPartialText()
    {
        var api = new FakeApi(() => MessageResponse("""{"supplier_name":"Metro","lines":[{"label":"BEUR""", stopReason: "max_tokens"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReaderFor(api).ReadAsync([1], "image/jpeg"));

        Assert.Equal("Ticket trop long pour être lu", ex.Message);
    }

    [Fact]
    public async Task Read_AnswerWithoutText_IsUnreadable()
    {
        var api = new FakeApi(() => MessageResponse(text: null));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReaderFor(api).ReadAsync([1], "image/jpeg"));

        Assert.Equal("Réponse de lecture illisible", ex.Message);
    }

    [Fact]
    public async Task Read_AnswerThatIsNotJson_IsUnreadable()
    {
        var api = new FakeApi(() => MessageResponse("Je ne peux pas lire ce ticket."));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReaderFor(api).ReadAsync([1], "image/jpeg"));

        Assert.Equal("Réponse de lecture illisible", ex.Message);
    }

    [Theory]
    [InlineData(401, "authentication_error", "Clé API Anthropic refusée")]
    [InlineData(402, "billing_error", "Crédit Anthropic épuisé")]
    [InlineData(403, "permission_error", "n'a pas le droit")]
    [InlineData(404, "not_found_error", "Modèle Anthropic introuvable")]
    [InlineData(400, "invalid_request_error", "Anthropic a refusé la requête (format ou taille du fichier non accepté)")]
    [InlineData(413, "request_too_large", "(HTTP 413)")]
    [InlineData(429, "rate_limit_error", "Quota Anthropic atteint")]
    [InlineData(500, "api_error", "indisponible ou surchargé")]
    [InlineData(529, "overloaded_error", "indisponible ou surchargé")]
    public async Task Read_ApiErrors_AreTranslatedToFrench(int status, string type, string expectedFragment)
    {
        var api = new FakeApi(() => ErrorResponse((HttpStatusCode)status, type, "détail technique"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReaderFor(api).ReadAsync([1], "image/jpeg"));

        Assert.Contains(expectedFragment, ex.Message);
        Assert.DoesNotContain("détail technique", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Read_BadRequest_KeepsTheEnglishReasonOnlyInTheInnerException()
    {
        var api = new FakeApi(() => ErrorResponse(HttpStatusCode.BadRequest, "invalid_request_error", "image exceeds 5 MB maximum"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReaderFor(api).ReadAsync([1], "image/jpeg"));

        Assert.Equal("Anthropic a refusé la requête (format ou taille du fichier non accepté)", ex.Message);
        Assert.Contains("image exceeds 5 MB maximum", ex.InnerException!.Message);
    }

    [Fact]
    public async Task Read_ImageOver5MB_FailsBeforeAnyCall()
    {
        var api = new FakeApi(() => MessageResponse(ExtractionJson));
        var tooBig = new byte[5 * 1024 * 1024 + 1];

        foreach (var type in new[] { "image/jpeg", "image/png", "image/webp" })
        {
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReaderFor(api).ReadAsync(tooBig, type));
            Assert.Equal("Image trop volumineuse pour la lecture (5 Mo maximum) : reprenez la photo ou réduisez-la", ex.Message);
        }

        Assert.Empty(api.Calls);
    }

    [Fact]
    public async Task Read_ImageOfExactly5MB_IsSent()
    {
        var api = new FakeApi(() => MessageResponse(ExtractionJson));

        await ReaderFor(api).ReadAsync(new byte[5 * 1024 * 1024], "image/jpeg");

        Assert.Single(api.Calls);
    }

    [Fact]
    public async Task Read_PdfOver5MB_IsSent()
    {
        var api = new FakeApi(() => MessageResponse(ExtractionJson));

        await ReaderFor(api).ReadAsync(new byte[6 * 1024 * 1024], "application/pdf");

        Assert.Single(api.Calls);
    }

    [Fact]
    public async Task Read_UnsupportedTypeIsReportedBeforeTheImageSize()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ReaderFor(new FakeApi(() => MessageResponse(ExtractionJson))).ReadAsync(new byte[6 * 1024 * 1024], "image/gif"));

        Assert.Equal("Format non pris en charge (JPEG, PNG, WebP ou PDF)", ex.Message);
    }

    [Fact]
    public async Task Read_OkResponseThatIsNotJson_IsUnreadable()
    {
        var api = new FakeApi(() => Json(HttpStatusCode.OK, "<html>pas du json</html>"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReaderFor(api).ReadAsync([1], "image/jpeg"));

        Assert.Equal("Réponse de lecture illisible", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Read_OkResponseWithoutContent_IsUnreadable()
    {
        var api = new FakeApi(() => Json(HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            id = "msg_test",
            type = "message",
            role = "assistant",
            model = "claude-opus-5-5",
            stop_reason = "end_turn",
            stop_sequence = (string?)null,
            usage = new { input_tokens = 10, output_tokens = 20 }
        })));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReaderFor(api).ReadAsync([1], "image/jpeg"));

        Assert.Equal("Réponse de lecture illisible", ex.Message);
        Assert.NotNull(ex.InnerException);
    }

    [Fact]
    public async Task Read_OkResponseWithWrongContentShape_IsUnreadable()
    {
        var api = new FakeApi(() => Json(HttpStatusCode.OK, """{"id":"msg_test","type":"message","role":"assistant","model":"claude-opus-5-5","content":"oups","stop_reason":"end_turn","usage":{"input_tokens":1,"output_tokens":1}}"""));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ReaderFor(api).ReadAsync([1], "image/jpeg"));

        Assert.Equal("Réponse de lecture illisible", ex.Message);
    }

    [Fact]
    public async Task Read_NetworkFailure_IsTranslatedToFrench()
    {
        var reader = ReaderFor(new ThrowingApi(new HttpRequestException("connexion refusée")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync([1], "image/jpeg"));

        Assert.Contains("Impossible de joindre Anthropic", ex.Message);
    }

    [Fact]
    public async Task Read_ClientTimeout_IsTranslatedToFrench()
    {
        var reader = ReaderFor(new ThrowingApi(new TaskCanceledException("délai dépassé", new TimeoutException())));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync([1], "image/jpeg"));

        Assert.Contains("trop de temps", ex.Message);
    }

    [Fact]
    public async Task Read_CallerCancellation_Propagates()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var reader = ReaderFor(new FakeApi(() => MessageResponse(ExtractionJson)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync([1], "image/jpeg", cts.Token));
    }

    [Fact]
    public async Task Read_CancellationDuringTheCall_IsNotTranslatedEvenWhenTheSdkWrapsIt()
    {
        using var cts = new CancellationTokenSource();
        var reader = ReaderFor(new CancellingApi(cts));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader.ReadAsync([1], "image/jpeg", cts.Token));
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
}
