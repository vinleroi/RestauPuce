# ChefOS Étape 2 — Scan des tickets : plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Photographier un ticket fournisseur, le faire lire par Claude, valider les lignes et mettre à jour les prix des ingrédients (et donc les coûts de recettes), avec mémorisation des correspondances par fournisseur.

**Architecture:** Logique pure (calcul du prix d'une ligne, normalisation des libellés, suggestion d'ingrédient) dans `ChefOS.Core.Receipts`. Persistance, `ReceiptService` et lecteurs (`ClaudeReceiptReader`, `DemoReceiptReader`) dans `ChefOS.Infrastructure`. Écrans Blazor dans `ChefOS.Back`.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, SDK officiel `Anthropic` (C#), MudBlazor, xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-03-scan-tickets-design.md`

## Global Constraints

- Branche `feature/etape-2-tickets` dans `/Users/ahadji/Desktop/ChefOS`. Toutes les contraintes de l'étape 1 restent valables :
  - `net10.0`, paquets ajoutés sans version, `ChefOS.Core` sans aucun paquet ;
  - interface en français, culture `fr-BE`, tout `MudNumericField` avec `Culture="Ui.Fr"` ;
  - taux en fraction (`0.06`), calculs en `decimal` non arrondi ;
  - dates en UTC ;
  - commits `feat:` / `fix:` terminés par `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Précisions de colonnes :
  - montants en euros : `numeric(18,2)` ;
  - quantités : `numeric(18,4)` ;
  - prix par unité de base : `numeric(18,6)` ;
  - taux : `numeric(5,4)`.
- Tous les nouveaux types métier implémentent `ITenantScoped`, ont un filtre global et sont couverts par le test de modèle de la Task 1.
- Les services vérifient, à travers le contexte filtré, que tout id référencé appartient au restaurant courant : fournisseur, ingrédient, ticket.
- Modèle IA : `claude-opus-5-5`, effort `Medium`, sortie structurée, `fallbacks: "default"` avec le beta `server-side-fallback-2026-07-01`. La clé est lue dans `Anthropic:ApiKey` puis dans `ANTHROPIC_API_KEY`, et n'est jamais écrite dans un fichier versionné.
- Aucun test automatique n'appelle l'API réelle.
- Fichiers acceptés : `image/jpeg`, `image/png`, `image/webp`, `application/pdf`, 10 Mo maximum (`10 * 1024 * 1024`).

## Review Focus

1. **Ticket TTC sans taux de TVA** : la ligne est marquée « TVA à vérifier » et son total HT reste modifiable ; ce n'est jamais traité comme un prix HT silencieusement. → test `Map_TtcWithoutRate_FlagsVatToCheck` (Task 4).
2. **Deux lignes pour le même ingrédient** (par exemple deux sacs de farine) : le prix appliqué est la moyenne pondérée, pas la dernière ligne. → test `Apply_TwoLinesSameIngredient_UsesWeightedAverage` (Task 4).
3. **Ticket plus ancien que le dernier prix connu** : l'historique est enrichi, mais le prix courant n'est pas remplacé. → test `SetPrice_OlderDate_DoesNotReplaceCurrent` (Task 1).
4. **Ligne en pièces pour un ingrédient au kilo sans poids moyen** : la validation est refusée avec un message clair, sans division par zéro. → test `Compute_PieceForGramIngredientWithoutWeight_ReportsIncompatible` (Task 2).
5. **Réponse IA sans lignes ou JSON invalide** : le ticket passe au statut `Failed` avec un message, et la saisie manuelle reste possible. → tests `Parse_InvalidJson_Throws` (Task 5) et `Extract_ReaderThrows_MarksFailed` (Task 4).

---

## Structure des fichiers

```
src/ChefOS.Core/Receipts/
  Receipt.cs, ReceiptFile.cs, ReceiptLine.cs, ReceiptStatus.cs, SupplierAlias.cs
  ReceiptExtraction.cs            (records : sortie d'un lecteur)
  IReceiptReader.cs
  LabelNormalizer.cs
  ReceiptLinePricing.cs           (prix par unité de base d'une ligne + problèmes)
  IngredientMatcher.cs            (suggestion d'ingrédient)
src/ChefOS.Infrastructure/
  Data/Configurations/{Receipt,ReceiptFile,ReceiptLine,SupplierAlias}Configuration.cs
  Data/Migrations/*_AddReceipts.cs
  Receipts/{ClaudeReceiptReader,DemoReceiptReader,ReceiptExtractionJson,ReceiptReaderOptions}.cs
  Services/ReceiptService.cs, Services/Inputs.cs (ajouts)
  DependencyInjection.cs          (AddChefOsReceiptReader)
src/ChefOS.Back/
  Components/Pages/Receipts/{ReceiptList,ReceiptNew,ReceiptReview}.razor (+ ReceiptReviewForm.cs)
  Receipts/ReceiptFileEndpoint.cs (GET /receipts/{id}/file)
  Components/Pages/Dashboard.razor (bloc « Hausses de prix »)
tests/ChefOS.Tests/
  Core/{LabelNormalizerTests,ReceiptLinePricingTests,IngredientMatcherTests}.cs
  Infrastructure/{TenantModelTests,ReceiptServiceTests,ReceiptExtractionJsonTests}.cs
  Back/ReceiptReviewFormTests.cs
```

---

### Task 1 : Garde-fous multi-restaurant et prix des tickets en retard

**Files:**
- Create : `tests/ChefOS.Tests/Infrastructure/TenantModelTests.cs`
- Modify : `src/ChefOS.Infrastructure/Services/IngredientService.cs` (`SetPriceAsync`), `tests/ChefOS.Tests/Infrastructure/IngredientServiceTests.cs`

**Interfaces:**
- Produces : le test de modèle, que chaque task suivante ajoutant une entité verra échouer tant que le filtre global n'existe pas, et la nouvelle sémantique de `SetPriceAsync`.

- [ ] **Step 1 : Écrire les tests qui échouent ou qui épinglent le comportement**

```csharp
[Fact] public void EveryTenantScopedEntity_HasQueryFilter()
{   // parcourt db.Model.GetEntityTypes() dont ClrType implémente ITenantScoped ; chacun doit avoir un filtre global (GetDeclaredQueryFilters() non vide, ou API équivalente d'EF 10)
}
[Fact] public async Task NullTenant_ReadsNothing()             // données dans A ; contexte FixedTenantContext(null) → 0 ligne sur chaque DbSet ITenantScoped
[Fact] public async Task SetPrice_OlderDate_DoesNotReplaceCurrent()   // prix 0.010 au jour J ; SetPriceAsync(0.008, Receipt, date J-5) → historique 2 lignes, CurrentPricePerBaseUnit == 0.010
[Fact] public async Task SetPrice_SameOrNewerDate_ReplacesCurrent()
```

- [ ] **Step 2 : Lancer les tests** : `dotnet test --filter "TenantModelTests|IngredientServiceTests"`. Résultat attendu : `SetPrice_OlderDate_DoesNotReplaceCurrent` échoue, les tests de modèle passent déjà.
- [ ] **Step 3 : Modifier `SetPriceAsync`** pour ajouter toujours l'entrée d'historique, mais ne mettre à jour `CurrentPricePerBaseUnit` que si `dateUtc >= ` la date maximale déjà présente dans l'historique de l'ingrédient (ou s'il n'y a aucun historique).
- [ ] **Step 4 : Lancer les tests** : tout PASS, suite complète comprise.
- [ ] **Step 5 : Commit** `feat: garde-fous multi-restaurant et prix des tickets en retard`

---

### Task 2 : Domaine tickets (Core) — entités, normalisation, prix d'une ligne, suggestion

**Files:**
- Create : `src/ChefOS.Core/Receipts/*` (voir la structure)
- Test : `tests/ChefOS.Tests/Core/{LabelNormalizerTests,ReceiptLinePricingTests,IngredientMatcherTests}.cs`

**Interfaces:**
- Produces :
  - `enum ReceiptStatus { Pending, Extracted, Failed, Applied }`
  - `Receipt : ITenantScoped` : `Id`, `RestaurantId`, `SupplierId?`, `SupplierNameRaw` (string, défaut ""), `ReceiptDate` (DateTime?, UTC), `InvoiceNumber?`, `AmountsIncludeVat` (bool), `Total` (decimal?), `Status`, `ErrorMessage?`, `CreatedAt`, `AppliedAt?`, `List<ReceiptLine> Lines`.
  - `ReceiptFile : ITenantScoped` : `Id`, `RestaurantId`, `ReceiptId`, `FileName`, `ContentType`, `byte[] Data`.
  - `ReceiptLine : ITenantScoped` : `Id`, `RestaurantId`, `ReceiptId`, `Position` (int), `RawLabel`, `Quantity?`, `Unit` (MeasureUnit?), `UnitPrice?`, `LineTotal?`, `VatRate?`, `LineTotalExclVat?`, `VatToCheck` (bool), `IngredientId?`, `IsIgnored`, `AppliedPricePerBaseUnit?`.
  - `SupplierAlias : ITenantScoped` : `Id`, `RestaurantId`, `SupplierId?`, `NormalizedLabel`, `IngredientId`.
  - `record ReceiptExtraction(string? SupplierName, DateOnly? Date, string? InvoiceNumber, bool AmountsIncludeVat, decimal? Total, IReadOnlyList<ExtractedLine> Lines)` ; `record ExtractedLine(string Label, decimal? Quantity, MeasureUnit? Unit, decimal? UnitPrice, decimal? LineTotal, decimal? VatRate)`.
  - `interface IReceiptReader { Task<ReceiptExtraction> ReadAsync(byte[] content, string contentType, CancellationToken ct = default); }`
  - `static class LabelNormalizer { string Normalize(string label); }` : passage en majuscules invariantes, suppression des diacritiques, tout caractère hors `[A-Z0-9]` remplacé par un espace, espaces fusionnés, rognage.
  - `enum ReceiptLineIssue { None, MissingIngredient, MissingQuantity, IncompatibleUnit, InvalidTotal }` ; `record ReceiptLinePrice(decimal? PricePerBaseUnit, decimal? BaseQuantity, ReceiptLineIssue Issue)`.
  - `static class ReceiptLinePricing` :
    - `ReceiptLinePrice Compute(decimal? quantity, MeasureUnit? unit, decimal? lineTotalExclVat, Ingredient? ingredient)` : l'ordre des contrôles est ingrédient, puis quantité/unité, puis total > 0, puis conversion avec `UnitConverter.ToBase(quantity, unit, ingredient.BaseUnit, ingredient.AveragePieceWeightGrams)`. Résultat : `PricePerBaseUnit = total ÷ baseQty`.
    - `decimal? ExclVat(decimal? lineTotal, decimal? vatRate, bool amountsIncludeVat, out bool vatToCheck)` : si `!amountsIncludeVat`, renvoie le total tel quel et `vatToCheck = false` ; si TTC et taux connu, renvoie `total ÷ (1 + taux)` et `false` ; si TTC sans taux, renvoie le total tel quel et `vatToCheck = true`.
  - `static class IngredientMatcher { int? Suggest(string rawLabel, int? supplierId, IReadOnlyList<SupplierAlias> aliases, IReadOnlyList<Ingredient> ingredients); double Similarity(string a, string b); }` :
    - ordre : alias avec le même `SupplierId`, puis alias avec `SupplierId == null`, puis l'ingrédient non archivé ayant la meilleure `Similarity` ≥ 0.5 (égalité départagée par l'id le plus petit) ;
    - `Similarity` = |mots communes| ÷ |mots du plus court| sur les mots normalisés de 2 caractères ou plus ;
    - un alias qui pointe vers un ingrédient archivé ou inconnu est ignoré.

- [ ] **Step 1 : Écrire les tests qui échouent**

```csharp
// LabelNormalizerTests
[Theory]
[InlineData("Filet poul. 2,35KG", "FILET POUL 2 35KG")]
[InlineData("  Crème fraîche 40% ", "CREME FRAICHE 40")]
public void Normalize(string raw, string expected) => Assert.Equal(expected, LabelNormalizer.Normalize(raw));

// ReceiptLinePricingTests
[Fact] public void Compute_KgLine_PricePerGram()          // 2.5 kg, 30.00 € HT, ingrédient Gram → 0.012
[Fact] public void Compute_LitreLine_PricePerMl()         // 6 l, 9.00 € → 0.0015
[Fact] public void Compute_PieceIngredient()              // 30 pièces, 8.40 € → 0.28
[Fact] public void Compute_PieceForGramIngredientWithWeight()     // 10 pièces, poids moyen 60 g, 6.00 € → 0.01
[Fact] public void Compute_PieceForGramIngredientWithoutWeight_ReportsIncompatible()   // Review Focus 4
[Fact] public void Compute_MissingIngredient() / Compute_MissingQuantity() / Compute_ZeroTotal_Invalid()
[Fact] public void ExclVat_TtcWithRate()                  // 10.60, 0.06, TTC → 10.00, vatToCheck false
[Fact] public void ExclVat_TtcWithoutRate_Flags()         // → 10.60, vatToCheck true
[Fact] public void ExclVat_Ht_Unchanged()

// IngredientMatcherTests
[Fact] public void Suggest_SupplierAliasWins()
[Fact] public void Suggest_GlobalAliasWhenNoSupplierAlias()
[Fact] public void Suggest_ByNameSimilarity()             // "BEURRE DOUX 82% 1KG" → ingrédient "Beurre doux"
[Fact] public void Suggest_BelowThreshold_Null()
[Fact] public void Suggest_SkipsArchivedIngredientsAndAliasesToArchived()
```

- [ ] **Step 2 : Lancer les tests et vérifier l'échec** (erreurs de compilation)
- [ ] **Step 3 : Implémenter les types listés dans Interfaces**
- [ ] **Step 4 : Lancer les tests** → PASS
- [ ] **Step 5 : Commit** `feat: domaine des tickets fournisseurs (prix d'une ligne, normalisation, suggestion)`

---

### Task 3 : Persistance des tickets

**Files:**
- Create : `src/ChefOS.Infrastructure/Data/Configurations/{Receipt,ReceiptFile,ReceiptLine,SupplierAlias}Configuration.cs`, la migration `AddReceipts`
- Modify : `src/ChefOS.Infrastructure/Data/ChefOsDbContext.cs` (DbSets `Receipts`, `ReceiptFiles`, `ReceiptLines`, `SupplierAliases`, et un filtre global sur les quatre)

**Interfaces:**
- Consumes : les entités de la Task 2 et le test de modèle de la Task 1.
- Produces : les tables, puis la migration `dotnet ef migrations add AddReceipts -p src/ChefOS.Infrastructure -o Data/Migrations`.

Règles de mapping :
- **Statuts et unités** : `Status` et `Unit` en texte.
- **Précisions** :
  - `numeric(18,2)` : `Total`, `LineTotal`, `LineTotalExclVat`, `UnitPrice` ;
  - `numeric(18,4)` : `Quantity` ;
  - `numeric(5,4)` : `VatRate` ;
  - `numeric(18,6)` : `AppliedPricePerBaseUnit`.
- **Suppressions** :
  - `Receipt`→`Supplier` : `SetNull` ;
  - `ReceiptFile`→`Receipt` : `Cascade` (relation 1-1, index unique sur `ReceiptId`) ;
  - `ReceiptLine`→`Receipt` : `Cascade` ;
  - `ReceiptLine`→`Ingredient` : `SetNull` ;
  - `SupplierAlias`→`Ingredient` : `Cascade` ;
  - `SupplierAlias`→`Supplier` : `Cascade` ;
  - chaque entité→`Restaurant` : `Cascade`, via le helper existant `BelongsToRestaurant`.
- **Index unique `IX_SupplierAliases_Key`** sur (`RestaurantId`, `SupplierId`, `NormalizedLabel`) avec `.AreNullsDistinct(false)`.
- **`Receipt.Lines`** est trié par `Position` côté requêtes, pas dans le modèle.

- [ ] **Step 1 : Lancer `dotnet test --filter TenantModelTests` après avoir ajouté les DbSets sans filtre.** Le test de modèle doit échouer : c'est la preuve que la garde fonctionne.
- [ ] **Step 2 : Ajouter les filtres, les configurations et la migration.** Vérifier que la migration contient l'index avec `NULLS NOT DISTINCT`.
- [ ] **Step 3 : Ajouter le test d'intégration** `SupplierAlias_DuplicateKeyWithNullSupplier_Rejected` dans `TenantIsolationTests` ou dans un nouveau `ReceiptPersistenceTests` : deux alias (null, "BEURRE") dans le même restaurant → `DbUpdateException`.
- [ ] **Step 4 : Lancer la suite** → PASS, build sans avertissement
- [ ] **Step 5 : Commit** `feat: persistance des tickets fournisseurs et des correspondances`

---

### Task 4 : `ReceiptService`

**Files:**
- Create : `src/ChefOS.Infrastructure/Services/ReceiptService.cs`, ajouts dans `Services/Inputs.cs`
- Test : `tests/ChefOS.Tests/Infrastructure/ReceiptServiceTests.cs` (avec un `FakeReceiptReader` déclaré dans le fichier de test)

**Interfaces:**
- Consumes : `IReceiptReader`, `LabelNormalizer`, `ReceiptLinePricing`, `IngredientMatcher`, `IngredientService.SetPriceAsync`, `ITenantContext`.
- Produces :
  - `record ReceiptSummary(int Id, DateTime CreatedAt, DateTime? ReceiptDate, string SupplierName, decimal? Total, ReceiptStatus Status, int LineCount)`
  - `record ReceiptLineInput(int? Id, string RawLabel, decimal? Quantity, MeasureUnit? Unit, decimal? LineTotalExclVat, decimal? VatRate, int? IngredientId, bool IsIgnored)`
  - `record ReceiptDraftInput(int ReceiptId, int? SupplierId, DateTime? ReceiptDate, string? InvoiceNumber, IReadOnlyList<ReceiptLineInput> Lines)`
  - `record AppliedPrice(int IngredientId, string IngredientName, BaseUnit BaseUnit, decimal? OldPricePerBaseUnit, decimal NewPricePerBaseUnit, bool ReplacedCurrent)`
  - `record ApplyResult(IReadOnlyList<AppliedPrice> Prices)`
  - `class ReceiptService(IDbContextFactory<ChefOsDbContext> factory, IReceiptReader reader, IngredientService ingredients, ITenantContext tenant)` :
    - `Task<int> CreateAsync(byte[] content, string fileName, string contentType)` : valide le type et la taille (messages « Format non pris en charge (JPEG, PNG, WebP ou PDF) » et « Fichier trop volumineux (10 Mo maximum) »), crée `Receipt` (Pending, `CreatedAt` UTC) et `ReceiptFile`.
    - `Task ExtractAsync(int receiptId, CancellationToken ct = default)` :
      1. appelle le lecteur ;
      2. si le lecteur lève une exception ou ne renvoie aucune ligne, passe le statut à `Failed` avec `ErrorMessage` (message de l'exception, ou « Aucune ligne n'a été lue sur ce ticket ») et ne relance pas l'exception ;
      3. sinon, remplit l'en-tête : `SupplierNameRaw`, le fournisseur rapproché par nom normalisé, la date à minuit UTC, `AmountsIncludeVat` et `Total` ;
      4. crée les lignes : `Position` = index ; `LineTotalExclVat` et `VatToCheck` via `ReceiptLinePricing.ExclVat` ; `IngredientId` via `IngredientMatcher.Suggest` ;
      5. passe le statut à `Extracted`.
    - `Task<IReadOnlyList<ReceiptSummary>> ListAsync()` : du plus récent au plus ancien.
    - `Task<Receipt?> GetAsync(int id)` : avec les lignes triées par `Position`, sans les octets du fichier.
    - `Task<ReceiptFile?> GetFileAsync(int receiptId)`
    - `Task SaveDraftAsync(ReceiptDraftInput input)` :
      - refusé si le statut est `Applied` (« Ce ticket a déjà été appliqué ») ;
      - vérifie que le fournisseur et les ingrédients appartiennent au restaurant (« Fournisseur introuvable », « Ingrédient introuvable ») ;
      - remplace les lignes en conservant leur ordre ;
      - un ticket `Failed` complété à la main passe à `Extracted`.
    - `Task<ApplyResult> ApplyAsync(int receiptId)` :
      1. statut requis `Extracted` ;
      2. calcule chaque ligne non ignorée avec `ReceiptLinePricing.Compute` ; au premier problème, lève `DomainValidationException` « Ligne {Position+1} « {RawLabel} » : {problème} », avec les libellés « ingrédient à choisir », « quantité ou unité manquante », « unité incompatible avec l'ingrédient », « montant invalide » ;
      3. regroupe par ingrédient : prix = Σ totaux HT ÷ Σ quantités de base ;
      4. pour chaque ingrédient, appelle `SetPriceAsync(id, prix, PriceSource.Receipt, receipt.SupplierId, receipt.ReceiptDate ?? CreatedAt)` ;
      5. enregistre `AppliedPricePerBaseUnit` sur les lignes ;
      6. crée ou met à jour un `SupplierAlias` par couple (`SupplierId`, `LabelNormalizer.Normalize(RawLabel)`) ;
      7. passe le statut à `Applied` avec `AppliedAt` ;
      8. renvoie les anciens et nouveaux prix, l'ancien prix étant lu avant l'application.
    - `Task DeleteAsync(int receiptId)` : refusé si le statut est `Applied`.
    - Id inconnu ou appartenant à un autre restaurant : `DomainValidationException("Ticket introuvable")`.

- [ ] **Step 1 : Écrire les tests qui échouent**

```csharp
[Fact] public async Task Create_RejectsUnsupportedType() / Create_RejectsTooLarge()
[Fact] public async Task Extract_MapsHeaderLinesAndSuggestions()     // le faux lecteur renvoie 3 lignes, dont une qui correspond par nom
[Fact] public async Task Extract_UsesSupplierAliasFirst()
[Fact] public async Task Extract_ReaderThrows_MarksFailed()           // Review Focus 5
[Fact] public async Task Extract_NoLines_MarksFailed()
[Fact] public async Task Map_TtcWithoutRate_FlagsVatToCheck()         // Review Focus 1
[Fact] public async Task SaveDraft_ForeignIngredient_Throws()
[Fact] public async Task SaveDraft_FailedReceiptWithManualLines_BecomesExtracted()
[Fact] public async Task Apply_UpdatesPricesHistoryAndAliases()       // prix courant, entrée Receipt avec le fournisseur et la date, alias créé
[Fact] public async Task Apply_TwoLinesSameIngredient_UsesWeightedAverage()   // Review Focus 2 : 1 kg à 10 € + 3 kg à 24 € → 0.0085 €/g
[Fact] public async Task Apply_LineWithoutIngredient_RefusedWithLineNumber()
[Fact] public async Task Apply_IgnoredLinesSkipped()
[Fact] public async Task Apply_Twice_Refused()
[Fact] public async Task Apply_ChangesDishCost()                     // RecipeService.LoadBookAsync avant et après : le coût du plat change
[Fact] public async Task OtherRestaurant_CannotSeeOrApply()
```

- [ ] **Step 2 : Lancer → échec**
- [ ] **Step 3 : Implémenter**
- [ ] **Step 4 : Lancer → PASS**, puis la suite complète
- [ ] **Step 5 : Commit** `feat: service des tickets (lecture, brouillon, application des prix, mémorisation)`

---

### Task 5 : Lecteurs de tickets (Claude et Demo)

**Files:**
- Create : `src/ChefOS.Infrastructure/Receipts/{ClaudeReceiptReader,DemoReceiptReader,ReceiptExtractionJson,ReceiptReaderOptions}.cs`
- Modify : `src/ChefOS.Infrastructure/DependencyInjection.cs` (`AddChefOsReceiptReader(this IServiceCollection, IConfiguration)` + enregistrement de `ReceiptService`), `src/ChefOS.Back/Program.cs` (appel), `src/ChefOS.Back/appsettings.Development.json` (`"Receipts": { "Reader": "Claude" }`), `README.md` (section « Scan des tickets »)
- Test : `tests/ChefOS.Tests/Infrastructure/ReceiptExtractionJsonTests.cs`, ainsi qu'un test de `DemoReceiptReader`

**Interfaces:**
- Consumes : `IReceiptReader`, `ReceiptExtraction`.
- Produces :
  - `ReceiptReaderOptions { string Reader = "Claude"; }` (section `Receipts`).
  - `static class ReceiptExtractionJson`, qui contient le schéma JSON envoyé à l'API et le parseur :
    - `IReadOnlyDictionary<string, JsonElement> Schema` (objet ; `additionalProperties: false` ; `required` pour tous les champs, les valeurs absentes étant `null`) ;
    - `ReceiptExtraction Parse(string json)` : lève `InvalidOperationException("Réponse de lecture illisible")` sur un JSON invalide ; unité `"g"|"kg"|"ml"|"cl"|"l"|"piece"` → `MeasureUnit`, valeur inconnue → null ; date `yyyy-MM-dd` ; les décimaux sont lus en culture invariante.
    - Champs du schéma : `supplier_name` (string|null), `date` (string|null), `invoice_number` (string|null), `amounts_include_vat` (boolean), `total` (number|null), `lines` (tableau d'objets {`label` string, `quantity` number|null, `unit` enum|null, `unit_price` number|null, `line_total` number|null, `vat_rate` number|null}).
  - `ClaudeReceiptReader(AnthropicClient client) : IReceiptReader` :
    - **Appel** : `client.Beta.Messages.Create` avec `Model = "claude-opus-5-5"`, `MaxTokens = 16000`, `OutputConfig` (effort `Medium` + format JSON avec `Schema`), `Betas = ["server-side-fallback-2026-07-01"]` et `fallbacks: "default"`.
    - **Contenu de l'appel** : le contenu utilisateur est un bloc image base64 (jpeg, png ou webp) ou un bloc document PDF base64, suivi d'un bloc texte : la consigne.
    - **Consigne, en français** : lire le ticket ou la facture d'un fournisseur de restaurant, recopier les libellés tels qu'imprimés, indiquer si les montants sont TTC, et convertir les conditionnements en quantité totale (« 6 x 1L » → 6 l, « 2 x 2,5 kg » → 5 kg). Pour une ligne vendue au poids, l'unité est `kg` ou `g` ; pour une ligne à la pièce ou au colis sans poids, `piece`. Le taux de TVA est une fraction (0.06). Les lignes de remise, de consigne et de sous-total doivent être omises.
    - **Statut de la réponse** : `stop_reason == "refusal"` → `InvalidOperationException("La lecture du ticket a été refusée")` ; `max_tokens` → `InvalidOperationException("Ticket trop long pour être lu")`.
    - **Exceptions de l'API** (`Anthropic.Exceptions`) : elles sont traduites en `InvalidOperationException` avec un message français (clé invalide, quota, indisponible).
    - **SDK** : les noms exacts viennent de la doc du skill `claude-api` (C#) ; les noms manquants se corrigent avec le compilateur, en suivant le schéma de nommage du SDK.
  - `DemoReceiptReader : IReceiptReader` :
    - renvoie un ticket factice, après une attente de 800 ms pour simuler la lecture ;
    - l'en-tête : fournisseur « Metro Cash & Carry », date du jour, HT ;
    - au moins 6 lignes dont les libellés ressemblent à des ingrédients du Petit Bistro (par exemple « BEURRE DOUX 82% 1KG », « OIGNONS JAUNES FILET 5KG », « BIERE BRUNE 33CL X24 » ; lire `DemoSeeder` pour les noms exacts) ;
    - au moins une ligne avec une hausse de prix d'environ 10 % par rapport au seed et une ligne sans correspondance (« SACS POUBELLE 100L ») que le chef ignorera.
  - `AddChefOsReceiptReader(IServiceCollection, IConfiguration)` :
    - lie `ReceiptReaderOptions` et enregistre `ReceiptService` en scoped ;
    - si `Reader == "Demo"`, enregistre `DemoReceiptReader` ;
    - sinon, lit la clé dans `configuration["Anthropic:ApiKey"]`, puis dans `ANTHROPIC_API_KEY` ;
      - clé présente : enregistre un `AnthropicClient` singleton (avec `ApiKey`) et `ClaudeReceiptReader` ;
      - clé absente : enregistre un `MissingKeyReceiptReader`, qui lève `InvalidOperationException("Clé API Anthropic non configurée : voir le README (dotnet user-secrets set Anthropic:ApiKey …)")`, si bien que le ticket passe en `Failed` avec ce message.

- [ ] **Step 1 : Écrire les tests qui échouent**

```csharp
[Fact] public void Parse_FullResponse()                 // JSON en dur, 2 lignes (kg et piece), TTC, date → valeurs exactes
[Fact] public void Parse_UnknownUnit_IsNull()
[Fact] public void Parse_InvalidJson_Throws()           // Review Focus 5
[Fact] public void Schema_IsStrictObject()              // type object, additionalProperties false, les 6 propriétés de premier niveau sont required
[Fact] public async Task DemoReader_ReturnsLines()
[Fact] public void Registration_DemoMode_UsesDemoReader() / Registration_NoKey_UsesMissingKeyReader()
```

- [ ] **Step 2 : Lancer → échec**
- [ ] **Step 3 : Ajouter le paquet `Anthropic` à Infrastructure et implémenter.** Le code de `ClaudeReceiptReader` doit compiler ; il n'est pas appelé dans les tests.
- [ ] **Step 4 : Lancer → PASS**, build sans avertissement
- [ ] **Step 5 : README.** Ajouter une section « Scan des tickets » : `dotnet user-secrets set Anthropic:ApiKey <clé> --project src/ChefOS.Back`, le mode `Receipts:Reader=Demo` pour une démonstration sans clé, et le coût indicatif (quelques centimes par ticket).
- [ ] **Step 6 : Commit** `feat: lecture des tickets par Claude (et lecteur de démonstration)`

---

### Task 6 : Écrans des tickets

**Files:**
- Create : `src/ChefOS.Back/Components/Pages/Receipts/{ReceiptList,ReceiptNew,ReceiptReview}.razor`, `ReceiptReviewForm.cs`, `src/ChefOS.Back/Receipts/ReceiptFileEndpoint.cs`
- Modify : `Components/Layout/NavMenu.razor` (lien « Tickets », icône reçu, entre Ingrédients et Fournisseurs), `Program.cs` (mapping de l'endpoint)
- Test : `tests/ChefOS.Tests/Back/ReceiptReviewFormTests.cs`

**Interfaces:**
- Consumes : `ReceiptService`, `IngredientService.ListAsync`, `SupplierService.ListAsync/SaveAsync`, `ReceiptLinePricing`, `Ui.*`.
- Produces :
  - `GET /receipts/{id:int}/file` : `RequireAuthorization`, passe par `ReceiptService.GetFileAsync` (donc filtré par restaurant), renvoie le fichier avec son `ContentType`, 404 sinon.
  - `ReceiptReviewForm` : état des lignes éditables, avec `From(Receipt, …)`, `ToDraftInput()` et `Evaluate(IReadOnlyDictionary<int, Ingredient>)` → par ligne, le prix calculé, l'ancien prix, la variation (`(nouveau - ancien) ÷ ancien`) et le problème. Testé unitairement.

Écrans (spec §2) :
- **`/receipts`** : `MudTable` avec les colonnes Date, Fournisseur, Total, Lignes et Statut. Le statut est une puce : En attente / Lu / Échec / Appliqué. Le bouton « Scanner un ticket » mène à `/receipts/new`.
- **`/receipts/new`** :
  - `MudFileUpload` avec `accept="image/jpeg,image/png,image/webp,application/pdf"` et l'attribut `capture="environment"` pour la caméra du mobile (si MudBlazor le permet ; sinon un `InputFile` stylé).
  - Les octets sont lus avec `OpenReadStream(maxAllowedSize: 10 Mo)`, puis `CreateAsync` puis `ExtractAsync`, avec un indicateur « Lecture du ticket… ». On est ensuite redirigé vers `/receipts/{id}`.
  - Les `DomainValidationException` sont affichées dans une snackbar.
- **`/receipts/{id}`** :
  - **Disposition** : deux colonnes sur grand écran, l'aperçu du fichier à gauche (`<img>`, ou `<iframe>` pour un PDF, pointant vers l'endpoint), le formulaire à droite ; une seule colonne sur mobile.
  - **En-tête** : sélection du fournisseur ; si aucun ne correspond et que `SupplierNameRaw` est rempli, un bouton « Créer « {nom} » » ; date ; n° de facture ; total lu ; puce TTC/HT.
  - **Lignes** : libellé ; quantité (`MudNumericField`, Culture) ; unité ; total HT (Culture), avec la puce « TVA à vérifier » quand `VatToCheck` est vrai ; ingrédient (autocomplétion sur les ingrédients non archivés) ; prix calculé (`Ui.PricePerDisplayUnit`) ; ancien prix ; variation ; case « Ignorer » ; bouton supprimer. Le bouton « Ajouter une ligne » complète le tableau.
  - **Couleur de la variation** : ▲ rouge au-delà de +5 %, ▼ vert en dessous de -5 %, gris sinon.
  - **Problèmes** : une ligne avec un problème bloquant est surlignée et porte le libellé du problème.
  - **Actions** : « Enregistrer » (`SaveDraftAsync`) ; « Valider et mettre à jour les prix » (`SaveDraftAsync` puis `ApplyAsync`), qui affiche le récapitulatif `ApplyResult` dans un `MudAlert` : nom, ancien et nouveau prix dans l'unité d'affichage, variation. Une mention « historique seulement (ticket plus ancien) » apparaît quand `ReplacedCurrent` est faux.
  - **Statut `Failed`** : `MudAlert` avec `ErrorMessage` et la saisie manuelle disponible.
  - **Statut `Applied`** : tout est en lecture seule.
  - **Ticket introuvable** : « Élément introuvable ».
  - **Suppression** d'un ticket non appliqué, avec une confirmation.

- [ ] **Step 1 : Tests de `ReceiptReviewForm`** (variation, problème par ligne, mapping vers `ReceiptDraftInput`), qui échouent d'abord
- [ ] **Step 2 : Implémenter le formulaire, l'endpoint et les trois pages**
- [ ] **Step 3 : Build sans avertissement, `dotnet test`**, dont `NumericFieldCultureTests`
- [ ] **Step 4 : Vérification manuelle en mode Demo** (`Receipts__Reader=Demo`) avec le compte de démo :
  1. scanner un fichier image quelconque ;
  2. les lignes sont pré-remplies et les correspondances suggérées ;
  3. ignorer la ligne « SACS POUBELLE » ;
  4. valider : le récapitulatif montre au moins une hausse ;
  5. la fiche de l'ingrédient concerné affiche le nouveau prix et un point « Receipt » dans l'historique ;
  6. le coût d'une recette qui l'utilise a changé ;
  7. un second ticket du même fournisseur pré-remplit l'ingrédient grâce à l'alias.
  Nettoyer ensuite les données de test.
- [ ] **Step 5 : Commit** `feat: écrans de scan et de validation des tickets`

---

### Task 7 : Hausses de prix sur le tableau de bord

**Files:**
- Modify : `src/ChefOS.Infrastructure/Services/DashboardService.cs`, `Services/Inputs.cs`, `src/ChefOS.Back/Components/Pages/Dashboard.razor`
- Test : `tests/ChefOS.Tests/Infrastructure/DashboardServiceTests.cs`

**Interfaces:**
- Produces : `record PriceChange(int IngredientId, string Name, BaseUnit BaseUnit, decimal PreviousPrice, decimal CurrentPrice, decimal ChangePercent, DateTime Date)` ; `Task<IReadOnlyList<PriceChange>> DashboardService.GetRecentPriceChangesAsync(int days = 30, decimal minChange = 0.05m)` :
  - pour chaque ingrédient non archivé, compare les deux dernières entrées d'historique ;
  - garde ceux dont la dernière entrée a moins de `days` jours et dont la variation absolue vaut au moins `minChange` ;
  - trie par variation décroissante.

- [ ] **Step 1 : Tests** `PriceChanges_ListsRecentIncreaseOverThreshold`, `PriceChanges_IgnoresOldAndSmallChanges`
- [ ] **Step 2 : Implémenter le service** et ajouter au tableau de bord un bloc « Variations de prix (30 jours) » : nom (lien vers la fiche), ancien → nouveau prix dans l'unité d'affichage, variation colorée. Liste vide → « Aucune variation notable ».
- [ ] **Step 3 : Suite complète** → PASS, build sans avertissement
- [ ] **Step 4 : Commit** `feat: variations de prix récentes sur le tableau de bord`
