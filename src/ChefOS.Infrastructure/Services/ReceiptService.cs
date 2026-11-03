using ChefOS.Core.Common;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Tickets fournisseurs du restaurant courant : téléversement, lecture par le <see cref="IReceiptReader"/> (IA ou démo),
/// brouillon corrigé par le chef, puis application des prix aux ingrédients et mémorisation des libellés.
/// Les clés étrangères de la base sont à colonne unique : la vérification qu'un fournisseur, un ingrédient, une ligne ou un
/// ticket appartient bien au restaurant courant se fait ici, via le contexte filtré par restaurant (qui estampille aussi les ajouts).
/// Les octets du fichier ne sont lus que par <see cref="GetFileAsync"/> : ni les listes ni <see cref="GetAsync"/> ne les chargent.
/// </summary>
public class ReceiptService(IDbContextFactory<ChefOsDbContext> factory, IReceiptReader reader, IngredientService ingredients)
{
    private const int MaxFileBytes = 10 * 1024 * 1024;

    private const string UnsupportedTypeMessage = "Format non pris en charge (JPEG, PNG, WebP ou PDF)";
    private const string TooLargeMessage = "Fichier trop volumineux (10 Mo maximum)";
    private const string UnknownReceiptMessage = "Ticket introuvable";
    private const string UnknownSupplierMessage = "Fournisseur introuvable";
    private const string UnknownIngredientMessage = "Ingrédient introuvable";
    private const string UnknownLineMessage = "Ligne introuvable";
    private const string MissingFileMessage = "Fichier du ticket introuvable";
    private const string AlreadyAppliedMessage = "Ce ticket a déjà été appliqué";
    private const string NotReadyMessage = "Ce ticket n'est pas prêt à être appliqué";
    private const string AppliedCannotBeDeletedMessage = "Un ticket appliqué ne peut pas être supprimé";
    private const string NoLineReadMessage = "Aucune ligne n'a été lue sur ce ticket";
    private const string VatRateMessage = "Le taux de TVA doit être compris entre 0 et 100 %";

    // Échelles des colonnes numeric : arrondir avant de valider/comparer évite qu'une valeur change à l'enregistrement.
    private const int QuantityScale = 4;
    private const int AmountScale = 2;
    private const int VatRateScale = 4;

    // Échelle du prix par unité de base (numeric(18,6)), la même qu'IngredientService : le prix annoncé est celui qui est enregistré.
    private const int PriceScale = 6;

    private static readonly HashSet<string> AcceptedContentTypes = new(StringComparer.Ordinal)
    {
        "image/jpeg", "image/png", "image/webp", "application/pdf"
    };

    /// <summary>Enregistre le fichier et crée un ticket « en attente » ; renvoie son identifiant. La lecture se lance avec <see cref="ExtractAsync"/>.</summary>
    public async Task<int> CreateAsync(byte[] content, string fileName, string contentType)
    {
        var type = contentType.Trim().ToLowerInvariant();
        if (!AcceptedContentTypes.Contains(type))
            throw new DomainValidationException(UnsupportedTypeMessage);
        if (content.Length > MaxFileBytes)
            throw new DomainValidationException(TooLargeMessage);

        await using var db = await factory.CreateDbContextAsync();

        var receipt = new Receipt { Status = ReceiptStatus.Pending, CreatedAt = DateTime.UtcNow };
        db.Receipts.Add(receipt);

        // L'identifiant du ticket n'existe qu'après le premier enregistrement : une transaction évite un ticket sans fichier.
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.SaveChangesAsync();

        db.ReceiptFiles.Add(new ReceiptFile
        {
            ReceiptId = receipt.Id,
            FileName = string.IsNullOrWhiteSpace(fileName) ? "ticket" : fileName.Trim(),
            ContentType = type,
            Data = content
        });
        await db.SaveChangesAsync();

        await transaction.CommitAsync();
        return receipt.Id;
    }

    /// <summary>
    /// Fait lire le fichier du ticket puis remplit l'en-tête et les lignes (propositions d'ingrédients comprises) ; un ticket déjà lu est relu
    /// et ses lignes remplacées. Une lecture qui échoue ou ne renvoie aucune ligne met le ticket en <see cref="ReceiptStatus.Failed"/> avec
    /// son message, sans lever d'exception : le chef peut compléter le ticket à la main. Seule l'annulation demandée par l'appelant
    /// (<paramref name="ct"/>) se propage, et laisse le ticket tel quel.
    /// </summary>
    public async Task ExtractAsync(int receiptId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync();

        var receipt = await RequireReceiptAsync(db, receiptId, includeLines: true, ct);
        if (receipt.Status == ReceiptStatus.Applied)
            throw new DomainValidationException(AlreadyAppliedMessage);

        var file = await db.ReceiptFiles.AsNoTracking()
            .Where(f => f.ReceiptId == receiptId)
            .Select(f => new { f.Data, f.ContentType })
            .FirstOrDefaultAsync(ct)
            ?? throw new DomainValidationException(MissingFileMessage);

        ReceiptExtraction extraction;
        try
        {
            extraction = await reader.ReadAsync(file.Data, file.ContentType, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Y compris un délai dépassé du lecteur (TaskCanceledException sans annulation de l'appelant).
            await MarkFailedAsync(db, receipt, ex.Message, ct);
            return;
        }

        if (extraction.Lines.Count == 0)
        {
            await MarkFailedAsync(db, receipt, NoLineReadMessage, ct);
            return;
        }

        var suppliers = await db.Suppliers.AsNoTracking().ToListAsync(ct);
        var aliases = await db.SupplierAliases.AsNoTracking().ToListAsync(ct);
        var allIngredients = await db.Ingredients.AsNoTracking().ToListAsync(ct); // archivés compris : le matcher les écarte lui-même

        var supplierName = extraction.SupplierName?.Trim() ?? string.Empty;
        var supplierId = MatchSupplier(supplierName, suppliers);

        receipt.SupplierNameRaw = supplierName;
        receipt.SupplierId = supplierId;
        receipt.ReceiptDate = extraction.Date is { } day
            ? DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
            : null;
        receipt.InvoiceNumber = NullIfBlank(extraction.InvoiceNumber);
        receipt.AmountsIncludeVat = extraction.AmountsIncludeVat;
        receipt.Total = extraction.Total;
        receipt.ErrorMessage = null;
        receipt.Status = ReceiptStatus.Extracted;

        // Une relecture remplace les lignes ; tout part dans le même enregistrement que l'en-tête.
        db.ReceiptLines.RemoveRange(receipt.Lines.ToList());

        var position = 0;
        foreach (var read in extraction.Lines)
        {
            var label = (read.Label ?? string.Empty).Trim();
            var exclVat = ReceiptLinePricing.ExclVat(read.LineTotal, read.VatRate, extraction.AmountsIncludeVat, out var vatToCheck);
            var suggestion = IngredientMatcher.Suggest(label, supplierId, aliases, allIngredients);

            db.ReceiptLines.Add(new ReceiptLine
            {
                ReceiptId = receipt.Id,
                Position = position++,
                RawLabel = label,
                Quantity = read.Quantity,
                Unit = read.Unit,
                UnitPrice = read.UnitPrice,
                LineTotal = read.LineTotal,
                VatRate = read.VatRate,
                LineTotalExclVat = exclVat,
                VatToCheck = vatToCheck,
                IngredientId = suggestion?.IngredientId,
                MatchSource = suggestion?.Source
            });
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Tickets du restaurant courant, du plus récent au plus ancien, sans leurs lignes ni leur fichier.</summary>
    public async Task<IReadOnlyList<ReceiptSummary>> ListAsync()
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.Receipts.AsNoTracking()
            .OrderByDescending(r => r.CreatedAt)
            .ThenByDescending(r => r.Id)
            .Select(r => new ReceiptSummary(
                r.Id,
                r.CreatedAt,
                r.ReceiptDate,
                db.Suppliers.Where(s => s.Id == r.SupplierId).Select(s => s.Name).FirstOrDefault() ?? r.SupplierNameRaw,
                r.Total,
                r.Status,
                r.Lines.Count))
            .ToListAsync();
    }

    /// <summary>Le ticket et ses lignes (triées par <see cref="ReceiptLine.Position"/>), sans les octets du fichier ; null s'il n'existe pas dans le restaurant courant.</summary>
    public async Task<Receipt?> GetAsync(int id)
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.Receipts.AsNoTracking()
            .Include(r => r.Lines.OrderBy(l => l.Position).ThenBy(l => l.Id))
            .FirstOrDefaultAsync(r => r.Id == id);
    }

    /// <summary>Le fichier téléversé, octets compris ; null si le ticket n'existe pas dans le restaurant courant.</summary>
    public async Task<ReceiptFile?> GetFileAsync(int receiptId)
    {
        await using var db = await factory.CreateDbContextAsync();

        return await db.ReceiptFiles.AsNoTracking().FirstOrDefaultAsync(f => f.ReceiptId == receiptId);
    }

    /// <summary>
    /// Enregistre le brouillon du chef : en-tête et lignes, dans l'ordre reçu. Une ligne qui porte l'identifiant d'une ligne du ticket la met
    /// à jour (elle garde son identifiant et ce que le lecteur y avait lu) ; sans identifiant, c'est une ligne ajoutée à la main ; les lignes
    /// du ticket absentes du brouillon sont supprimées. Un ingrédient différent de celui enregistré est un choix manuel (origine effacée) ;
    /// la mention « TVA à vérifier » ne reste que si ni le taux ni le total de la ligne n'ont été modifiés. Un ticket en échec complété avec
    /// au moins une ligne passe à <see cref="ReceiptStatus.Extracted"/>. Tout est enregistré d'un coup ou pas du tout.
    /// </summary>
    public async Task SaveDraftAsync(ReceiptDraftInput input)
    {
        await using var db = await factory.CreateDbContextAsync();

        var receipt = await RequireReceiptAsync(db, input.ReceiptId, includeLines: true);
        if (receipt.Status == ReceiptStatus.Applied)
            throw new DomainValidationException(AlreadyAppliedMessage);

        var lines = NormalizeLines(input.Lines);
        await RequireSupplierAsync(db, input.SupplierId);
        await RequireIngredientsAsync(db, lines.Select(l => l.IngredientId).OfType<int>().Distinct().ToList());

        // Chaque ligne du ticket ne peut être reprise qu'une fois : Remove consomme l'entrée, ce qui reste est supprimé.
        var remaining = receipt.Lines.ToDictionary(l => l.Id);
        for (var position = 0; position < lines.Count; position++)
        {
            var line = lines[position];

            ReceiptLine entity;
            if (line.Id is { } lineId)
            {
                if (!remaining.Remove(lineId, out var existing))
                    throw new DomainValidationException(UnknownLineMessage);
                entity = existing;
            }
            else
            {
                entity = new ReceiptLine { ReceiptId = receipt.Id };
                db.ReceiptLines.Add(entity);
            }

            var ingredientChanged = entity.IngredientId != line.IngredientId;
            var vatStillToCheck = entity.VatToCheck
                                  && entity.VatRate == line.VatRate
                                  && entity.LineTotalExclVat == line.LineTotalExclVat;

            entity.Position = position;
            entity.RawLabel = line.RawLabel;
            entity.Quantity = line.Quantity;
            entity.Unit = line.Unit;
            entity.LineTotalExclVat = line.LineTotalExclVat;
            entity.VatRate = line.VatRate;
            entity.VatToCheck = vatStillToCheck;
            entity.IngredientId = line.IngredientId;
            if (ingredientChanged)
                entity.MatchSource = null;
            entity.IsIgnored = line.IsIgnored;
        }

        db.ReceiptLines.RemoveRange(remaining.Values);

        receipt.SupplierId = input.SupplierId;
        receipt.ReceiptDate = ToUtc(input.ReceiptDate);
        receipt.InvoiceNumber = NullIfBlank(input.InvoiceNumber);
        if (receipt.Status == ReceiptStatus.Failed && lines.Count > 0)
        {
            receipt.Status = ReceiptStatus.Extracted;
            receipt.ErrorMessage = null;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Applique le ticket : chaque ligne non ignorée est chiffrée (<see cref="ReceiptLinePricing.Compute"/>) ; au premier problème, par ordre de ligne,
    /// rien n'est écrit et le message nomme la ligne. Les lignes d'un même ingrédient donnent un seul prix, la moyenne pondérée par les quantités
    /// (Σ totaux HT ÷ Σ quantités de base), enregistré comme prix de l'ingrédient (source Receipt, fournisseur et date du ticket, ou date de création
    /// sans date lue) et repris dans <see cref="ReceiptLine.AppliedPricePerBaseUnit"/> de chacune de ces lignes. Les libellés sont mémorisés par
    /// fournisseur (créés ou mis à jour). Les prix sont écrits d'abord, un ingrédient après l'autre (<see cref="IngredientService.SetPriceAsync"/>
    /// ouvre son propre contexte) ; le ticket n'est marqué appliqué qu'à la fin, avec les lignes et les alias dans un seul enregistrement.
    /// Si une écriture échoue (base indisponible…) au milieu, des prix déjà écrits restent et le ticket reste « lu » : l'appliquer à nouveau
    /// réécrit les mêmes prix, au prix d'une entrée d'historique en double.
    /// </summary>
    public async Task<ApplyResult> ApplyAsync(int receiptId)
    {
        await using var db = await factory.CreateDbContextAsync();

        var receipt = await RequireReceiptAsync(db, receiptId, includeLines: true);
        if (receipt.Status == ReceiptStatus.Applied)
            throw new DomainValidationException(AlreadyAppliedMessage);
        if (receipt.Status != ReceiptStatus.Extracted)
            throw new DomainValidationException(NotReadyMessage);

        // Lectures et validations d'abord : aucune écriture tant que toutes les lignes ne sont pas bonnes.
        var active = receipt.Lines
            .Where(l => !l.IsIgnored)
            .OrderBy(l => l.Position)
            .ThenBy(l => l.Id)
            .ToList();

        var ingredientIds = active.Select(l => l.IngredientId).OfType<int>().Distinct().ToList();
        var byId = await db.Ingredients.AsNoTracking()
            .Where(i => ingredientIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id);

        var priced = new List<(ReceiptLine Line, Ingredient Ingredient, decimal Total, decimal BaseQuantity)>();
        foreach (var line in active)
        {
            var ingredient = line.IngredientId is { } id ? byId.GetValueOrDefault(id) : null;
            var price = ReceiptLinePricing.Compute(line.Quantity, line.Unit, line.LineTotalExclVat, ingredient);
            if (price.Issue != ReceiptLineIssue.None)
                throw new DomainValidationException($"Ligne {line.Position + 1} « {line.RawLabel} » : {Describe(price.Issue)}");

            // Sans problème, le total et la quantité de base sont renseignés.
            priced.Add((line, ingredient!, line.LineTotalExclVat!.Value, price.BaseQuantity!.Value));
        }

        var latestPriceDates = await db.IngredientPrices
            .Where(p => ingredientIds.Contains(p.IngredientId))
            .GroupBy(p => p.IngredientId)
            .Select(g => new { IngredientId = g.Key, Latest = g.Max(p => p.Date) })
            .ToDictionaryAsync(x => x.IngredientId, x => x.Latest);

        var supplierId = receipt.SupplierId;
        var aliasesByLabel = (await db.SupplierAliases.Where(a => a.SupplierId == supplierId).ToListAsync())
            .ToDictionary(a => a.NormalizedLabel);

        var priceDate = DateTime.SpecifyKind(receipt.ReceiptDate ?? receipt.CreatedAt, DateTimeKind.Utc);

        // Écritures : les prix, ingrédient par ingrédient (dans l'ordre du ticket).
        var applied = new List<AppliedPrice>();
        foreach (var group in priced.GroupBy(p => p.Ingredient.Id))
        {
            var ingredient = group.First().Ingredient;
            var unitPrice = Math.Round(
                group.Sum(p => p.Total) / group.Sum(p => p.BaseQuantity), PriceScale, MidpointRounding.AwayFromZero);

            // Même règle que SetPriceAsync : le prix courant n'est remplacé que par un ticket au moins aussi récent que le dernier prix connu.
            var replacesCurrent = !latestPriceDates.TryGetValue(ingredient.Id, out var latest) || priceDate >= latest;

            await ingredients.SetPriceAsync(ingredient.Id, unitPrice, PriceSource.Receipt, supplierId, priceDate);

            applied.Add(new AppliedPrice(
                ingredient.Id, ingredient.Name, ingredient.BaseUnit, ingredient.CurrentPricePerBaseUnit, unitPrice, replacesCurrent));
            foreach (var member in group)
                member.Line.AppliedPricePerBaseUnit = unitPrice;
        }

        // Puis, en un seul enregistrement : lignes, alias et statut (appliqué en dernier).
        foreach (var (line, ingredient, _, _) in priced)
        {
            var label = LabelNormalizer.Normalize(line.RawLabel);
            if (label.Length == 0)
                continue;

            if (aliasesByLabel.TryGetValue(label, out var alias))
            {
                alias.IngredientId = ingredient.Id;
            }
            else
            {
                alias = new SupplierAlias { SupplierId = supplierId, NormalizedLabel = label, IngredientId = ingredient.Id };
                db.SupplierAliases.Add(alias);
                aliasesByLabel[label] = alias;
            }
        }

        receipt.Status = ReceiptStatus.Applied;
        receipt.AppliedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        return new ApplyResult(applied);
    }

    /// <summary>Supprime le ticket, ses lignes et son fichier ; refusé une fois appliqué (ses prix sont dans l'historique).</summary>
    public async Task DeleteAsync(int receiptId)
    {
        await using var db = await factory.CreateDbContextAsync();

        var receipt = await RequireReceiptAsync(db, receiptId, includeLines: false);
        if (receipt.Status == ReceiptStatus.Applied)
            throw new DomainValidationException(AppliedCannotBeDeletedMessage);

        // Les lignes et le fichier suivent par cascade en base.
        db.Receipts.Remove(receipt);
        await db.SaveChangesAsync();
    }

    private static async Task<Receipt> RequireReceiptAsync(
        ChefOsDbContext db, int id, bool includeLines, CancellationToken ct = default)
    {
        var query = db.Receipts.AsQueryable();
        if (includeLines)
            query = query.Include(r => r.Lines);

        return await query.FirstOrDefaultAsync(r => r.Id == id, ct)
               ?? throw new DomainValidationException(UnknownReceiptMessage);
    }

    private static async Task RequireSupplierAsync(ChefOsDbContext db, int? supplierId)
    {
        if (supplierId is { } id && !await db.Suppliers.AnyAsync(s => s.Id == id))
            throw new DomainValidationException(UnknownSupplierMessage);
    }

    private static async Task RequireIngredientsAsync(ChefOsDbContext db, IReadOnlyList<int> ingredientIds)
    {
        if (ingredientIds.Count == 0)
            return;

        var found = await db.Ingredients.CountAsync(i => ingredientIds.Contains(i.Id));
        if (found != ingredientIds.Count)
            throw new DomainValidationException(UnknownIngredientMessage);
    }

    private static async Task MarkFailedAsync(ChefOsDbContext db, Receipt receipt, string message, CancellationToken ct)
    {
        receipt.Status = ReceiptStatus.Failed;
        receipt.ErrorMessage = message;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Fournisseur du restaurant dont le nom normalisé est celui du ticket ; sinon celui dont le nom normalisé est contenu, mot entier, dans
    /// le nom lu (« METRO » dans « METRO CASH CARRY », le plus long d'abord) ; sinon l'unique fournisseur dont le nom contient le nom lu.
    /// Plusieurs candidats possibles pour ce dernier cas : aucun choix, le chef tranchera. Un mot partiel ne compte pas (« METRO » ≠ « METROPOLE »).
    /// </summary>
    private static int? MatchSupplier(string readName, IReadOnlyList<Supplier> suppliers)
    {
        var read = LabelNormalizer.Normalize(readName);
        if (read.Length == 0)
            return null;

        var candidates = suppliers
            .Select(s => (s.Id, Name: LabelNormalizer.Normalize(s.Name)))
            .Where(c => c.Name.Length > 0)
            .ToList();

        var exact = candidates.Where(c => c.Name == read).OrderBy(c => c.Id).Select(c => (int?)c.Id).FirstOrDefault();
        if (exact is not null)
            return exact;

        var insideRead = candidates
            .Where(c => ContainsWords(read, c.Name))
            .OrderByDescending(c => c.Name.Length)
            .ThenBy(c => c.Id)
            .Select(c => (int?)c.Id)
            .FirstOrDefault();
        if (insideRead is not null)
            return insideRead;

        var containingRead = candidates.Where(c => ContainsWords(c.Name, read)).ToList();
        return containingRead.Count == 1 ? containingRead[0].Id : null;
    }

    // Comparaison par mots entiers : les deux textes sont normalisés (mots séparés par une seule espace).
    private static bool ContainsWords(string text, string words) =>
        $" {text} ".Contains($" {words} ", StringComparison.Ordinal);

    private static List<ReceiptLineInput> NormalizeLines(IReadOnlyList<ReceiptLineInput> lines)
    {
        var normalized = new List<ReceiptLineInput>(lines.Count);
        foreach (var line in lines)
        {
            var vatRate = Round(line.VatRate, VatRateScale);
            if (vatRate is < 0m or > 1m)
                throw new DomainValidationException(VatRateMessage);

            normalized.Add(line with
            {
                RawLabel = (line.RawLabel ?? string.Empty).Trim(),
                Quantity = Round(line.Quantity, QuantityScale),
                LineTotalExclVat = Round(line.LineTotalExclVat, AmountScale),
                VatRate = vatRate
            });
        }

        return normalized;
    }

    private static string Describe(ReceiptLineIssue issue) => issue switch
    {
        ReceiptLineIssue.MissingIngredient => "ingrédient à choisir",
        ReceiptLineIssue.MissingQuantity => "quantité ou unité manquante",
        ReceiptLineIssue.IncompatibleUnit => "unité incompatible avec l'ingrédient",
        ReceiptLineIssue.InvalidTotal => "montant invalide",
        _ => throw new ArgumentOutOfRangeException(nameof(issue), issue, null)
    };

    // Une date saisie sans fuseau (sélecteur de date) est prise comme une date UTC.
    private static DateTime? ToUtc(DateTime? date) => date switch
    {
        null => null,
        { Kind: DateTimeKind.Local } local => local.ToUniversalTime(),
        { } other => DateTime.SpecifyKind(other, DateTimeKind.Utc)
    };

    private static decimal? Round(decimal? value, int scale) =>
        value is { } number ? Math.Round(number, scale, MidpointRounding.AwayFromZero) : null;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
