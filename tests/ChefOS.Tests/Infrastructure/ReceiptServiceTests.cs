using ChefOS.Core.Common;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Core.Recipes;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Tests.Infrastructure;

[Collection("postgres")]
public class ReceiptServiceTests(PostgresFixture postgres)
{
    private const string UnsupportedTypeMessage = "Format non pris en charge (JPEG, PNG, WebP ou PDF)";
    private const string TooLargeMessage = "Fichier trop volumineux (10 Mo maximum)";
    private const string UnknownReceiptMessage = "Ticket introuvable";
    private const string UnknownIngredientMessage = "Ingrédient introuvable";
    private const string UnknownSupplierMessage = "Fournisseur introuvable";
    private const string UnknownLineMessage = "Ligne introuvable";
    private const string AlreadyAppliedMessage = "Ce ticket a déjà été appliqué";
    private const string NotReadyMessage = "Ce ticket n'est pas prêt à être appliqué";
    private const string NoLineReadMessage = "Aucune ligne n'a été lue sur ce ticket";

    private static readonly byte[] SomeBytes = [1, 2, 3];
    private static readonly DateTime FutureDay = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    // ── Mise en place ────────────────────────────────────────────────────────

    /// <summary>Lecteur configurable : renvoie <see cref="Result"/> ou lève <see cref="Failure"/>.</summary>
    private sealed class FakeReceiptReader : IReceiptReader
    {
        public ReceiptExtraction? Result { get; set; }
        public Exception? Failure { get; set; }
        public Action? OnRead { get; set; }
        public List<(byte[] Content, string ContentType)> Calls { get; } = [];

        public Task<ReceiptExtraction> ReadAsync(byte[] content, string contentType, CancellationToken ct = default)
        {
            Calls.Add((content, contentType));
            OnRead?.Invoke();
            if (Failure is not null)
                throw Failure;

            return Task.FromResult(Result ?? throw new InvalidOperationException("Aucun résultat configuré"));
        }
    }

    private async Task<(DbContextOptions<ChefOsDbContext> Options, int RestaurantId, FakeReceiptReader Reader, ReceiptService Service, IngredientService Ingredients)>
        ArrangeAsync(string name = "Bistro")
    {
        var options = await postgres.CreateDatabaseAsync();
        var restaurant = await TestData.CreateRestaurantAsync(options, name);
        var factory = new TestContextFactory(options, restaurant);
        var reader = new FakeReceiptReader();
        var ingredients = new IngredientService(factory);
        return (options, restaurant, reader, new ReceiptService(factory, reader, ingredients), ingredients);
    }

    private static ChefOsDbContext OpenContext(DbContextOptions<ChefOsDbContext> options, int restaurantId) =>
        new TestContextFactory(options, restaurantId).CreateDbContext();

    private static ReceiptService ServiceFor(DbContextOptions<ChefOsDbContext> options, int restaurantId, IReceiptReader reader)
    {
        var factory = new TestContextFactory(options, restaurantId);
        return new ReceiptService(factory, reader, new IngredientService(factory));
    }

    private static async Task<int> AddSupplierAsync(DbContextOptions<ChefOsDbContext> options, int restaurantId, string name)
    {
        var supplier = await new SupplierService(new TestContextFactory(options, restaurantId))
            .SaveAsync(new SupplierInput(null, name, null, null, null));
        return supplier.Id;
    }

    private static async Task<int> AddIngredientAsync(
        IngredientService ingredients, string name, decimal? pricePerBaseUnit = null, BaseUnit baseUnit = BaseUnit.Gram)
    {
        var ingredient = await ingredients.SaveAsync(new IngredientInput(
            null, name, "Divers", EuAllergen.None, baseUnit, pricePerBaseUnit, 100m, null, null));
        return ingredient.Id;
    }

    private static ExtractedLine Line(
        string label, decimal? quantity, MeasureUnit? unit, decimal? lineTotal, decimal? vatRate = null, decimal? unitPrice = null) =>
        new(label, quantity, unit, unitPrice, lineTotal, vatRate);

    private static ReceiptExtraction Extraction(
        IReadOnlyList<ExtractedLine> lines,
        string? supplier = "METRO",
        DateOnly? date = null,
        bool includeVat = false,
        decimal? total = null,
        string? invoice = null) =>
        new(supplier, date ?? new DateOnly(2099, 1, 15), invoice, includeVat, total, lines);

    /// <summary>Téléverse puis lit un ticket avec le lecteur factice et renvoie son identifiant.</summary>
    private static async Task<int> ReadAsync(ReceiptService service, FakeReceiptReader reader, ReceiptExtraction extraction)
    {
        reader.Result = extraction;
        var id = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");
        await service.ExtractAsync(id);
        return id;
    }

    /// <summary>Le ticket tel que l'interface le renverrait, sans modification.</summary>
    private static ReceiptDraftInput DraftOf(Receipt receipt, Func<ReceiptLine, ReceiptLineInput, ReceiptLineInput>? edit = null) =>
        new(
            receipt.Id,
            receipt.SupplierId,
            receipt.ReceiptDate,
            receipt.InvoiceNumber,
            receipt.Lines.OrderBy(l => l.Position).Select(l =>
            {
                var input = new ReceiptLineInput(
                    l.Id, l.RawLabel, l.Quantity, l.Unit, l.LineTotalExclVat, l.VatRate, l.IngredientId, l.IsIgnored);
                return edit is null ? input : edit(l, input);
            }).ToList());

    private static async Task<Receipt> LoadAsync(ReceiptService service, int id)
    {
        var receipt = await service.GetAsync(id);
        Assert.NotNull(receipt);
        return receipt;
    }

    private static async Task<DomainValidationException> AssertRefusedAsync(Func<Task> action, string message)
    {
        var ex = await Assert.ThrowsAsync<DomainValidationException>(action);
        Assert.Equal(message, ex.Message);
        return ex;
    }

    // ── Création ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Create_StoresPendingReceiptAndFile()
    {
        var (_, _, _, service, _) = await ArrangeAsync();

        var before = DateTime.UtcNow;
        var id = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");
        var after = DateTime.UtcNow;

        var receipt = await LoadAsync(service, id);
        Assert.Equal(ReceiptStatus.Pending, receipt.Status);
        Assert.Equal(DateTimeKind.Utc, receipt.CreatedAt.Kind);
        Assert.InRange(receipt.CreatedAt, before, after);
        Assert.Empty(receipt.Lines);

        var file = await service.GetFileAsync(id);
        Assert.NotNull(file);
        Assert.Equal("ticket.jpg", file.FileName);
        Assert.Equal("image/jpeg", file.ContentType);
        Assert.Equal(SomeBytes, file.Data);
    }

    [Theory]
    [InlineData("image/jpeg")]
    [InlineData("image/png")]
    [InlineData("image/webp")]
    [InlineData("application/pdf")]
    [InlineData("IMAGE/JPEG")]
    public async Task Create_AcceptsSupportedTypes(string contentType)
    {
        var (_, _, _, service, _) = await ArrangeAsync();

        var id = await service.CreateAsync(SomeBytes, "ticket", contentType);

        var file = await service.GetFileAsync(id);
        Assert.NotNull(file);
        Assert.Equal(contentType.ToLowerInvariant(), file.ContentType);
    }

    [Theory]
    [InlineData("image/gif")]
    [InlineData("text/plain")]
    [InlineData("")]
    public async Task Create_RejectsUnsupportedType(string contentType)
    {
        var (options, restaurant, _, service, _) = await ArrangeAsync();

        await AssertRefusedAsync(() => service.CreateAsync(SomeBytes, "ticket.gif", contentType), UnsupportedTypeMessage);

        await using var db = OpenContext(options, restaurant);
        Assert.Empty(await db.Receipts.ToListAsync());
    }

    [Fact]
    public async Task Create_RejectsTooLarge()
    {
        var (options, restaurant, _, service, _) = await ArrangeAsync();

        await AssertRefusedAsync(
            () => service.CreateAsync(new byte[10 * 1024 * 1024 + 1], "gros.pdf", "application/pdf"), TooLargeMessage);

        await using var db = OpenContext(options, restaurant);
        Assert.Empty(await db.Receipts.ToListAsync());
        Assert.Empty(await db.ReceiptFiles.ToListAsync());
    }

    [Fact]
    public async Task Create_AcceptsExactlyTenMegabytes()
    {
        var (_, _, _, service, _) = await ArrangeAsync();

        var id = await service.CreateAsync(new byte[10 * 1024 * 1024], "limite.pdf", "application/pdf");

        var file = await service.GetFileAsync(id);
        Assert.NotNull(file);
        Assert.Equal(10 * 1024 * 1024, file.Data.Length);
    }

    // ── Lecture du ticket ────────────────────────────────────────────────────

    [Fact]
    public async Task Extract_MapsHeaderLinesAndSuggestions()
    {
        var (options, restaurant, reader, service, ingredients) = await ArrangeAsync();
        var metro = await AddSupplierAsync(options, restaurant, "Metro");
        var farine = await AddIngredientAsync(ingredients, "Farine");
        reader.Result = Extraction(
            [
                Line("FARINE T55 1KG", 2m, MeasureUnit.Kg, 3.10m, unitPrice: 1.55m),
                Line("SACS POUBELLE", 1m, MeasureUnit.Piece, 4.50m),
                Line("DIVERS", null, null, null)
            ],
            supplier: "METRO CASH & CARRY",
            date: new DateOnly(2026, 11, 3),
            total: 7.60m,
            invoice: "F-2026-001");
        var id = await service.CreateAsync(SomeBytes, "ticket.pdf", "application/pdf");

        await service.ExtractAsync(id);

        var call = Assert.Single(reader.Calls);
        Assert.Equal(SomeBytes, call.Content);
        Assert.Equal("application/pdf", call.ContentType);
        var receipt = await LoadAsync(service, id);
        Assert.Equal(ReceiptStatus.Extracted, receipt.Status);
        Assert.Null(receipt.ErrorMessage);
        Assert.Equal("METRO CASH & CARRY", receipt.SupplierNameRaw);
        Assert.Equal(metro, receipt.SupplierId);
        Assert.Equal(new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc), receipt.ReceiptDate);
        Assert.Equal(DateTimeKind.Utc, receipt.ReceiptDate!.Value.Kind);
        Assert.Equal("F-2026-001", receipt.InvoiceNumber);
        Assert.False(receipt.AmountsIncludeVat);
        Assert.Equal(7.60m, receipt.Total);

        Assert.Equal([0, 1, 2], receipt.Lines.Select(l => l.Position));
        Assert.Equal(["FARINE T55 1KG", "SACS POUBELLE", "DIVERS"], receipt.Lines.Select(l => l.RawLabel));
        var first = receipt.Lines[0];
        Assert.Equal(2m, first.Quantity);
        Assert.Equal(MeasureUnit.Kg, first.Unit);
        Assert.Equal(1.55m, first.UnitPrice);
        Assert.Equal(3.10m, first.LineTotal);
        Assert.Equal(3.10m, first.LineTotalExclVat);   // ticket en HT : total repris tel quel
        Assert.False(first.VatToCheck);
        Assert.Equal(farine, first.IngredientId);
        Assert.Equal(MatchSource.NameMatch, first.MatchSource);
        Assert.All(receipt.Lines.Skip(1), l => Assert.Null(l.IngredientId));
        Assert.All(receipt.Lines.Skip(1), l => Assert.Null(l.MatchSource));
        Assert.All(receipt.Lines, l => Assert.False(l.IsIgnored));
        Assert.All(receipt.Lines, l => Assert.Null(l.AppliedPricePerBaseUnit));
        Assert.All(receipt.Lines, l => Assert.Equal(restaurant, l.RestaurantId));
    }

    [Fact]
    public async Task Extract_UsesSupplierAliasFirst()
    {
        var (options, restaurant, reader, service, ingredients) = await ArrangeAsync();
        var metro = await AddSupplierAsync(options, restaurant, "Metro");
        var autre = await AddSupplierAsync(options, restaurant, "Promocash");
        var beurre = await AddIngredientAsync(ingredients, "Beurre");
        var margarine = await AddIngredientAsync(ingredients, "Margarine");
        var crème = await AddIngredientAsync(ingredients, "Crème");
        var huile = await AddIngredientAsync(ingredients, "Huile");
        await using (var db = OpenContext(options, restaurant))
        {
            db.SupplierAliases.Add(new SupplierAlias { SupplierId = metro, NormalizedLabel = LabelNormalizer.Normalize("Beurre 250g"), IngredientId = margarine });
            db.SupplierAliases.Add(new SupplierAlias { SupplierId = autre, NormalizedLabel = LabelNormalizer.Normalize("Beurre 250g"), IngredientId = huile });
            db.SupplierAliases.Add(new SupplierAlias { SupplierId = null, NormalizedLabel = LabelNormalizer.Normalize("Crème pro"), IngredientId = crème });
            await db.SaveChangesAsync();
        }

        var id = await ReadAsync(service, reader, Extraction(
        [
            Line("Beurre 250g", 1m, MeasureUnit.Kg, 5m),
            Line("Crème PRO", 1m, MeasureUnit.L, 3m),
            Line("Beurre doux", 1m, MeasureUnit.Kg, 6m)
        ], supplier: "Metro"));

        var lines = (await LoadAsync(service, id)).Lines;
        Assert.Equal(margarine, lines[0].IngredientId);          // l'alias de Metro l'emporte sur le nom (« Beurre »)
        Assert.Equal(MatchSource.SupplierAlias, lines[0].MatchSource);
        Assert.Equal(crème, lines[1].IngredientId);
        Assert.Equal(MatchSource.GlobalAlias, lines[1].MatchSource);
        Assert.Equal(beurre, lines[2].IngredientId);             // pas d'alias : rapprochement par nom
        Assert.Equal(MatchSource.NameMatch, lines[2].MatchSource);
    }

    [Fact]
    public async Task Extract_ArchivedIngredient_IsNotSuggested()
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        var farine = await AddIngredientAsync(ingredients, "Farine");
        await ingredients.SetArchivedAsync(farine, true);

        var id = await ReadAsync(service, reader, Extraction([Line("FARINE", 1m, MeasureUnit.Kg, 2m)]));

        var line = Assert.Single((await LoadAsync(service, id)).Lines);
        Assert.Null(line.IngredientId);
        Assert.Null(line.MatchSource);
    }

    [Theory]
    [InlineData("Metro|Metro Cash Carry", "METRO CASH & CARRY", "Metro Cash Carry")] // égalité exacte avant le contenu
    [InlineData("Metro|Metro Cash", "METRO CASH CARRY", "Metro Cash")]               // plusieurs contenus : le plus long
    [InlineData("Metro", "METRO CASH CARRY", "Metro")]                               // fournisseur contenu dans le nom lu
    [InlineData("Metro Cash & Carry", "METRO", "Metro Cash & Carry")]                // nom lu contenu dans le fournisseur
    [InlineData("Metro", "METROPOLE TRAITEUR", null)]                                // un mot partiel ne compte pas
    [InlineData("Metro", "Promocash", null)]
    [InlineData("Metro", "", null)]
    [InlineData("Metro", null, null)]
    public async Task Extract_MatchesSupplierByNormalizedName(string suppliers, string? readName, string? expected)
    {
        var (options, restaurant, reader, service, _) = await ArrangeAsync();
        foreach (var name in suppliers.Split('|'))
            await AddSupplierAsync(options, restaurant, name);

        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m)], supplier: readName));

        var receipt = await LoadAsync(service, id);
        Assert.Equal(readName ?? string.Empty, receipt.SupplierNameRaw);
        if (expected is null)
        {
            Assert.Null(receipt.SupplierId);
            return;
        }

        await using var db = OpenContext(options, restaurant);
        var matched = await db.Suppliers.SingleAsync(s => s.Id == receipt.SupplierId);
        Assert.Equal(expected, matched.Name);
    }

    [Fact]
    public async Task Extract_OtherRestaurantSupplier_IsNotMatched()
    {
        var (options, _, reader, service, _) = await ArrangeAsync();
        var other = await TestData.CreateRestaurantAsync(options, "Autre");
        await AddSupplierAsync(options, other, "Metro");

        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m)], supplier: "METRO"));

        Assert.Null((await LoadAsync(service, id)).SupplierId);
    }

    [Fact]
    public async Task Extract_ReaderThrows_MarksFailed()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        reader.Failure = new InvalidOperationException("Le service de lecture est indisponible");
        var id = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");

        await service.ExtractAsync(id); // ne relance pas l'exception

        var receipt = await LoadAsync(service, id);
        Assert.Equal(ReceiptStatus.Failed, receipt.Status);
        Assert.Equal("Le service de lecture est indisponible", receipt.ErrorMessage);
        Assert.Empty(receipt.Lines);
    }

    [Fact]
    public async Task Extract_ReaderTimeout_MarksFailed()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        reader.Failure = new TaskCanceledException("Délai dépassé"); // le jeton de l'appelant n'est pas annulé
        var id = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");

        await service.ExtractAsync(id);

        var receipt = await LoadAsync(service, id);
        Assert.Equal(ReceiptStatus.Failed, receipt.Status);
        Assert.Equal("Délai dépassé", receipt.ErrorMessage);
    }

    [Fact]
    public async Task Extract_CallerCancellation_IsPropagatedAndKeepsStatus()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        using var cts = new CancellationTokenSource();
        reader.OnRead = cts.Cancel;
        reader.Failure = new OperationCanceledException(cts.Token);
        var id = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExtractAsync(id, cts.Token));

        var receipt = await LoadAsync(service, id);
        Assert.Equal(ReceiptStatus.Pending, receipt.Status);
        Assert.Null(receipt.ErrorMessage);
    }

    [Fact]
    public async Task Extract_NoLines_MarksFailed()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        reader.Result = Extraction([]);
        var id = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");

        await service.ExtractAsync(id);

        var receipt = await LoadAsync(service, id);
        Assert.Equal(ReceiptStatus.Failed, receipt.Status);
        Assert.Equal(NoLineReadMessage, receipt.ErrorMessage);
        Assert.Empty(receipt.Lines);
    }

    [Fact]
    public async Task Extract_AfterFailure_RetryClearsErrorAndFillsReceipt()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        reader.Failure = new InvalidOperationException("Panne");
        var id = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");
        await service.ExtractAsync(id);

        reader.Failure = null;
        reader.Result = Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m), Line("Poivre", 1m, MeasureUnit.Kg, 9m)]);
        await service.ExtractAsync(id);

        var receipt = await LoadAsync(service, id);
        Assert.Equal(ReceiptStatus.Extracted, receipt.Status);
        Assert.Null(receipt.ErrorMessage);
        Assert.Equal(2, receipt.Lines.Count);
    }

    [Fact]
    public async Task Extract_Twice_ReplacesLinesInsteadOfDuplicatingThem()
    {
        var (options, restaurant, reader, service, _) = await ArrangeAsync();
        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m), Line("Poivre", 1m, MeasureUnit.Kg, 9m)]));

        reader.Result = Extraction([Line("Huile", 1m, MeasureUnit.L, 4m)]);
        await service.ExtractAsync(id);

        var receipt = await LoadAsync(service, id);
        Assert.Equal(["Huile"], receipt.Lines.Select(l => l.RawLabel));
        await using var db = OpenContext(options, restaurant);
        Assert.Equal(1, await db.ReceiptLines.CountAsync());
    }

    [Fact]
    public async Task Extract_AppliedReceipt_Refused()
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        await AddIngredientAsync(ingredients, "Sel");
        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m)]));
        await service.ApplyAsync(id);

        await AssertRefusedAsync(() => service.ExtractAsync(id), AlreadyAppliedMessage);
    }

    [Fact]
    public async Task Map_TtcWithoutRate_FlagsVatToCheck()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();

        var id = await ReadAsync(service, reader, Extraction(
            [
                Line("Farine", 1m, MeasureUnit.Kg, 10.60m, vatRate: 0.06m),
                Line("Sel", 1m, MeasureUnit.Kg, 5m, vatRate: null),
                Line("Lait", 1m, MeasureUnit.L, 2m, vatRate: 0m)
            ],
            includeVat: true));

        var receipt = await LoadAsync(service, id);
        Assert.True(receipt.AmountsIncludeVat);
        Assert.Equal(10m, receipt.Lines[0].LineTotalExclVat);       // 10,60 / 1,06
        Assert.False(receipt.Lines[0].VatToCheck);
        Assert.Equal(5m, receipt.Lines[1].LineTotalExclVat);        // taux inconnu : total repris tel quel
        Assert.True(receipt.Lines[1].VatToCheck);
        Assert.Equal(2m, receipt.Lines[2].LineTotalExclVat);        // taux 0 % : connu
        Assert.False(receipt.Lines[2].VatToCheck);
        Assert.Equal(10.60m, receipt.Lines[0].LineTotal);           // le total lu reste conservé
    }

    [Fact]
    public async Task Map_HtReceipt_NeverFlagsVatToCheck()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();

        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 5m, vatRate: null)], includeVat: false));

        var line = Assert.Single((await LoadAsync(service, id)).Lines);
        Assert.Equal(5m, line.LineTotalExclVat);
        Assert.False(line.VatToCheck);
    }

    // ── Lecture et liste ─────────────────────────────────────────────────────

    [Fact]
    public async Task List_NewestFirst_WithSummary()
    {
        var (options, restaurant, reader, service, _) = await ArrangeAsync();
        await AddSupplierAsync(options, restaurant, "Metro Cash & Carry");
        var older = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m), Line("Poivre", 1m, MeasureUnit.Kg, 9m)], supplier: "METRO", total: 10m));
        var failed = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");
        reader.Failure = new InvalidOperationException("Panne");
        await service.ExtractAsync(failed);

        var list = await service.ListAsync();

        Assert.Equal([failed, older], list.Select(s => s.Id));
        var olderSummary = list[1];
        Assert.Equal("Metro Cash & Carry", olderSummary.SupplierName);   // le fournisseur rapproché, pas le texte lu
        Assert.Equal(10m, olderSummary.Total);
        Assert.Equal(ReceiptStatus.Extracted, olderSummary.Status);
        Assert.Equal(2, olderSummary.LineCount);
        Assert.Equal(FutureDay, olderSummary.ReceiptDate);
        var failedSummary = list[0];
        Assert.Equal(ReceiptStatus.Failed, failedSummary.Status);
        Assert.Equal(0, failedSummary.LineCount);
        Assert.Equal(string.Empty, failedSummary.SupplierName);
        Assert.Null(failedSummary.ReceiptDate);
        Assert.Null(failedSummary.Total);
    }

    [Fact]
    public async Task List_UnmatchedSupplier_ShowsReadName()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m)], supplier: "Grossiste inconnu"));

        var summary = Assert.Single(await service.ListAsync());

        Assert.Equal("Grossiste inconnu", summary.SupplierName);
    }

    [Fact]
    public async Task Get_ReturnsLinesOrderedByPosition()
    {
        var (options, restaurant, reader, service, _) = await ArrangeAsync();
        var id = await ReadAsync(service, reader, Extraction(
            [Line("A", 1m, MeasureUnit.Kg, 1m), Line("B", 1m, MeasureUnit.Kg, 1m), Line("C", 1m, MeasureUnit.Kg, 1m)]));
        await using (var db = OpenContext(options, restaurant))
        {
            // Ordre d'insertion bouleversé : le tri doit venir de Position.
            var lines = await db.ReceiptLines.OrderBy(l => l.Id).ToListAsync();
            lines[0].Position = 2;
            lines[2].Position = 0;
            await db.SaveChangesAsync();
        }

        var receipt = await LoadAsync(service, id);

        Assert.Equal(["C", "B", "A"], receipt.Lines.Select(l => l.RawLabel));
    }

    [Fact]
    public async Task Get_UnknownId_ReturnsNull()
    {
        var (_, _, _, service, _) = await ArrangeAsync();

        Assert.Null(await service.GetAsync(12345));
        Assert.Null(await service.GetFileAsync(12345));
    }

    // ── Brouillon ────────────────────────────────────────────────────────────

    [Fact]
    public async Task SaveDraft_ForeignIngredient_Throws()
    {
        var (options, _, reader, service, ingredients) = await ArrangeAsync();
        var other = await TestData.CreateRestaurantAsync(options, "Autre");
        var foreign = await AddIngredientAsync(new IngredientService(new TestContextFactory(options, other)), "Farine");
        var local = await AddIngredientAsync(ingredients, "Sel");
        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m)]));
        var receipt = await LoadAsync(service, id);

        await AssertRefusedAsync(
            () => service.SaveDraftAsync(DraftOf(receipt, (_, input) => input with { IngredientId = foreign })),
            UnknownIngredientMessage);
        await AssertRefusedAsync(
            () => service.SaveDraftAsync(DraftOf(receipt, (_, input) => input with { IngredientId = 987654 })),
            UnknownIngredientMessage);

        Assert.Equal(local, Assert.Single((await LoadAsync(service, id)).Lines).IngredientId); // brouillon inchangé
    }

    [Fact]
    public async Task SaveDraft_ForeignSupplier_Throws()
    {
        var (options, _, reader, service, _) = await ArrangeAsync();
        var other = await TestData.CreateRestaurantAsync(options, "Autre");
        var foreign = await AddSupplierAsync(options, other, "Metro");
        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m)], supplier: "Inconnu"));
        var receipt = await LoadAsync(service, id);

        await AssertRefusedAsync(() => service.SaveDraftAsync(DraftOf(receipt) with { SupplierId = foreign }), UnknownSupplierMessage);

        Assert.Null((await LoadAsync(service, id)).SupplierId);
    }

    [Fact]
    public async Task SaveDraft_FailedReceiptWithManualLines_BecomesExtracted()
    {
        var (options, restaurant, reader, service, ingredients) = await ArrangeAsync();
        var metro = await AddSupplierAsync(options, restaurant, "Metro");
        var sel = await AddIngredientAsync(ingredients, "Sel");
        reader.Failure = new InvalidOperationException("Ticket illisible");
        var id = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");
        await service.ExtractAsync(id);

        await service.SaveDraftAsync(new ReceiptDraftInput(
            id, metro, FutureDay, "  F-9  ",
            [
                new ReceiptLineInput(null, "Sel fin", 5m, MeasureUnit.Kg, 7.50m, 0.06m, sel, false),
                new ReceiptLineInput(null, "Sacs", 1m, MeasureUnit.Piece, 3m, null, null, true)
            ]));

        var receipt = await LoadAsync(service, id);
        Assert.Equal(ReceiptStatus.Extracted, receipt.Status);
        Assert.Null(receipt.ErrorMessage);
        Assert.Equal(metro, receipt.SupplierId);
        Assert.Equal(FutureDay, receipt.ReceiptDate);
        Assert.Equal("F-9", receipt.InvoiceNumber);
        Assert.Equal(["Sel fin", "Sacs"], receipt.Lines.Select(l => l.RawLabel));
        Assert.Equal([0, 1], receipt.Lines.Select(l => l.Position));
        var first = receipt.Lines[0];
        Assert.Equal(5m, first.Quantity);
        Assert.Equal(MeasureUnit.Kg, first.Unit);
        Assert.Equal(7.50m, first.LineTotalExclVat);
        Assert.Equal(0.06m, first.VatRate);
        Assert.Equal(sel, first.IngredientId);
        Assert.Null(first.MatchSource);                  // choisi à la main
        Assert.False(first.IsIgnored);
        Assert.True(receipt.Lines[1].IsIgnored);
        Assert.All(receipt.Lines, l => Assert.Equal(restaurant, l.RestaurantId));
    }

    [Fact]
    public async Task SaveDraft_FailedReceiptWithoutLines_StaysFailed()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        reader.Failure = new InvalidOperationException("Ticket illisible");
        var id = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");
        await service.ExtractAsync(id);

        await service.SaveDraftAsync(new ReceiptDraftInput(id, null, null, null, []));

        var receipt = await LoadAsync(service, id);
        Assert.Equal(ReceiptStatus.Failed, receipt.Status);
        Assert.Equal("Ticket illisible", receipt.ErrorMessage);
    }

    [Fact]
    public async Task SaveDraft_Applied_Refused()
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        await AddIngredientAsync(ingredients, "Sel");
        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m)]));
        var applied = await LoadAsync(service, id);
        await service.ApplyAsync(id);

        await AssertRefusedAsync(() => service.SaveDraftAsync(DraftOf(applied)), AlreadyAppliedMessage);
    }

    [Fact]
    public async Task SaveDraft_ReplacesLinesKeepingOrderAndLineIds()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        var id = await ReadAsync(service, reader, Extraction(
            [
                Line("Sel", 1m, MeasureUnit.Kg, 1m, unitPrice: 1m),
                Line("Poivre", 2m, MeasureUnit.Kg, 18m, unitPrice: 9m),
                Line("Huile", 3m, MeasureUnit.L, 12m, unitPrice: 4m)
            ]));
        var before = await LoadAsync(service, id);
        var sel = before.Lines[0];
        var huile = before.Lines[2];

        await service.SaveDraftAsync(new ReceiptDraftInput(
            id, null, null, null,
            [
                new ReceiptLineInput(huile.Id, "Huile d'olive", 4m, MeasureUnit.L, 16m, null, null, false),
                new ReceiptLineInput(null, "Ajouté", 1m, MeasureUnit.Piece, 2m, null, null, false),
                new ReceiptLineInput(sel.Id, "Sel", 1m, MeasureUnit.Kg, 1m, null, null, true)
            ]));

        var after = await LoadAsync(service, id);
        Assert.Equal(["Huile d'olive", "Ajouté", "Sel"], after.Lines.Select(l => l.RawLabel));
        Assert.Equal([0, 1, 2], after.Lines.Select(l => l.Position));
        Assert.Equal(huile.Id, after.Lines[0].Id);           // les lignes conservées gardent leur identifiant
        Assert.Equal(sel.Id, after.Lines[2].Id);
        Assert.DoesNotContain(after.Lines, l => l.Id == before.Lines[1].Id); // « Poivre » a été retirée
        Assert.Equal(4m, after.Lines[0].UnitPrice);          // les valeurs lues par le lecteur restent
        Assert.Equal(12m, after.Lines[0].LineTotal);
        Assert.Equal(16m, after.Lines[0].LineTotalExclVat);  // le total HT, lui, est celui du brouillon
        Assert.Null(after.Lines[1].UnitPrice);
        Assert.Null(after.Lines[1].LineTotal);
        Assert.True(after.Lines[2].IsIgnored);
    }

    [Fact]
    public async Task SaveDraft_UnknownOrRepeatedLineId_Throws()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        var first = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m)]));
        var second = await ReadAsync(service, reader, Extraction([Line("Poivre", 1m, MeasureUnit.Kg, 1m)]));
        var firstLine = (await LoadAsync(service, first)).Lines[0];
        var secondLine = (await LoadAsync(service, second)).Lines[0];

        // Une ligne d'un autre ticket n'est pas adoptée.
        await AssertRefusedAsync(
            () => service.SaveDraftAsync(new ReceiptDraftInput(first, null, null, null,
                [new ReceiptLineInput(secondLine.Id, "Poivre", 1m, MeasureUnit.Kg, 1m, null, null, false)])),
            UnknownLineMessage);
        await AssertRefusedAsync(
            () => service.SaveDraftAsync(new ReceiptDraftInput(first, null, null, null,
            [
                new ReceiptLineInput(firstLine.Id, "Sel", 1m, MeasureUnit.Kg, 1m, null, null, false),
                new ReceiptLineInput(firstLine.Id, "Sel", 1m, MeasureUnit.Kg, 1m, null, null, false)
            ])),
            UnknownLineMessage);

        Assert.Equal(["Sel"], (await LoadAsync(service, first)).Lines.Select(l => l.RawLabel));
        Assert.Equal(["Poivre"], (await LoadAsync(service, second)).Lines.Select(l => l.RawLabel));
    }

    [Fact]
    public async Task SaveDraft_IngredientChangedByHand_ClearsMatchSource_UnchangedKeepsIt()
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        var farine = await AddIngredientAsync(ingredients, "Farine");
        var semoule = await AddIngredientAsync(ingredients, "Semoule");
        var id = await ReadAsync(service, reader, Extraction(
            [Line("FARINE T55", 1m, MeasureUnit.Kg, 1m), Line("SEMOULE", 1m, MeasureUnit.Kg, 1m), Line("Autre", 1m, MeasureUnit.Kg, 1m)]));
        var receipt = await LoadAsync(service, id);
        Assert.Equal([MatchSource.NameMatch, MatchSource.NameMatch, null], receipt.Lines.Select(l => l.MatchSource));

        await service.SaveDraftAsync(DraftOf(receipt, (line, input) => line.Position switch
        {
            0 => input,                                        // inchangée : garde sa source
            1 => input with { IngredientId = farine },         // autre ingrédient choisi à la main
            _ => input with { IngredientId = semoule }         // aucune proposition, choisi à la main
        }));

        var after = (await LoadAsync(service, id)).Lines;
        Assert.Equal([farine, farine, semoule], after.Select(l => l.IngredientId));
        Assert.Equal([MatchSource.NameMatch, null, null], after.Select(l => l.MatchSource));
    }

    [Fact]
    public async Task SaveDraft_VatToCheck_KeptWhenUntouched_ClearedWhenRateOrTotalEdited()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        var id = await ReadAsync(service, reader, Extraction(
            [
                Line("A", 1m, MeasureUnit.Kg, 5m),
                Line("B", 1m, MeasureUnit.Kg, 6m),
                Line("C", 1m, MeasureUnit.Kg, 7m)
            ],
            includeVat: true));
        var receipt = await LoadAsync(service, id);
        Assert.All(receipt.Lines, l => Assert.True(l.VatToCheck));

        await service.SaveDraftAsync(DraftOf(receipt, (line, input) => line.Position switch
        {
            0 => input,
            1 => input with { VatRate = 0.06m },
            _ => input with { LineTotalExclVat = 6m }
        }));

        var after = (await LoadAsync(service, id)).Lines;
        Assert.Equal([true, false, false], after.Select(l => l.VatToCheck));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(21)]
    public async Task SaveDraft_VatRateOutOfRange_Throws(double rate)
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m)]));
        var receipt = await LoadAsync(service, id);

        await AssertRefusedAsync(
            () => service.SaveDraftAsync(DraftOf(receipt, (_, input) => input with { VatRate = (decimal)rate })),
            "Le taux de TVA doit être compris entre 0 et 100 %");
    }

    [Fact]
    public async Task SaveDraft_NormalizesDateToUtc()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m)]));
        var receipt = await LoadAsync(service, id);

        // Une date saisie sans fuseau (sélecteur de date) est prise comme une date UTC.
        await service.SaveDraftAsync(DraftOf(receipt) with { ReceiptDate = new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Unspecified) });

        var after = await LoadAsync(service, id);
        Assert.Equal(new DateTime(2026, 5, 20, 0, 0, 0, DateTimeKind.Utc), after.ReceiptDate);
        Assert.Equal(DateTimeKind.Utc, after.ReceiptDate!.Value.Kind);
    }

    // ── Application ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Apply_UpdatesPricesHistoryAndAliases()
    {
        var (options, restaurant, reader, service, ingredients) = await ArrangeAsync();
        var metro = await AddSupplierAsync(options, restaurant, "Metro");
        var farine = await AddIngredientAsync(ingredients, "Farine", 0.001m);
        var id = await ReadAsync(service, reader, Extraction(
            [Line("Farine T55 25kg", 25m, MeasureUnit.Kg, 30m)], supplier: "METRO"));

        var before = DateTime.UtcNow;
        var result = await service.ApplyAsync(id);
        var after = DateTime.UtcNow;

        var price = Assert.Single(result.Prices);
        Assert.Equal(farine, price.IngredientId);
        Assert.Equal("Farine", price.IngredientName);
        Assert.Equal(BaseUnit.Gram, price.BaseUnit);
        Assert.Equal(0.001m, price.OldPricePerBaseUnit);
        Assert.Equal(0.0012m, price.NewPricePerBaseUnit);          // 30 € / 25 000 g
        Assert.True(price.ReplacedCurrent);

        Assert.Equal(0.0012m, (await ingredients.GetAsync(farine))!.CurrentPricePerBaseUnit);
        var history = await ingredients.GetPriceHistoryAsync(farine);
        Assert.Equal(2, history.Count);
        var entry = history[1];
        Assert.Equal(PriceSource.Receipt, entry.Source);
        Assert.Equal(metro, entry.SupplierId);
        Assert.Equal(FutureDay, entry.Date);
        Assert.Equal(0.0012m, entry.PricePerBaseUnit);

        await using var db = OpenContext(options, restaurant);
        var alias = Assert.Single(await db.SupplierAliases.ToListAsync());
        Assert.Equal(metro, alias.SupplierId);
        Assert.Equal("FARINE T55 25KG", alias.NormalizedLabel);
        Assert.Equal(farine, alias.IngredientId);
        Assert.Equal(restaurant, alias.RestaurantId);

        var receipt = await LoadAsync(service, id);
        Assert.Equal(ReceiptStatus.Applied, receipt.Status);
        Assert.NotNull(receipt.AppliedAt);
        Assert.Equal(DateTimeKind.Utc, receipt.AppliedAt.Value.Kind);
        Assert.InRange(receipt.AppliedAt.Value, before, after);
        Assert.Equal(0.0012m, Assert.Single(receipt.Lines).AppliedPricePerBaseUnit);
    }

    [Fact]
    public async Task Apply_TwoLinesSameIngredient_UsesWeightedAverage()
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        var beurre = await AddIngredientAsync(ingredients, "Beurre", 0.001m);
        var id = await ReadAsync(service, reader, Extraction(
        [
            Line("Beurre doux", 1m, MeasureUnit.Kg, 10m),
            Line("Beurre extra fin", 3m, MeasureUnit.Kg, 24m)
        ]));

        var result = await service.ApplyAsync(id);

        var price = Assert.Single(result.Prices);
        Assert.Equal(0.0085m, price.NewPricePerBaseUnit);          // (10 + 24) € / (1 000 + 3 000) g, pas la moyenne des prix
        Assert.Equal(0.0085m, (await ingredients.GetAsync(beurre))!.CurrentPricePerBaseUnit);
        var receiptEntries = (await ingredients.GetPriceHistoryAsync(beurre)).Where(p => p.Source == PriceSource.Receipt).ToList();
        Assert.Equal(0.0085m, Assert.Single(receiptEntries).PricePerBaseUnit);   // une seule entrée d'historique par ingrédient
        var lines = (await LoadAsync(service, id)).Lines;
        Assert.All(lines, l => Assert.Equal(0.0085m, l.AppliedPricePerBaseUnit));
    }

    [Fact]
    public async Task Apply_SeveralIngredients_ReportsOneUnitPriceEachInTicketOrder()
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        var sel = await AddIngredientAsync(ingredients, "Sel", 0.002m);
        var huile = await AddIngredientAsync(ingredients, "Huile", baseUnit: BaseUnit.Milliliter);
        var oeuf = await AddIngredientAsync(ingredients, "Oeuf", baseUnit: BaseUnit.Piece);
        var id = await ReadAsync(service, reader, Extraction(
        [
            Line("Huile", 2m, MeasureUnit.L, 8m),
            Line("Oeuf", 30m, MeasureUnit.Piece, 9m),
            Line("Sel fin", 500m, MeasureUnit.G, 1m)
        ]));

        var result = await service.ApplyAsync(id);

        Assert.Equal([huile, oeuf, sel], result.Prices.Select(p => p.IngredientId));
        Assert.Equal([0.004m, 0.3m, 0.002m], result.Prices.Select(p => p.NewPricePerBaseUnit));
        Assert.Equal([null, null, 0.002m], result.Prices.Select(p => p.OldPricePerBaseUnit));
        Assert.Equal([BaseUnit.Milliliter, BaseUnit.Piece, BaseUnit.Gram], result.Prices.Select(p => p.BaseUnit));
    }

    [Fact]
    public async Task Apply_LateReceipt_OnlyEnrichesHistory()
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        var sel = await AddIngredientAsync(ingredients, "Sel", 0.002m); // entrée manuelle datée d'aujourd'hui
        var id = await ReadAsync(service, reader, Extraction(
            [Line("Sel", 1m, MeasureUnit.Kg, 3m)], date: new DateOnly(2000, 1, 1)));

        var result = await service.ApplyAsync(id);

        var price = Assert.Single(result.Prices);
        Assert.False(price.ReplacedCurrent);
        Assert.Equal(0.002m, price.OldPricePerBaseUnit);
        Assert.Equal(0.003m, price.NewPricePerBaseUnit);
        Assert.Equal(0.002m, (await ingredients.GetAsync(sel))!.CurrentPricePerBaseUnit);
        var history = await ingredients.GetPriceHistoryAsync(sel);
        Assert.Equal(2, history.Count);
        Assert.Equal(new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc), history[0].Date); // trié par date : le ticket en retard passe devant
        Assert.Equal(PriceSource.Receipt, history[0].Source);
        Assert.Equal(ReceiptStatus.Applied, (await LoadAsync(service, id)).Status);
    }

    [Fact]
    public async Task Apply_NoReceiptDate_UsesCreationDate()
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        var sel = await AddIngredientAsync(ingredients, "Sel");
        reader.Result = new ReceiptExtraction("METRO", null, null, false, null, [Line("Sel", 1m, MeasureUnit.Kg, 3m)]);
        var id = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");
        await service.ExtractAsync(id);
        var createdAt = (await LoadAsync(service, id)).CreatedAt;

        await service.ApplyAsync(id);

        var entry = Assert.Single(await ingredients.GetPriceHistoryAsync(sel));
        Assert.Equal(createdAt, entry.Date);
        Assert.Equal(DateTimeKind.Utc, entry.Date.Kind);
    }

    [Fact]
    public async Task Apply_IngredientWithoutPrice_HasNoOldPrice()
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        await AddIngredientAsync(ingredients, "Sel", pricePerBaseUnit: null);
        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 3m)]));

        var price = Assert.Single((await service.ApplyAsync(id)).Prices);

        Assert.Null(price.OldPricePerBaseUnit);
        Assert.True(price.ReplacedCurrent);
    }

    [Fact]
    public async Task Apply_ExistingAlias_IsUpdatedNotDuplicated()
    {
        var (options, restaurant, reader, service, ingredients) = await ArrangeAsync();
        var metro = await AddSupplierAsync(options, restaurant, "Metro");
        var farine = await AddIngredientAsync(ingredients, "Farine");
        var semoule = await AddIngredientAsync(ingredients, "Semoule");
        await using (var db = OpenContext(options, restaurant))
        {
            db.SupplierAliases.Add(new SupplierAlias { SupplierId = metro, NormalizedLabel = "BLE MOULU", IngredientId = semoule });
            await db.SaveChangesAsync();
        }

        var id = await ReadAsync(service, reader, Extraction([Line("Blé moulu", 1m, MeasureUnit.Kg, 3m)], supplier: "METRO"));
        Assert.Equal(semoule, (await LoadAsync(service, id)).Lines[0].IngredientId); // proposé par l'alias
        var receipt = await LoadAsync(service, id);
        await service.SaveDraftAsync(DraftOf(receipt, (_, input) => input with { IngredientId = farine })); // le chef corrige

        await service.ApplyAsync(id);

        await using var check = OpenContext(options, restaurant);
        var alias = Assert.Single(await check.SupplierAliases.ToListAsync());
        Assert.Equal(farine, alias.IngredientId);
        Assert.Equal(metro, alias.SupplierId);
    }

    [Fact]
    public async Task Apply_OtherSupplierAliasWithSameLabel_IsLeftAlone()
    {
        var (options, restaurant, reader, service, ingredients) = await ArrangeAsync();
        var metro = await AddSupplierAsync(options, restaurant, "Metro");
        var promocash = await AddSupplierAsync(options, restaurant, "Promocash");
        var farine = await AddIngredientAsync(ingredients, "Farine");
        var semoule = await AddIngredientAsync(ingredients, "Semoule");
        await using (var db = OpenContext(options, restaurant))
        {
            db.SupplierAliases.Add(new SupplierAlias { SupplierId = promocash, NormalizedLabel = "FARINE", IngredientId = semoule });
            db.SupplierAliases.Add(new SupplierAlias { SupplierId = null, NormalizedLabel = "FARINE", IngredientId = semoule });
            await db.SaveChangesAsync();
        }
        var id = await ReadAsync(service, reader, Extraction([Line("Farine", 1m, MeasureUnit.Kg, 3m)], supplier: "METRO"));
        var receipt = await LoadAsync(service, id);
        Assert.Equal(semoule, receipt.Lines[0].IngredientId); // l'alias global propose « Semoule »
        await service.SaveDraftAsync(DraftOf(receipt, (_, input) => input with { IngredientId = farine }));

        await service.ApplyAsync(id);

        await using var check = OpenContext(options, restaurant);
        var aliases = await check.SupplierAliases.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(3, aliases.Count);
        Assert.Equal(semoule, aliases.Single(a => a.SupplierId == promocash).IngredientId);
        Assert.Equal(semoule, aliases.Single(a => a.SupplierId == null).IngredientId);
        Assert.Equal(farine, aliases.Single(a => a.SupplierId == metro).IngredientId);
    }

    [Fact]
    public async Task Apply_ReceiptWithoutSupplier_MemorisesAliasForAllSuppliers()
    {
        var (options, restaurant, reader, service, ingredients) = await ArrangeAsync();
        var farine = await AddIngredientAsync(ingredients, "Farine");
        var id = await ReadAsync(service, reader, Extraction([Line("Farine", 1m, MeasureUnit.Kg, 3m)], supplier: "Inconnu"));

        await service.ApplyAsync(id);

        await using var db = OpenContext(options, restaurant);
        var alias = Assert.Single(await db.SupplierAliases.ToListAsync());
        Assert.Null(alias.SupplierId);
        Assert.Equal("FARINE", alias.NormalizedLabel);
        Assert.Equal(farine, alias.IngredientId);
    }

    [Fact]
    public async Task Apply_SameLabelOnTwoLines_CreatesOneAlias()
    {
        var (options, restaurant, reader, service, ingredients) = await ArrangeAsync();
        await AddIngredientAsync(ingredients, "Tomate");
        var id = await ReadAsync(service, reader, Extraction(
            [Line("Tomate", 1m, MeasureUnit.Kg, 3m), Line("TOMATE", 2m, MeasureUnit.Kg, 5m)], supplier: "Inconnu"));

        await service.ApplyAsync(id);

        await using var db = OpenContext(options, restaurant);
        Assert.Equal(1, await db.SupplierAliases.CountAsync());
    }

    [Fact]
    public async Task Apply_LineWithoutIngredient_RefusedWithLineNumber()
    {
        var (options, restaurant, reader, service, ingredients) = await ArrangeAsync();
        var sel = await AddIngredientAsync(ingredients, "Sel", 0.002m);
        var id = await ReadAsync(service, reader, Extraction(
            [Line("Sel", 1m, MeasureUnit.Kg, 3m), Line("Sacs poubelle", 1m, MeasureUnit.Piece, 4m)]));

        await AssertRefusedAsync(() => service.ApplyAsync(id), "Ligne 2 « Sacs poubelle » : ingrédient à choisir");

        // Rien n'a été écrit : prix, historique, alias, statut.
        Assert.Equal(0.002m, (await ingredients.GetAsync(sel))!.CurrentPricePerBaseUnit);
        Assert.Single(await ingredients.GetPriceHistoryAsync(sel));
        var receipt = await LoadAsync(service, id);
        Assert.Equal(ReceiptStatus.Extracted, receipt.Status);
        Assert.Null(receipt.AppliedAt);
        Assert.All(receipt.Lines, l => Assert.Null(l.AppliedPricePerBaseUnit));
        await using var db = OpenContext(options, restaurant);
        Assert.Empty(await db.SupplierAliases.ToListAsync());
    }

    [Theory]
    [InlineData(null, null, 3.0, "quantité ou unité manquante")]
    [InlineData(2.0, null, 3.0, "quantité ou unité manquante")]
    [InlineData(0.0, MeasureUnit.Kg, 3.0, "quantité ou unité manquante")]
    [InlineData(1.0, MeasureUnit.Ml, 3.0, "unité incompatible avec l'ingrédient")]
    [InlineData(1.0, MeasureUnit.Kg, null, "montant invalide")]
    [InlineData(1.0, MeasureUnit.Kg, 0.0, "montant invalide")]
    [InlineData(1.0, MeasureUnit.Kg, -2.0, "montant invalide")]
    public async Task Apply_InvalidLine_RefusedWithReason(double? quantity, MeasureUnit? unit, double? total, string reason)
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        await AddIngredientAsync(ingredients, "Farine");
        var id = await ReadAsync(service, reader, Extraction(
            [Line("Farine", (decimal?)quantity, unit, (decimal?)total)]));

        await AssertRefusedAsync(() => service.ApplyAsync(id), $"Ligne 1 « Farine » : {reason}");
    }

    [Fact]
    public async Task Apply_ReportsTheFirstBlockingLineByPosition()
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        await AddIngredientAsync(ingredients, "Sel");
        await AddIngredientAsync(ingredients, "Farine");
        var id = await ReadAsync(service, reader, Extraction(
        [
            Line("Sel", 1m, MeasureUnit.Kg, 3m),
            Line("Farine", null, null, 3m),
            Line("Sacs", 1m, MeasureUnit.Piece, 3m)
        ]));

        await AssertRefusedAsync(() => service.ApplyAsync(id), "Ligne 2 « Farine » : quantité ou unité manquante");
    }

    [Fact]
    public async Task Apply_IgnoredLinesSkipped()
    {
        var (options, restaurant, reader, service, ingredients) = await ArrangeAsync();
        var sel = await AddIngredientAsync(ingredients, "Sel");
        var id = await ReadAsync(service, reader, Extraction(
        [
            Line("Sel", 1m, MeasureUnit.Kg, 3m),
            Line("Consigne caisse", null, null, null),
            Line("Sel gris", 1m, MeasureUnit.Kg, 99m)
        ]));
        var receipt = await LoadAsync(service, id);
        await service.SaveDraftAsync(DraftOf(receipt, (line, input) => line.Position == 0 ? input : input with { IsIgnored = true }));

        var result = await service.ApplyAsync(id);

        var price = Assert.Single(result.Prices);
        Assert.Equal(sel, price.IngredientId);
        Assert.Equal(0.003m, price.NewPricePerBaseUnit);               // la ligne ignorée à 99 € ne compte pas
        var lines = (await LoadAsync(service, id)).Lines;
        Assert.Equal([0.003m, null, null], lines.Select(l => l.AppliedPricePerBaseUnit));
        await using var db = OpenContext(options, restaurant);
        var alias = Assert.Single(await db.SupplierAliases.ToListAsync());   // aucun alias pour une ligne ignorée
        Assert.Equal("SEL", alias.NormalizedLabel);
    }

    [Fact]
    public async Task Apply_Twice_Refused()
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        var sel = await AddIngredientAsync(ingredients, "Sel");
        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 3m)]));
        await service.ApplyAsync(id);

        await AssertRefusedAsync(() => service.ApplyAsync(id), AlreadyAppliedMessage);

        Assert.Single(await ingredients.GetPriceHistoryAsync(sel));    // pas de seconde entrée d'historique
    }

    [Fact]
    public async Task Apply_NotExtracted_Refused()
    {
        var (_, _, reader, service, _) = await ArrangeAsync();
        var pending = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");
        reader.Failure = new InvalidOperationException("Panne");
        var failed = await service.CreateAsync(SomeBytes, "ticket.jpg", "image/jpeg");
        await service.ExtractAsync(failed);

        await AssertRefusedAsync(() => service.ApplyAsync(pending), NotReadyMessage);
        await AssertRefusedAsync(() => service.ApplyAsync(failed), NotReadyMessage);
    }

    [Fact]
    public async Task Apply_ChangesDishCost()
    {
        var (options, restaurant, reader, service, ingredients) = await ArrangeAsync();
        var farine = await AddIngredientAsync(ingredients, "Farine", 0.001m);
        var recipes = new RecipeService(new TestContextFactory(options, restaurant));
        var dish = await recipes.SaveAsync(new RecipeInput(
            null, "Pain", RecipeKind.Dish, "Plats", 10m, 0.06m, null, null, null,
            [new RecipeLineInput(farine, null, 200m, MeasureUnit.G)]));
        var before = (await recipes.LoadBookAsync()).Cost(dish.Id);
        var id = await ReadAsync(service, reader, Extraction([Line("Farine T55 25kg", 25m, MeasureUnit.Kg, 30m)]));

        await service.ApplyAsync(id);

        var after = (await recipes.LoadBookAsync()).Cost(dish.Id);
        Assert.Equal(0.2m, before.TotalCost);                          // 200 g × 0,001
        Assert.Equal(0.24m, after.TotalCost);                          // 200 g × 0,0012
    }

    // ── Suppression ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_RemovesReceiptLinesAndFile()
    {
        var (options, restaurant, reader, service, _) = await ArrangeAsync();
        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m), Line("Poivre", 1m, MeasureUnit.Kg, 1m)]));
        var kept = await ReadAsync(service, reader, Extraction([Line("Huile", 1m, MeasureUnit.L, 1m)]));

        await service.DeleteAsync(id);

        Assert.Null(await service.GetAsync(id));
        Assert.Null(await service.GetFileAsync(id));
        Assert.Equal([kept], (await service.ListAsync()).Select(s => s.Id));
        await using var db = OpenContext(options, restaurant);
        Assert.Equal(1, await db.ReceiptLines.CountAsync());
        Assert.Equal(1, await db.ReceiptFiles.CountAsync());
    }

    [Fact]
    public async Task Delete_Applied_Refused()
    {
        var (_, _, reader, service, ingredients) = await ArrangeAsync();
        await AddIngredientAsync(ingredients, "Sel");
        var id = await ReadAsync(service, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 1m)]));
        await service.ApplyAsync(id);

        await AssertRefusedAsync(() => service.DeleteAsync(id), "Un ticket appliqué ne peut pas être supprimé");

        Assert.NotNull(await service.GetAsync(id));
    }

    // ── Multi-restaurant ─────────────────────────────────────────────────────

    [Fact]
    public async Task OtherRestaurant_CannotSeeOrApply()
    {
        var (options, restaurantA, reader, serviceA, ingredientsA) = await ArrangeAsync("Restaurant A");
        var restaurantB = await TestData.CreateRestaurantAsync(options, "Restaurant B");
        var serviceB = ServiceFor(options, restaurantB, reader);
        var sel = await AddIngredientAsync(ingredientsA, "Sel", 0.002m);
        var id = await ReadAsync(serviceA, reader, Extraction([Line("Sel", 1m, MeasureUnit.Kg, 3m)]));
        var receipt = await LoadAsync(serviceA, id);

        Assert.Null(await serviceB.GetAsync(id));
        Assert.Null(await serviceB.GetFileAsync(id));
        Assert.Empty(await serviceB.ListAsync());
        await AssertRefusedAsync(() => serviceB.ExtractAsync(id), UnknownReceiptMessage);
        await AssertRefusedAsync(() => serviceB.SaveDraftAsync(DraftOf(receipt)), UnknownReceiptMessage);
        await AssertRefusedAsync(() => serviceB.ApplyAsync(id), UnknownReceiptMessage);
        await AssertRefusedAsync(() => serviceB.DeleteAsync(id), UnknownReceiptMessage);

        // Le ticket du restaurant A est intact et ses prix n'ont pas bougé.
        var intact = await LoadAsync(serviceA, id);
        Assert.Equal(ReceiptStatus.Extracted, intact.Status);
        Assert.Equal(0.002m, (await ingredientsA.GetAsync(sel))!.CurrentPricePerBaseUnit);
        Assert.Equal(restaurantA, intact.RestaurantId);
    }

    [Fact]
    public async Task UnknownReceipt_Refused()
    {
        var (_, _, _, service, _) = await ArrangeAsync();

        await AssertRefusedAsync(() => service.ExtractAsync(424242), UnknownReceiptMessage);
        await AssertRefusedAsync(
            () => service.SaveDraftAsync(new ReceiptDraftInput(424242, null, null, null, [])), UnknownReceiptMessage);
        await AssertRefusedAsync(() => service.ApplyAsync(424242), UnknownReceiptMessage);
        await AssertRefusedAsync(() => service.DeleteAsync(424242), UnknownReceiptMessage);
    }
}
