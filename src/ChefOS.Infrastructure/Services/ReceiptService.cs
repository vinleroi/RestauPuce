using ChefOS.Core.Common;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Receipts;
using ChefOS.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ChefOS.Infrastructure.Services;

/// <summary>
/// Tickets fournisseurs du restaurant courant : téléversement, lecture par le <see cref="IReceiptReader"/> (IA ou démo),
/// brouillon corrigé par le chef, puis application des prix aux ingrédients et mémorisation des libellés.
/// Les clés étrangères de la base sont à colonne unique : la vérification qu'un fournisseur, un ingrédient, une ligne ou un
/// ticket appartient bien au restaurant courant se fait ici, via le contexte filtré par restaurant (qui estampille aussi les ajouts).
/// Les octets du fichier ne sont lus que par <see cref="GetFileAsync"/> : ni les listes ni <see cref="GetAsync"/> ne les chargent.
/// Une lecture qui échoue est journalisée (avertissement, avec l'exception d'origine et l'identifiant du ticket) ; ni le contenu du fichier
/// ni la clé de l'API n'y figurent.
/// </summary>
public class ReceiptService(IDbContextFactory<ChefOsDbContext> factory, IReceiptReader reader, ILogger<ReceiptService> logger, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

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
    private const string FutureDateMessage = "La date du ticket ne peut pas être dans le futur";
    private const string NoLineToApplyMessage = "Aucune ligne à appliquer";

    // Échelles des colonnes numeric : arrondir avant de valider/comparer évite qu'une valeur change à l'enregistrement.
    private const int QuantityScale = 4;
    private const int AmountScale = 2;
    private const int VatRateScale = 4;

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

        var receipt = new Receipt { Status = ReceiptStatus.Pending, CreatedAt = _clock.GetUtcNow().UtcDateTime };
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
            // Journalisé : le message français du ticket ne garde pas l'exception d'origine (motif de l'API, pile). Jamais les octets ni la clé.
            logger.LogWarning(ex, "Lecture du ticket {ReceiptId} échouée", receiptId);
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
        // Une date lue dans le futur (année mal lue) serait gardée comme référence de tous les prix suivants : elle est écartée, le chef la saisit.
        var readDay = extraction.Date is { } day
            ? DateTime.SpecifyKind(day.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc)
            : (DateTime?)null;
        var calendar = await RestaurantCalendar.ForCurrentRestaurantAsync(db, _clock, ct);
        receipt.ReceiptDate = readDay is { } readable && !calendar.IsInTheFuture(readable) ? readable : null;
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
            // Un taux hors de 0–1 n'est pas une fraction de TVA (6 ou 21 au lieu de 0,06 ou 0,21) : il est traité comme inconnu, sans quoi
            // le prix hors TVA serait faux ou la colonne numeric(5,4) déborderait. Sur un ticket TTC, la ligne est alors « TVA à vérifier ».
            var vatRate = read.VatRate is >= 0m and <= 1m ? read.VatRate : null;
            var exclVat = ReceiptLinePricing.ExclVat(read.LineTotal, vatRate, extraction.AmountsIncludeVat, out var vatToCheck);
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
                VatRate = vatRate,
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
    /// la mention « TVA à vérifier » ne reste que si ni le taux ni le total de la ligne n'ont été modifiés. Un ticket en échec ou pas encore lu, complété
    /// avec au moins une ligne, passe à <see cref="ReceiptStatus.Extracted"/>. Une date postérieure à demain (fuseau du restaurant) est refusée (voir <see cref="RestaurantCalendar.IsInTheFuture"/>).
    /// Tout est enregistré d'un coup ou pas du tout.
    /// </summary>
    public async Task SaveDraftAsync(ReceiptDraftInput input)
    {
        await using var db = await factory.CreateDbContextAsync();

        var receipt = await RequireReceiptAsync(db, input.ReceiptId, includeLines: true);
        if (receipt.Status == ReceiptStatus.Applied)
            throw new DomainValidationException(AlreadyAppliedMessage);

        var receiptDate = ToUtc(input.ReceiptDate);
        if (receiptDate is { } day && (await RestaurantCalendar.ForCurrentRestaurantAsync(db, _clock)).IsInTheFuture(day))
            throw new DomainValidationException(FutureDateMessage);

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
        receipt.ReceiptDate = receiptDate;
        receipt.InvoiceNumber = NullIfBlank(input.InvoiceNumber);
        if (receipt.Status is ReceiptStatus.Failed or ReceiptStatus.Pending && lines.Count > 0)
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
    /// fournisseur (créés ou mis à jour).
    /// Tout ou rien : une fois les lignes validées, une seule transaction réserve le ticket (passage atomique de « lu » à « appliqué », une seule
    /// exécution l'emporte), écrit les prix, les prix de ligne et les alias, puis valide. Tout échec annule tout, statut compris : le ticket reste
    /// « lu » et peut être appliqué à nouveau sans doublon d'historique. Deux applications simultanées : l'une réussit, l'autre est refusée
    /// (« déjà appliqué »). Sont aussi refusés, avant toute écriture, un ticket daté d'après-demain ou plus tard (voir <see cref="RestaurantCalendar.IsInTheFuture"/> :
    /// il rendrait « en retard » tous les tickets suivants) et un ticket sans aucune ligne à appliquer (vide, ou toutes les lignes ignorées).
    /// </summary>
    public async Task<ApplyResult> ApplyAsync(int receiptId)
    {
        await using var db = await factory.CreateDbContextAsync();

        var receipt = await RequireReceiptAsync(db, receiptId, includeLines: true);
        RequireApplicable(receipt.Status);
        var calendar = await RestaurantCalendar.ForCurrentRestaurantAsync(db, _clock);
        if (receipt.ReceiptDate is { } receiptDay && calendar.IsInTheFuture(receiptDay))
            throw new DomainValidationException(FutureDateMessage);

        // Lectures et validations d'abord : aucune écriture tant que toutes les lignes ne sont pas bonnes.
        var active = receipt.Lines
            .Where(l => !l.IsIgnored)
            .OrderBy(l => l.Position)
            .ThenBy(l => l.Id)
            .ToList();
        if (active.Count == 0)
            throw new DomainValidationException(NoLineToApplyMessage);

        var ingredientIds = active.Select(l => l.IngredientId).OfType<int>().Distinct().ToList();
        var byId = await db.Ingredients.AsNoTracking()
            .Where(i => ingredientIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id);

        var priced = new List<(ReceiptLine Line, int IngredientId, decimal Total, decimal BaseQuantity)>();
        foreach (var line in active)
        {
            var ingredient = line.IngredientId is { } id ? byId.GetValueOrDefault(id) : null;
            var price = ReceiptLinePricing.Compute(line.Quantity, line.Unit, line.LineTotalExclVat, ingredient);
            if (price.Issue != ReceiptLineIssue.None)
                throw new DomainValidationException($"Ligne {line.Position + 1} « {line.RawLabel} » : {Describe(price.Issue)}");

            // Sans problème, l'ingrédient, le total et la quantité de base sont renseignés.
            priced.Add((line, ingredient!.Id, line.LineTotalExclVat!.Value, price.BaseQuantity!.Value));
        }

        var supplierId = receipt.SupplierId;
        // Un ticket sans date lue est situé au jour local de sa création : l'entrée de prix est un jour, pas un instant.
        var priceDate = DateTime.SpecifyKind(receipt.ReceiptDate ?? calendar.DayOf(receipt.CreatedAt), DateTimeKind.Utc);

        // Écritures : une transaction, un seul SaveChanges pour les données ; rien n'est visible tant qu'elle n'est pas validée.
        await using var transaction = await db.Database.BeginTransactionAsync();

        // Réservation du ticket : « lu » -> « appliqué » en une instruction. Si un autre appel l'a déjà pris (double clic, deux onglets), cette
        // instruction attend sa fin puis ne trouve plus de ticket « lu » : aucune ligne modifiée, refus. Annulée avec la transaction en cas d'échec.
        DateTime? appliedAt = calendar.UtcNow;
        var claimed = await db.Receipts
            .Where(r => r.Id == receiptId && r.Status == ReceiptStatus.Extracted)
            .ExecuteUpdateAsync(set => set
                .SetProperty(r => r.Status, ReceiptStatus.Applied)
                .SetProperty(r => r.AppliedAt, appliedAt));
        if (claimed != 1)
            throw new DomainValidationException(await RefusalAsync(db, receiptId));

        var aliasesByLabel = (await db.SupplierAliases.Where(a => a.SupplierId == supplierId).ToListAsync())
            .ToDictionary(a => a.NormalizedLabel);

        // Les ingrédients sont relus dans la transaction : l'ancien prix annoncé est celui qui est remplacé. Un ingrédient disparu entre-temps
        // fait échouer l'écriture du prix (« Ingrédient introuvable ») et annule tout.
        var current = await db.Ingredients.Where(i => ingredientIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id);

        var applied = new List<AppliedPrice>();
        foreach (var group in priced.GroupBy(p => p.IngredientId))
        {
            var ingredient = current.GetValueOrDefault(group.Key)
                             ?? throw new DomainValidationException(UnknownIngredientMessage);
            var unitPrice = IngredientService.NormalizePrice(group.Sum(p => p.Total) / group.Sum(p => p.BaseQuantity));
            var oldPrice = ingredient.CurrentPricePerBaseUnit;

            var replacesCurrent = await IngredientService.AddPriceEntryAsync(
                db, calendar, ingredient.Id, unitPrice, PriceSource.Receipt, supplierId, priceDate);

            applied.Add(new AppliedPrice(
                ingredient.Id, ingredient.Name, ingredient.BaseUnit, oldPrice, unitPrice, replacesCurrent));
            foreach (var member in group)
                member.Line.AppliedPricePerBaseUnit = unitPrice;
        }

        foreach (var (line, ingredientId, _, _) in priced)
        {
            var label = LabelNormalizer.Normalize(line.RawLabel);
            if (label.Length == 0)
                continue;

            if (aliasesByLabel.TryGetValue(label, out var alias))
            {
                alias.IngredientId = ingredientId;
            }
            else
            {
                alias = new SupplierAlias { SupplierId = supplierId, NormalizedLabel = label, IngredientId = ingredientId };
                db.SupplierAliases.Add(alias);
                aliasesByLabel[label] = alias;
            }
        }

        await db.SaveChangesAsync();
        await transaction.CommitAsync();

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

    private static void RequireApplicable(ReceiptStatus status)
    {
        if (status == ReceiptStatus.Applied)
            throw new DomainValidationException(AlreadyAppliedMessage);
        if (status != ReceiptStatus.Extracted)
            throw new DomainValidationException(NotReadyMessage);
    }

    // Le ticket n'a pas pu être réservé : on relit son état actuel (un autre appel a pu l'appliquer, le supprimer ou le modifier entre-temps).
    private static async Task<string> RefusalAsync(ChefOsDbContext db, int receiptId)
    {
        var status = await db.Receipts.AsNoTracking()
            .Where(r => r.Id == receiptId)
            .Select(r => (ReceiptStatus?)r.Status)
            .FirstOrDefaultAsync();

        return status switch
        {
            null => UnknownReceiptMessage,
            ReceiptStatus.Applied => AlreadyAppliedMessage,
            _ => NotReadyMessage
        };
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

    // Le ticket porte un jour, pas une heure : on garde le jour du calendrier saisi (aucun décalage de fuseau) à minuit UTC.
    private static DateTime? ToUtc(DateTime? date) =>
        date is { } value ? DateTime.SpecifyKind(value.Date, DateTimeKind.Utc) : null;

    private static decimal? Round(decimal? value, int scale) =>
        value is { } number ? Math.Round(number, scale, MidpointRounding.AwayFromZero) : null;

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
