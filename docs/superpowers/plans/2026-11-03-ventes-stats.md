# ChefOS Étape 3 — Ventes, pertes et statistiques : plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Saisir ventes, commandes, pertes et inventaires, puis montrer par période le CA, les marges par plat, le menu engineering, le coût des pertes et l'écart entre consommation théorique et réelle.

**Architecture:** Les calculs purs (décomposition des recettes en ingrédients bruts, prix à une date, menu engineering, écart d'inventaire) vivent dans `ChefOS.Core.Sales` et `ChefOS.Core.Costing`. La persistance et les services (`SalesService`, `OrderService`, `WasteService`, `InventoryService`, `StatsService`) vivent dans Infrastructure, les écrans dans le Back.

**Tech Stack:** .NET 10, EF Core 10 + Npgsql, MudBlazor (MudChart), xUnit + Testcontainers.

**Spec:** `docs/superpowers/specs/2026-11-03-ventes-stats-design.md`

## Global Constraints

- Branche `feature/etape-3-ventes-stats`.
- Contraintes des étapes 1 et 2 :
  - `net10.0`, paquets sans version, `ChefOS.Core` sans paquet ;
  - interface en français, culture `fr-BE`, tout `MudNumericField` avec `Culture="Ui.Fr"` ;
  - taux en fraction ;
  - calculs en `decimal` non arrondis ;
  - dates en UTC ;
  - commits `feat:`/`fix:` terminés par `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Précisions de colonnes :
  - montants en euros : `numeric(18,2)` ;
  - quantités : `numeric(18,4)` ;
  - prix par unité de base : `numeric(18,6)` ;
  - taux : `numeric(5,4)`.
- Toute nouvelle entité métier implémente `ITenantScoped`. Elle reçoit un filtre global, et `TenantModelTests.SeedEveryTenantScopedTypeAsync` doit en créer une ligne.
- Les services vérifient, via le contexte filtré, que chaque id référencé (plat, recette, ingrédient, commande, inventaire) appartient au restaurant courant.
- Les dates « jour » sont stockées à 00:00 UTC (`DateTime.SpecifyKind(d.Date, Utc)`). Les périodes sont des intervalles de jours inclusifs `[from, to]`.
- Les tests n'utilisent pas d'égalité sur une valeur dérivée de `UtcNow` une fois persistée : on utilise des dates fixes.
- Chaque nouveau service est enregistré en scoped dans `AddChefOsInfrastructure` (`src/ChefOS.Infrastructure/DependencyInjection.cs`) par la tâche qui le crée.

## Review Focus

1. **Recette incomplète dans les statistiques** : un plat dont le coût est incomplet ne doit jamais fausser la moyenne en silence. Il porte l'indicateur « incomplet » et est exclu du menu engineering.
   → test `Profitability_IncompleteDish_FlaggedAndExcludedFromMatrix` (Task 6).
2. **Ventes saisies à la semaine** : elles comptent dans le CA de la période qui contient leur lundi, mais n'apparaissent pas comme un pic au lundi dans le graphique par jour. Elles forment une série à part, « Saisies à la semaine ».
   → test `RevenueByDay_WeeklyTotalsSeparated` (Task 6).
3. **Sous-préparation dans la consommation théorique** : 1 plat utilisant 100 ml d'un fond qui produit 2000 ml à partir de 1 kg d'os donne 50 g d'os, divisé par le rendement.
   → test `Explode_ThroughPreparation_ProRata` (Task 1).
4. **Commandes annulées** : elles ne comptent ni dans le CA ni dans la consommation.
   → test `Sales_CancelledOrdersExcluded` (Task 6).
5. **Écart d'inventaire sans achats datés** : les achats manuels n'ont pas de quantité, donc seuls les tickets appliqués comptent comme achats. L'écran le dit, et un test vérifie que les lignes de tickets ignorées ne comptent pas.
   → test `Variance_IgnoredReceiptLinesNotPurchases` (Task 6).

---

### Task 1 : Domaine (Core) — entités ventes, commandes, pertes, inventaires ; décomposition des recettes

**Files:**
- Create : `src/ChefOS.Core/Sales/{SalesEntry,ServicePeriod,Order,OrderLine,OrderSource,OrderStatus,WasteEntry,WasteReason,StockCount,StockCountLine,StockCountStatus}.cs`, `src/ChefOS.Core/Costing/RecipeExplosion.cs`
- Test : `tests/ChefOS.Tests/Core/RecipeExplosionTests.cs`

**Interfaces:**
- Produces (toutes les entités sont des classes `ITenantScoped` avec `Id` et `RestaurantId`) :
  - `enum ServicePeriod { Journee, Midi, Soir }`.
  - `SalesEntry` : `RecipeId`, `Date` (jour UTC), `Service`, `IsWeeklyTotal` (bool), `Quantity` (decimal), `UnitPriceInclVat`, `VatRate`, `UpdatedAt`.
  - `enum OrderSource { Salle, Emporter, Telephone, Vitrine }` ; `enum OrderStatus { Nouvelle, EnPreparation, Prete, Servie, Annulee }`.
  - `Order` : `Number` (int, séquentiel par restaurant), `CreatedAt`, `Source`, `Status`, `TableOrCustomer?`, `Note?`, `List<OrderLine> Lines`, `StatusChangedAt`.
  - `OrderLine` : `OrderId`, `RecipeId`, `Quantity` (int), `UnitPriceInclVat`, `VatRate`, `Note?`.
  - `enum WasteReason { Perime, Casse, Erreur, Surproduction, Autre }`.
  - `WasteEntry` : `Date`, `IngredientId?`, `RecipeId?` (exactement un des deux), `Quantity`, `Unit` (MeasureUnit ; `Piece` = portions pour un plat), `Reason`, `Note?`, `CreatedAt`.
  - `enum StockCountStatus { Brouillon, Cloture }`.
  - `StockCount` : `Date`, `Status`, `Note?`, `ClosedAt?`, `List<StockCountLine> Lines`.
  - `StockCountLine` : `StockCountId`, `IngredientId`, `Quantity`, `Unit` (MeasureUnit), `BaseQuantity` (decimal, calculée à l'enregistrement).
  - `sealed class RecipeExplosion(IEnumerable<Recipe> recipes, IEnumerable<Ingredient> ingredients)` :
    - `ExplosionResult Explode(int recipeId, decimal quantity, MeasureUnit? unit = null)` : `quantity` est un nombre de portions pour un plat (`unit` null ou `Piece`), ou une quantité dans `unit` pour une préparation (convertie dans l'`OutputUnit` de la préparation).
    - `record ExplosionResult(IReadOnlyDictionary<int, decimal> GrossBaseQuantityByIngredient, IReadOnlyList<CostIssue> Issues)`.
    - Règle : pour chaque ligne ingrédient, `ToBase(qty × facteur, unit, ingredient.BaseUnit, pieceWeight) ÷ (YieldPercent/100)` ; une ligne de sous-préparation est convertie dans l'`OutputUnit` de la préparation, avec le facteur `(qty × facteur) ÷ OutputQuantity`, puis on descend récursivement.
    - Garde-fou contre les cycles. Les problèmes (unité incompatible, sortie invalide, élément inconnu, cycle) sont collectés dans `Issues` (`CostIssueKind` de l'étape 1) et la ligne concernée est ignorée.
    - Un plat (`Kind == Dish`) se décompose en portions, ce qui revient à multiplier ses lignes par `quantity`.

- [ ] **Step 1 : Écrire les tests qui échouent**

```csharp
[Fact] public void Explode_Dish_IngredientLinesWithYield()        // 2 portions × (130 g saumon, rendement 65) → 400 g
[Fact] public void Explode_ThroughPreparation_ProRata()           // Review Focus 3 : plat 100 ml fond ; fond 2000 ml ← 1 kg os (rendement 100) → 50 g os
[Fact] public void Explode_TwoLevels_AccumulatesSameIngredient()  // le beurre utilisé directement et via une sauce se cumule
[Fact] public void Explode_PreparationInItsOwnUnit()              // Explode(fond, 1, L) → 500 g os
[Fact] public void Explode_PieceIngredient()                      // 2 œufs (pièce) → 2
[Fact] public void Explode_IncompatibleUnit_ReportedAndSkipped()
[Fact] public void Explode_ZeroOutput_ReportedAndSkipped()
[Fact] public void Explode_Cycle_DoesNotLoop()
```

- [ ] **Step 2 : Lancer les tests → échec** · **Step 3 : Implémenter** · **Step 4 : Lancer → PASS**, suite complète comprise
- [ ] **Step 5 : Commit** `feat: domaine ventes, commandes, pertes, inventaires et décomposition des recettes`

---

### Task 2 : Calculs purs — prix à une date, menu engineering, écart d'inventaire

**Files:**
- Create : `src/ChefOS.Core/Sales/{PriceAsOf,MenuEngineering,InventoryVariance}.cs`
- Test : `tests/ChefOS.Tests/Core/{PriceAsOfTests,MenuEngineeringTests,InventoryVarianceTests}.cs`

**Interfaces:**
- Produces :
  - `static class PriceAsOf` :
    - `decimal? At(IEnumerable<IngredientPrice> history, DateTime dayUtc, decimal? currentPrice)` : renvoie la dernière entrée (par `Date`, puis par `Id`) dont `Date < dayUtc.Date.AddDays(1)` ; sans entrée, renvoie `currentPrice`.
    - `IReadOnlyList<Ingredient> WithPricesAt(IEnumerable<Ingredient> ingredients, ILookup<int, IngredientPrice> history, DateTime dayUtc)` : renvoie des **copies** des ingrédients dont `CurrentPricePerBaseUnit` est remplacé par le prix en vigueur. Les entités d'origine ne sont jamais modifiées.
  - `enum MenuClass { Etoile, VacheALait, Enigme, PoidsMort }` ; `record MenuItem(int RecipeId, decimal QuantitySold, decimal UnitMargin)` ; `record MenuClassification(int RecipeId, MenuClass Class, decimal PopularityShare, decimal UnitMargin)` ; `static class MenuEngineering { IReadOnlyList<MenuClassification> Classify(IReadOnlyList<MenuItem> items); }` :
    - entrées dont `QuantitySold > 0` uniquement ;
    - seuil de popularité = 0.70 × (1 / n) ;
    - seuil de marge = Σ(qty × marge) ÷ Σ qty ;
    - les comparaisons utilisent `>=` ;
    - liste vide → résultat vide.
  - `record VarianceLine(int IngredientId, decimal Opening, decimal Purchases, decimal Closing, decimal ActualUse, decimal TheoreticalUse, decimal VarianceQuantity, decimal? VarianceValue)` ; `static class InventoryVariance { IReadOnlyList<VarianceLine> Compute(IReadOnlyDictionary<int, decimal> opening, IReadOnlyDictionary<int, decimal> purchases, IReadOnlyDictionary<int, decimal> closing, IReadOnlyDictionary<int, decimal> theoretical, IReadOnlyDictionary<int, decimal?> priceAtClose); }` :
    - un ingrédient absent d'un dictionnaire compte pour 0 ;
    - on garde chaque ingrédient présent dans au moins un dictionnaire ;
    - `ActualUse = opening + purchases − closing` ;
    - `VarianceQuantity = ActualUse − TheoreticalUse` ;
    - `VarianceValue = VarianceQuantity × prix` (null si le prix est inconnu) ;
    - tri par `VarianceValue` décroissante, les null en dernier.

- [ ] **Step 1 : Écrire les tests qui échouent**

```csharp
[Fact] public void PriceAsOf_LatestEntryOnOrBeforeDay()      // entrées du 1er, 10 et 20 ; date du 15 → prix du 10
[Fact] public void PriceAsOf_SameDayLaterTimeCounts()        // une entrée à 18h le jour même compte
[Fact] public void PriceAsOf_NoEntryBefore_UsesCurrent()
[Fact] public void PriceAsOf_WithPricesAt_ReturnsCopies()    // l'original n'est pas modifié
[Fact] public void MenuEngineering_FourQuadrants()           // 4 plats aux chiffres choisis → une classe chacun
[Fact] public void MenuEngineering_ThresholdsInclusive()
[Fact] public void MenuEngineering_IgnoresUnsold()
[Fact] public void Variance_ComputesActualAndVariance()      // ouverture 10, achats 5, clôture 4, théorique 9 → réel 11, écart 2
[Fact] public void Variance_MissingKeysAreZero_UnknownPriceNull_SortedByValue()
```

- [ ] **Step 2 → échec** · **Step 3 : Implémenter** · **Step 4 → PASS**
- [ ] **Step 5 : Commit** `feat: prix à une date, menu engineering et écart d'inventaire`

---

### Task 3 : Persistance

**Files:**
- Create : `src/ChefOS.Infrastructure/Data/Configurations/{SalesEntry,Order,OrderLine,WasteEntry,StockCount,StockCountLine}Configuration.cs`, migration `AddSalesAndStock`
- Modify : `ChefOsDbContext.cs` (DbSets `SalesEntries`, `Orders`, `OrderLines`, `WasteEntries`, `StockCounts`, `StockCountLines` + filtres globaux), `tests/ChefOS.Tests/Infrastructure/TenantModelTests.cs` (seed des 6 types)

Règles de mapping :
- **Énumérations en texte** : `Service`, `Source`, `Status`, `Reason`, `Unit`.
- **Précisions** : constraintes globales, plus `OrderLine.UnitPriceInclVat` en `numeric(18,2)` et `VatRate` en `numeric(5,4)`.
- **Index uniques** :
  - `IX_SalesEntries_Key` sur (`RestaurantId`, `RecipeId`, `Date`, `Service`, `IsWeeklyTotal`) ;
  - `IX_Orders_Number` sur (`RestaurantId`, `Number`) ;
  - `IX_StockCountLines_Key` sur (`StockCountId`, `IngredientId`).
- **Contrainte** `CK_WasteEntry_OneTarget` : `("IngredientId" IS NULL) <> ("RecipeId" IS NULL)`.
- **Règles de suppression** :
  - `SalesEntry`→`Recipe` : `Cascade` ;
  - `OrderLine`→`Order` : `Cascade` ;
  - `OrderLine`→`Recipe` : `Restrict` (une commande garde ses plats) ;
  - `WasteEntry`→`Ingredient`/`Recipe` : `Cascade` ;
  - `StockCountLine`→`StockCount` : `Cascade` ;
  - `StockCountLine`→`Ingredient` : `Cascade` ;
  - chaque entité→`Restaurant` : `Cascade`, via `BelongsToRestaurant`.
- **Suppression d'un plat ou d'un ingrédient** : `RecipeService.DeleteAsync` et `IngredientService.DeleteAsync` doivent maintenant refuser aussi un plat présent dans des commandes (« Ce plat figure dans des commandes : archivez-le »). Ajouter le test correspondant. Les ventes saisies et les pertes partent en cascade, ce qui est acceptable puisque l'archivage reste la voie recommandée.

- [ ] **Step 1 :** Ajouter les DbSets sans filtre, puis lancer `TenantModelTests` et vérifier qu'il échoue (preuve que le garde-fou fonctionne).
- [ ] **Step 2 :** Ajouter les filtres, les configurations, le seed des tests et la migration. Vérifier la contrainte et les index dans `Up()`.
- [ ] **Step 3 :** Ajouter les tests `SalesEntry_DuplicateKey_Rejected`, `WasteEntry_BothTargets_Rejected`, `DeleteDishUsedInOrders_Refused` et `DeleteRestaurant_CascadesSalesAndStock`.
- [ ] **Step 4 :** Lancer la suite complète → PASS, 0 avertissement.
- [ ] **Step 5 : Commit** `feat: persistance des ventes, commandes, pertes et inventaires`

---

### Task 4 : `SalesService` et `OrderService`

**Files:**
- Create : `src/ChefOS.Infrastructure/Services/{SalesService,OrderService}.cs`, ajouts dans `Inputs.cs`
- Test : `tests/ChefOS.Tests/Infrastructure/{SalesServiceTests,OrderServiceTests}.cs`

**Interfaces:**
- Produces :
  - `record SalesCell(int RecipeId, DateTime Date, ServicePeriod Service, bool IsWeeklyTotal, decimal Quantity)`.
  - `record SalesWeek(DateTime Monday, IReadOnlyList<SalesWeekDish> Dishes)` ; `record SalesWeekDish(int RecipeId, string Name, decimal? UnitPriceInclVat, IReadOnlyList<SalesCell> Cells)`.
  - `class SalesService(IDbContextFactory<ChefOsDbContext> factory)` :
    - `Task<SalesWeek> GetWeekAsync(DateTime anyDayUtc)` : semaine du lundi au dimanche ; plats non archivés de type `Dish` triés par catégorie puis par nom, plus les plats archivés qui ont des ventes cette semaine-là.
    - `Task SaveCellsAsync(IReadOnlyList<SalesCell> cells)` :
      - par clé (plat, date du jour, service, hebdo), une quantité de 0 supprime l'entrée, une quantité > 0 crée ou met à jour l'entrée en figeant `SellingPriceInclVat` et `VatRate` du plat (un plat sans prix est refusé : « Le plat {nom} n'a pas de prix de vente ») ;
      - une quantité < 0 est refusée ;
      - une quantité non entière est acceptée (½ portion) ;
      - les plats doivent être de type Dish et appartenir au restaurant (« Plat introuvable ») ;
      - une cellule hebdomadaire doit être datée d'un lundi (« Un total de semaine doit être daté du lundi ») ;
      - la date est normalisée en jour UTC.
  - `record OrderLineInput(int RecipeId, int Quantity, string? Note)` ; `record OrderInput(OrderSource Source, string? TableOrCustomer, string? Note, IReadOnlyList<OrderLineInput> Lines)` ; `record OrderSummary(int Id, int Number, DateTime CreatedAt, OrderSource Source, OrderStatus Status, string? TableOrCustomer, decimal TotalInclVat, IReadOnlyList<OrderLineSummary> Lines)` ; `record OrderLineSummary(string DishName, int Quantity, string? Note)`.
  - `class OrderService(IDbContextFactory<ChefOsDbContext> factory)` :
    - `Task<int> CreateAsync(OrderInput input)` :
      - au moins une ligne (« Ajoutez au moins un plat ») ;
      - quantité ≥ 1 ;
      - plats de type Dish, non archivés, avec un prix ;
      - les prix sont figés ;
      - `Number` = max + 1 pour le restaurant, dans une transaction, avec une nouvelle tentative en cas de violation de `IX_Orders_Number` ;
      - statut `Nouvelle` ; `CreatedAt` et `StatusChangedAt` en UTC.
    - `Task UpdateLinesAsync(int orderId, IReadOnlyList<OrderLineInput> lines)` : refusé si la commande est `Servie` ou `Annulee`.
    - `Task AdvanceAsync(int orderId)` : `Nouvelle → EnPreparation → Prete → Servie`. Refusé au-delà, et sur une commande annulée.
    - `Task CancelAsync(int orderId)` : refusé si la commande est `Servie`.
    - `Task<IReadOnlyList<OrderSummary>> ListForDayAsync(DateTime dayUtc)` : commandes créées ce jour-là, triées par numéro.
    - Commande inconnue ou d'un autre restaurant : « Commande introuvable ».

- [ ] **Step 1 : Tests qui échouent**

```csharp
// SalesServiceTests
[Fact] GetWeek_ListsDishesAndExistingCells()
[Fact] SaveCells_UpsertsFreezesPriceAndDeletesZeros()
[Fact] SaveCells_WeeklyTotalMustBeMonday()
[Fact] SaveCells_NegativeRefused() / SaveCells_DishWithoutPriceRefused() / SaveCells_ForeignDishRefused()
[Fact] SaveCells_LaterPriceChange_DoesNotAlterSavedEntry()
// OrderServiceTests
[Fact] Create_NumbersSequentiallyPerRestaurant()            // A : 1, 2 ; B : 1
[Fact] Create_FreezesPricesAndValidates()
[Fact] Advance_FollowsLifecycle_ThenRefused()
[Fact] Cancel_RefusedWhenServed() / UpdateLines_RefusedWhenCancelled()
[Fact] Create_Concurrent_NoDuplicateNumbers()               // 5 créations en parallèle → numéros 1 à 5
[Fact] OtherRestaurant_CannotSeeOrModify()
```

- [ ] **Step 2 → échec** · **Step 3 : Implémenter** · **Step 4 → PASS**
- [ ] **Step 5 : Commit** `feat: services des ventes saisies et des commandes`

---

### Task 5 : `WasteService` et `InventoryService`

**Files:**
- Create : `src/ChefOS.Infrastructure/Services/{WasteService,InventoryService}.cs`, ajouts dans `Inputs.cs`
- Test : `tests/ChefOS.Tests/Infrastructure/{WasteServiceTests,InventoryServiceTests}.cs`

**Interfaces:**
- Produces :
  - `record WasteInput(DateTime Date, int? IngredientId, int? RecipeId, decimal Quantity, MeasureUnit Unit, WasteReason Reason, string? Note)` ; `record WasteItem(int Id, DateTime Date, string ItemName, bool IsRecipe, decimal Quantity, MeasureUnit Unit, WasteReason Reason, string? Note, decimal? Cost, bool CostIncomplete)`.
  - `class WasteService(IDbContextFactory<ChefOsDbContext> factory)` :
    - `Task<int> AddAsync(WasteInput)` :
      - exactement une cible (« Choisissez un ingrédient ou une recette ») ;
      - quantité > 0 ;
      - date non future, avec la même règle qu'aux tickets (au plus aujourd'hui + 1 jour) ;
      - unité compatible, vérifiée par `UnitConverter` pour un ingrédient ; pour un plat, unité `Piece` uniquement ; pour une préparation, conversion dans son `OutputUnit` (« Unité incompatible ») ;
      - la cible appartient au restaurant.
    - `Task DeleteAsync(int id)`.
    - `Task<IReadOnlyList<WasteItem>> ListAsync(DateTime fromDay, DateTime toDay)` :
      - coût aux prix en vigueur à la date de la perte ;
      - pour un ingrédient : prix × quantité en unité de base, sans rendement (on jette du produit tel qu'acheté) ;
      - pour une recette : `RecipeBook` construit sur `PriceAsOf.WithPricesAt`, coût par portion (plat) ou par unité produite (préparation) × quantité ; `CostIncomplete` si le coût est incomplet ;
      - prix inconnu → `Cost` null ;
      - tri par date décroissante.
  - `record StockCountSummary(int Id, DateTime Date, StockCountStatus Status, int LineCount, decimal? Value)` ; `record StockCountLineInput(int IngredientId, decimal Quantity, MeasureUnit Unit)` ; `record StockCountSheet(int Id, DateTime Date, StockCountStatus Status, string? Note, IReadOnlyList<StockCountSheetLine> Lines)` ; `record StockCountSheetLine(int IngredientId, string Name, string Category, BaseUnit BaseUnit, decimal? Quantity, MeasureUnit? Unit)`.
  - `class InventoryService(IDbContextFactory<ChefOsDbContext> factory)` :
    - `Task<int> CreateAsync(DateTime dayUtc, string? note)` : brouillon, date non future.
    - `Task<StockCountSheet> GetSheetAsync(int id)` : tous les ingrédients non archivés, plus les archivés déjà comptés ; triés par catégorie puis par nom, avec leurs valeurs saisies.
    - `Task SaveLinesAsync(int id, IReadOnlyList<StockCountLineInput> lines)` :
      - refusé si l'inventaire est clôturé (« Inventaire clôturé ») ;
      - une quantité null ou absente supprime la ligne, une quantité < 0 est refusée ;
      - `BaseQuantity` vient de `UnitConverter.ToBase` (« Unité incompatible pour {ingrédient} ») ;
      - les ingrédients doivent appartenir au restaurant.
    - `Task CloseAsync(int id)` : passe le statut à `Cloture` avec `ClosedAt` ; refusé si aucune ligne n'est saisie.
    - `Task DeleteAsync(int id)` : brouillon uniquement.
    - `Task<IReadOnlyList<StockCountSummary>> ListAsync()` : tri par date décroissante ; la valeur vaut Σ quantité de base × prix en vigueur à la date de l'inventaire.

- [ ] **Step 1 : Tests qui échouent** `Waste_Add_ValidatesTargetUnitDate`, `Waste_List_CostsIngredientAtDatePrice_NoYield`, `Waste_List_DishCostIsRecipeCostTimesPortions`, `Waste_List_IncompleteRecipeFlagged`, `Inventory_SaveLines_ConvertsToBase`, `Inventory_Close_ThenReadOnly`, `Inventory_Close_EmptyRefused`, `Inventory_Value_UsesPriceAtDate`, `OtherRestaurant_CannotSeeOrModify` (pertes et inventaires).
- [ ] **Step 2 → échec** · **Step 3 : Implémenter** · **Step 4 → PASS**
- [ ] **Step 5 : Commit** `feat: services des pertes et des inventaires`

---

### Task 6 : `StatsService`

**Files:**
- Create : `src/ChefOS.Infrastructure/Services/StatsService.cs`, ajouts dans `Inputs.cs`
- Test : `tests/ChefOS.Tests/Infrastructure/StatsServiceTests.cs`

**Interfaces:**
- Consumes : `RecipeBook`, `RecipeExplosion`, `PriceAsOf`, `MenuEngineering`, `InventoryVariance`.
- Produces :
  - `record DishProfit(int RecipeId, string Name, decimal QuantitySold, decimal QuantityFromEntries, decimal QuantityFromOrders, decimal RevenueExclVat, decimal? UnitCost, decimal? TotalCost, decimal? Margin, decimal? FoodCostPercent, bool CostIncomplete, MenuClass? Class)`.
  - `record DayRevenue(DateTime Day, decimal FromEntries, decimal FromOrders)`.
  - `record WasteByReason(WasteReason Reason, decimal Cost, int Count)`.
  - `record VarianceReport(DateTime OpeningDate, DateTime ClosingDate, IReadOnlyList<VarianceRow> Rows, decimal TotalVarianceValue)` ; `record VarianceRow(int IngredientId, string Name, BaseUnit BaseUnit, decimal Opening, decimal Purchases, decimal Closing, decimal ActualUse, decimal TheoreticalUse, decimal VarianceQuantity, decimal? VarianceValue)`.
  - `record PeriodStats(DateTime From, DateTime To, decimal RevenueExclVat, decimal RevenueFromEntries, decimal RevenueFromOrders, decimal WeeklyTotalsRevenue, decimal TheoreticalCost, bool CostIncomplete, decimal GrossMargin, decimal? TheoreticalFoodCost, decimal WasteCost, decimal? ActualFoodCost, IReadOnlyList<DayRevenue> RevenueByDay, IReadOnlyList<DishProfit> Dishes, IReadOnlyList<WasteByReason> Waste, VarianceReport? Variance)`.
  - `class StatsService(IDbContextFactory<ChefOsDbContext> factory)` : `Task<PeriodStats> GetAsync(DateTime fromDay, DateTime toDay)`. Règles :
    - **Ventes de la période** : `SalesEntry` avec `Date` dans [from, to], plus les lignes des commandes non annulées dont `CreatedAt` tombe dans [from 00:00, to + 1 jour 00:00[. CA HT = Σ qty × prix figé ÷ (1 + TVA figée).
    - **Coûts** : prix en vigueur au jour `to` (`PriceAsOf.WithPricesAt`), puis `RecipeBook.Cost(recipeId)`. Coût unitaire = `TotalCost`. Un plat dont le coût est incomplet porte `CostIncomplete`, et son coût entre quand même dans le total, qui porte alors `CostIncomplete = true`.
    - `GrossMargin` = CA HT − coût théorique. `TheoreticalFoodCost` = coût ÷ CA (null si CA = 0).
    - **Menu engineering** : `MenuEngineering.Classify` sur les plats vendus et complets ; `Class` reste null pour les autres.
    - **CA par jour** : chaque jour de [from, to] apparaît, même à 0. Les entrées hebdomadaires sont **exclues** de `RevenueByDay` et totalisées dans `WeeklyTotalsRevenue`.
    - **Pertes** : coût de chaque perte de la période au prix de sa date (même règle que `WasteService`), regroupé par raison. `WasteCost` = total.
    - **Écart d'inventaire** :
      1. Prendre l'inventaire clôturé A le plus récent avec `Date` ≤ from, et le premier B avec `Date` ≥ to. Sans les deux, `Variance` = null et `ActualFoodCost` = null.
      2. Achats = lignes de tickets `Applied`, non ignorées, avec ingrédient, dont `ReceiptDate` ∈ ]A.Date, B.Date]. Quantité de base via `ReceiptLinePricing.Compute(...).BaseQuantity`.
      3. Consommation théorique = décomposition des ventes de ]A.Date, B.Date] (pas seulement de la période) et des pertes de ce même intervalle.
      4. `InventoryVariance.Compute` avec les prix en vigueur à B.Date, puis jointure des noms et unités.
      5. `ActualFoodCost` = Σ (consommation réelle × prix à B.Date) ÷ CA HT de ]A.Date, B.Date]. Il est indépendant de la période choisie, et l'écran l'indique avec les dates A–B.

- [ ] **Step 1 : Tests qui échouent** (dates fixes)

```csharp
[Fact] Revenue_SumsEntriesAndOrders_WithSourcesSplit()
[Fact] Sales_CancelledOrdersExcluded()                       // Review Focus 4
[Fact] RevenueByDay_WeeklyTotalsSeparated()                  // Review Focus 2
[Fact] Costs_UsePricesInForceAtPeriodEnd()                   // prix modifié après la fin de la période → ignoré
[Fact] Profitability_IncompleteDish_FlaggedAndExcludedFromMatrix()   // Review Focus 1
[Fact] MenuEngineering_ClassesAssigned()
[Fact] Waste_GroupedByReasonAtDatePrices()
[Fact] Variance_BetweenTwoClosedCounts()                     // ouverture, ticket appliqué, ventes, perte, clôture → écart attendu exact pour un ingrédient
[Fact] Variance_IgnoredReceiptLinesNotPurchases()            // Review Focus 5
[Fact] Variance_NullWithoutBracketingCounts()
[Fact] OtherRestaurant_DataNeverCounted()
```

- [ ] **Step 2 → échec** · **Step 3 : Implémenter.** Charger en une fois les ingrédients, l'historique (`ToLookup`), les recettes et les lignes ; aucune requête par plat. · **Step 4 → PASS**
- [ ] **Step 5 : Commit** `feat: statistiques de période (CA, marges, menu engineering, pertes, écart d'inventaire)`

---

### Task 7 : Écrans ventes, commandes, pertes, inventaires

**Files:**
- Create : `src/ChefOS.Back/Components/Pages/Sales/SalesWeek.razor` (+ `SalesGridForm.cs`), `Pages/Orders/{OrderBoard,OrderEditor}.razor`, `Pages/Waste/WasteList.razor` (+ dialogue d'ajout), `Pages/Inventory/{InventoryList,InventorySheet}.razor` (+ `InventorySheetForm.cs`)
- Modify : `Components/Layout/NavMenu.razor`, avec la section « Activité » : Ventes, Commandes, Pertes, Inventaires, Statistiques (lien `/stats` pour la Task 8)
- Test : `tests/ChefOS.Tests/Back/{SalesGridFormTests,InventorySheetFormTests}.cs`

Écrans (spec §5) :
- **`/sales`** :
  - semaine précédente / suivante, plus un sélecteur de date ;
  - une ligne par plat avec 7 jours + « Total semaine » (`MudNumericField` avec Culture), et un commutateur « Détail midi/soir » qui affiche deux sous-lignes ;
  - totaux par colonne et par ligne, CA HT de la semaine (calcul local) ;
  - « Enregistrer » envoie seulement les cellules modifiées ;
  - un bandeau rappelle : « Ventes faites hors ChefOS (votre caisse). Les commandes prises dans ChefOS sont comptées automatiquement. »
  - `SalesGridForm` porte l'état de la grille, le suivi des cellules modifiées et les totaux ; il est testé.
- **`/orders`** :
  - colonnes Nouvelle / En préparation / Prête / Servie (du jour), plus Annulées repliées ;
  - chaque carte affiche le n°, l'heure, la source, la table ou le client, les lignes, le total TTC et un bouton « Avancer » (libellé selon l'étape) ;
  - menu : modifier (si permis), annuler avec confirmation ;
  - « Nouvelle commande » ouvre `/orders/new` : source, table ou nom, autocomplétion des plats avec quantité, note ;
  - rafraîchissement manuel plus un timer de 30 s pendant que la page est ouverte (libéré au dispose).
- **`/waste`** :
  - période (7 jours par défaut) ;
  - tableau date / élément / quantité / raison / coût, avec « incomplet » si besoin ;
  - total ;
  - dialogue d'ajout : ingrédient ou recette (autocomplétion groupée), quantité, unité, raison, note, date (max aujourd'hui + 1).
- **`/inventory`** :
  - liste avec date, statut, nombre de lignes et valeur ;
  - « Nouvel inventaire » (date) ouvre `/inventory/{id}` ;
  - feuille groupée par catégorie avec quantité (Culture) et unité, dont l'unité par défaut correspond à la base de l'ingrédient (kg pour gramme, l pour ml, pièce) ;
  - « Enregistrer », « Clôturer » avec confirmation ; un inventaire clôturé est en lecture seule ;
  - `InventorySheetForm` est testé.
- **Partout** : les `DomainValidationException` s'affichent en snackbar, avec une garde `_busy`, et un id introuvable affiche « Élément introuvable ».

- [ ] **Step 1 : Tests des formulaires (échec d'abord)** · **Step 2 : Pages + navigation** · **Step 3 : Build sans avertissement + `dotnet test`**
- [ ] **Step 4 : Vérification manuelle** (compte de démo) : saisir une semaine, prendre une commande et la faire avancer jusqu'à « Servie », déclarer une perte, faire puis clôturer un inventaire. Nettoyer ensuite.
- [ ] **Step 5 : Commit** `feat: écrans ventes, commandes, pertes et inventaires`

---

### Task 8 : Statistiques, tableau de bord, données de démo

**Files:**
- Create : `src/ChefOS.Back/Components/Pages/Stats/StatsPage.razor` (+ `StatsPeriod.cs` pour les raccourcis de période)
- Modify : `Components/Pages/Dashboard.razor`, `src/ChefOS.Infrastructure/Seeding/DemoSeeder.cs`, `DashboardService` (CA HT et marge brute sur 7 jours, via `StatsService`), `README.md` (section « Ventes et statistiques »)
- Test : `tests/ChefOS.Tests/Back/StatsPeriodTests.cs`, `DemoSeederTests` (étendu)

- **`/stats`** :
  - raccourcis de période (cette semaine, semaine dernière, ce mois, mois dernier, ou personnalisée avec deux dates) ;
  - KPI : CA HT (détail saisies / commandes), coût théorique, marge brute, food cost théorique, pertes, food cost réel avec « entre inventaires du {A} et du {B} », sinon « — » et une aide ;
  - `MudChart` en barres empilées du CA par jour (saisies / commandes), plus une mention « + {montant} saisis à la semaine » si `WeeklyTotalsRevenue > 0` ;
  - tableau par plat triable : quantités, CA HT, coût, marge, food cost (`FoodCostChip` ou pastille équivalente), classe, incomplet ;
  - matrice 2×2 du menu engineering avec les noms des plats dans chaque quadrant et un conseil d'une ligne par quadrant :
    - Étoile : « À garder tel quel » ;
    - Vache à lait : « Revoir le coût ou le prix » ;
    - Énigme : « À mettre en avant » ;
    - Poids mort : « À remplacer » ;
  - pertes par raison ;
  - tableau de l'écart d'inventaire : les 15 premiers écarts, une ligne de total, et la note « Seuls les achats passés par un ticket validé sont comptés ».
- **Tableau de bord** : KPI « CA HT (7 jours) » et « Marge brute (7 jours) », avec un lien vers `/stats`.
- **`DemoSeeder`** (idempotent, toujours dans la même transaction de seed) :
  - 8 semaines de ventes saisies au jour, les 8 semaines avant aujourd'hui, avec des volumes réalistes et variés pour que les quatre quadrants soient peuplés ;
  - une quinzaine de commandes sur les 7 derniers jours, dont une annulée ;
  - 6 pertes ;
  - 2 inventaires clôturés, à J-28 et J-7, encadrant au moins un ticket `Applied` (lignes cohérentes, fournisseur Metro) ;
  - les valeurs donnent un écart d'inventaire positif plausible sur 2 ou 3 ingrédients ;
  - mettre à jour `DemoSeederTests` en conséquence : les 4 quadrants sont non vides sur la période des 4 dernières semaines et l'écart d'inventaire n'est pas null.

- [ ] **Step 1 : Tests `StatsPeriod` et seeder (échec d'abord)** · **Step 2 : Implémenter** · **Step 3 : Build sans avertissement + suite complète**
- [ ] **Step 4 : Vérification manuelle sur une base vide** (`docker compose down -v && up -d`, démarrage) : `/stats` sur les 4 dernières semaines est rempli (KPI, graphique, matrice, écart) et le tableau de bord affiche les nouveaux KPI. Arrêter l'app.
- [ ] **Step 5 : Commit** `feat: page statistiques, KPI du tableau de bord et données de démo`
