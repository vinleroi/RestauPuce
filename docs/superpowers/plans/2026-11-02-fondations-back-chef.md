# ChefOS Étape 1 — Fondations + Back chef : plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Livrer une solution .NET 10 multi-restaurants où un chef crée un compte et un restaurant, saisit ses ingrédients (avec rendement) et ses recettes (avec sous-préparations), et voit en direct le coût, le food cost et la marge de chaque plat.

**Architecture:** `ChefOS.Core` contient les entités et toute la logique de calcul, sans aucune dépendance. `ChefOS.Infrastructure` contient le `ChefOsDbContext` (PostgreSQL) avec un filtre global multi-restaurant et les services applicatifs. `ChefOS.Back` (Blazor Interactive Server + MudBlazor + Identity) consomme ces services. `ChefOS.Vitrine` n'est qu'un squelette branché sur la même base.

**Tech Stack:** .NET 10, Blazor Web App, MudBlazor, EF Core 10 + Npgsql, ASP.NET Core Identity, PostgreSQL 17 (Docker), xUnit, Testcontainers.PostgreSql.

**Spec:** `docs/superpowers/specs/2026-11-02-fondations-back-chef-design.md`

## Global Constraints

- Racine du dépôt : `/Users/ahadji/Desktop/ChefOS` ; projets dans `src/`, tests dans `tests/`.
- `TargetFramework` : `net10.0` pour tous les projets ; `<Nullable>enable</Nullable>` et `<ImplicitUsings>enable</ImplicitUsings>`.
- Paquets NuGet : ajoutés avec `dotnet add package <nom>` sans version (dernière stable compatible net10).
- `ChefOS.Core` ne référence **aucun** paquet (ni EF Core, ni ASP.NET).
- PostgreSQL de dev : image `postgres:17-alpine`, port hôte **5433** (le 5432 peut être pris par le Postgres Homebrew), base `chefos`, utilisateur `chefos`, mot de passe `chefos_dev`.
- Chaîne de connexion de dev (`ConnectionStrings:ChefOs`) : `Host=localhost;Port=5433;Database=chefos;Username=chefos;Password=chefos_dev`.
- Interface en **français** ; culture `fr-BE` (montants `12,60 €`, saisie décimale avec virgule).
- Taux stockés en **fraction** : TVA `0.12m` / `0.21m`, seuil d'alerte `0.35m`, food cost `0.336m`. Affichage en %.
- Précision des colonnes : prix par unité de base `numeric(18,6)` ; montants en euros `numeric(18,2)` ; quantités `numeric(18,4)` ; `YieldPercent` `numeric(5,2)` ; taux `numeric(5,4)`.
- Les calculs se font en `decimal` non arrondi ; l'arrondi n'intervient qu'à l'affichage.
- Messages de commit au format du projet (`feat:` / `test:` / `docs:` …) terminés par `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Les tests d'intégration nécessitent Docker démarré.

## Review Focus

1. **Saisie décimale avec virgule** (`2,5` kg, `12,50` €) dans les champs MudBlazor : la valeur doit être acceptée telle quelle en `fr-BE`, sans être tronquée ni rejetée. → vérification manuelle Task 10, étape 5.
2. **Changement de prix d'un ingrédient utilisé dans une sous-préparation** : le coût du plat qui utilise cette préparation doit changer au rechargement. → test `PriceChange_PropagatesThroughPreparation` Task 7.
3. **Préparation avec `OutputQuantity` nulle ou à 0** : aucune division par zéro ; le plat qui l'utilise est marqué « coût incomplet ». → test `Preparation_WithZeroOutput_IsIncompleteNotCrash` Task 3.
4. **Ligne de recette à quantité ≤ 0** : l'enregistrement est refusé avec un message clair. → test `Save_RejectsNonPositiveQuantity` Task 7.
5. **Deux restaurants portant le même nom** : le second reçoit le slug `nom-2`, sans erreur d'unicité. → test `Create_SameName_GetsSuffixedSlug` Task 5.

---

## Structure des fichiers

```
ChefOS/
├── ChefOS.sln
├── docker-compose.yml
├── .config/dotnet-tools.json                  (dotnet-ef)
├── README.md
├── src/
│   ├── ChefOS.Core/
│   │   ├── Common/DomainValidationException.cs
│   │   ├── Common/SlugGenerator.cs
│   │   ├── Restaurants/{Restaurant,Membership,MembershipRole,ITenantScoped}.cs
│   │   ├── Ingredients/{Ingredient,IngredientPrice,PriceSource,Supplier,EuAllergen}.cs
│   │   ├── Recipes/{Recipe,RecipeKind,RecipeLine}.cs
│   │   ├── Units/{BaseUnit,MeasureUnit,UnitConverter,ConversionResult}.cs
│   │   └── Costing/{RecipeBook,CostResult,CostIssue,CycleInfo,VatCalculator,FoodCostBadge}.cs
│   ├── ChefOS.Infrastructure/
│   │   ├── Identity/ApplicationUser.cs
│   │   ├── Data/{ChefOsDbContext,ITenantContext,FixedTenantContext,DesignTimeFactory}.cs
│   │   ├── Data/Configurations/*.cs               (une IEntityTypeConfiguration par entité)
│   │   ├── Data/Migrations/
│   │   ├── Services/{RestaurantService,SupplierService,IngredientService,RecipeService,DashboardService}.cs
│   │   ├── Services/Inputs.cs                     (records d'entrée et de sortie des services)
│   │   ├── Seeding/{DemoSeeder,DemoOptions}.cs
│   │   └── DependencyInjection.cs
│   ├── ChefOS.Back/                               (généré par le template blazor + Identity)
│   │   ├── Tenancy/{ClaimsTenantContext,ChefOsClaimsPrincipalFactory,ActiveRestaurantEndpoint}.cs
│   │   ├── Components/Layout/{MainLayout,NavMenu,RestaurantSelector}.razor
│   │   ├── Components/Pages/{Dashboard,Onboarding,Settings}.razor
│   │   ├── Components/Pages/Ingredients/{IngredientList,IngredientDetail}.razor
│   │   ├── Components/Pages/Recipes/{RecipeList,RecipeEditor}.razor
│   │   ├── Components/Pages/Suppliers/SupplierList.razor
│   │   └── Components/Shared/{FoodCostChip,AllergenChips}.razor
│   └── ChefOS.Vitrine/
└── tests/ChefOS.Tests/
    ├── Core/{UnitConverterTests,SlugGeneratorTests,RecipeBookTests,FoodCostBadgeTests}.cs
    ├── Infrastructure/{PostgresFixture,TestContextFactory,TenantIsolationTests,RestaurantServiceTests,IngredientServiceTests,RecipeServiceTests,DashboardServiceTests,DemoSeederTests}.cs
    └── SmokeTests.cs
```

---

### Task 1 : Squelette de la solution

**Files:**
- Create: `ChefOS.sln`, `.gitignore`, `docker-compose.yml`, les 4 projets `src/*`, `tests/ChefOS.Tests`, `tests/ChefOS.Tests/SmokeTests.cs`

**Interfaces:**
- Produces : la solution compile ; `docker compose up -d` lance PostgreSQL sur le port 5433.

- [ ] **Step 1 : Générer la solution et les projets**

```bash
cd /Users/ahadji/Desktop/ChefOS
dotnet new sln -n ChefOS
dotnet new gitignore
dotnet new classlib -n ChefOS.Core -o src/ChefOS.Core -f net10.0
dotnet new classlib -n ChefOS.Infrastructure -o src/ChefOS.Infrastructure -f net10.0
dotnet new blazor -n ChefOS.Back -o src/ChefOS.Back -f net10.0 --interactivity Server --all-interactive --auth Individual
dotnet new blazor -n ChefOS.Vitrine -o src/ChefOS.Vitrine -f net10.0 --interactivity None --empty
dotnet new xunit -n ChefOS.Tests -o tests/ChefOS.Tests -f net10.0
dotnet sln add src/*/*.csproj tests/ChefOS.Tests/ChefOS.Tests.csproj
dotnet add src/ChefOS.Infrastructure reference src/ChefOS.Core
dotnet add src/ChefOS.Back reference src/ChefOS.Core src/ChefOS.Infrastructure
dotnet add src/ChefOS.Vitrine reference src/ChefOS.Core src/ChefOS.Infrastructure
dotnet add tests/ChefOS.Tests reference src/ChefOS.Core src/ChefOS.Infrastructure
```

Supprimer `Class1.cs` dans Core et Infrastructure. Le dossier `Data/` et les migrations SQLite générés dans `ChefOS.Back` restent en place jusqu'à la Task 9.

- [ ] **Step 2 : Écrire `docker-compose.yml`**

Service `db` : image `postgres:17-alpine`, `POSTGRES_DB=chefos`, `POSTGRES_USER=chefos`, `POSTGRES_PASSWORD=chefos_dev`, port `5433:5432`, volume nommé `chefos-pg`.

- [ ] **Step 3 : Écrire le test de fumée**

```csharp
public class SmokeTests
{
    [Fact]
    public void Solution_Builds_And_CoreAssembly_Loads()
        => Assert.Equal("ChefOS.Core", typeof(ChefOS.Core.Common.DomainValidationException).Assembly.GetName().Name);
}
```

Créer `src/ChefOS.Core/Common/DomainValidationException.cs` : `public sealed class DomainValidationException(string message) : Exception(message);`

- [ ] **Step 4 : Vérifier**

Run : `dotnet build && dotnet test && docker compose up -d && docker compose ps`
Expected : build sans erreur, `Passed: 1`, le service `db` est `running`.

- [ ] **Step 5 : Commit**

```bash
git add -A && git commit -m "feat: squelette de la solution ChefOS (.NET 10, 4 projets, Postgres Docker)"
```

---

### Task 2 : Entités du domaine, conversion d'unités et slugs

**Files:**
- Create : tous les fichiers de `src/ChefOS.Core/{Restaurants,Ingredients,Recipes,Units}/`, `src/ChefOS.Core/Common/SlugGenerator.cs`
- Test : `tests/ChefOS.Tests/Core/UnitConverterTests.cs`, `tests/ChefOS.Tests/Core/SlugGeneratorTests.cs`

**Interfaces:**
- Produces :
  - `interface ITenantScoped { int RestaurantId { get; set; } }`
  - `Restaurant` : `Id`, `Name`, `Slug`, `Address?`, `VatNumber?`, `DefaultFoodVatRate = 0.12m`, `DefaultDrinkVatRate = 0.21m`, `FoodCostAlertThreshold = 0.35m`, `CreatedAt`.
  - `Membership` : `UserId` (string), `RestaurantId`, `Restaurant?`, `Role` (`MembershipRole { Owner, Chef, Staff }`).
  - `[Flags] enum EuAllergen` : `None=0, Gluten=1, Crustaceans=2, Eggs=4, Fish=8, Peanuts=16, Soy=32, Milk=64, Nuts=128, Celery=256, Mustard=512, Sesame=1024, Sulphites=2048, Lupin=4096, Molluscs=8192`.
  - `enum BaseUnit { Gram, Milliliter, Piece }` ; `enum MeasureUnit { G, Kg, Ml, Cl, L, Piece }` ; `enum PriceSource { Manual, Receipt }` ; `enum RecipeKind { Dish, Preparation }`.
  - `Supplier : ITenantScoped` : `Id`, `RestaurantId`, `Name`, `ContactName?`, `Email?`, `Phone?`.
  - `Ingredient : ITenantScoped` : `Id`, `RestaurantId`, `Name`, `Category` (string, défaut `"Divers"`), `Allergens` (EuAllergen), `BaseUnit`, `CurrentPricePerBaseUnit` (decimal?), `YieldPercent` (défaut `100m`), `AveragePieceWeightGrams` (decimal?), `SupplierId?`, `Supplier?`, `IsArchived`.
  - `IngredientPrice : ITenantScoped` : `Id`, `RestaurantId`, `IngredientId`, `Date` (DateTime UTC), `PricePerBaseUnit`, `SupplierId?`, `Source`.
  - `Recipe : ITenantScoped` : `Id`, `RestaurantId`, `Name`, `Kind`, `Category?`, `SellingPriceInclVat` (decimal?), `VatRate` (decimal?), `OutputQuantity` (decimal?, en unité de base), `OutputUnit` (BaseUnit?), `Instructions?`, `IsArchived`, `List<RecipeLine> Lines`.
  - `RecipeLine : ITenantScoped` : `Id`, `RestaurantId`, `RecipeId`, `IngredientId?`, `Ingredient?`, `SubRecipeId?`, `SubRecipe?`, `Quantity`, `Unit` (MeasureUnit). (`RecipeLine` est `ITenantScoped` pour pouvoir lui appliquer le filtre global, conformément à la spec §4 « toute entité métier ».)
  - `readonly record struct ConversionResult(bool Success, decimal Value)`.
  - `static class UnitConverter` :
    - `ConversionResult ToBase(decimal quantity, MeasureUnit unit, BaseUnit target, decimal? averagePieceWeightGrams)`
    - `decimal PricePerBaseFromDisplay(decimal pricePerDisplayUnit, BaseUnit baseUnit)` : €/kg → €/g et €/l → €/ml (÷ 1000) ; pièce inchangée.
    - `decimal PriceDisplayFromBase(decimal pricePerBaseUnit, BaseUnit baseUnit)` : l'inverse.
    - `string DisplayUnitLabel(BaseUnit baseUnit)` : `"kg"`, `"l"`, `"pièce"`.
  - `static class SlugGenerator { string FromName(string name); }`

- [ ] **Step 1 : Écrire les tests qui échouent**

```csharp
public class UnitConverterTests
{
    [Theory]
    [InlineData(2, MeasureUnit.Kg, BaseUnit.Gram, null, 2000)]
    [InlineData(250, MeasureUnit.G, BaseUnit.Gram, null, 250)]
    [InlineData(25, MeasureUnit.Cl, BaseUnit.Milliliter, null, 250)]
    [InlineData(1.5, MeasureUnit.L, BaseUnit.Milliliter, null, 1500)]
    [InlineData(3, MeasureUnit.Piece, BaseUnit.Gram, 60, 180)]
    [InlineData(120, MeasureUnit.G, BaseUnit.Piece, 60, 2)]
    [InlineData(0.24, MeasureUnit.Kg, BaseUnit.Piece, 60, 4)]
    [InlineData(2, MeasureUnit.Piece, BaseUnit.Piece, null, 2)]
    public void ToBase_ValidConversions(double qty, MeasureUnit unit, BaseUnit target, double? pieceWeight, double expected)
    {
        var r = UnitConverter.ToBase((decimal)qty, unit, target, (decimal?)pieceWeight);
        Assert.True(r.Success);
        Assert.Equal((decimal)expected, r.Value);
    }

    [Theory]
    [InlineData(2, MeasureUnit.Piece, BaseUnit.Gram, null)]
    [InlineData(100, MeasureUnit.G, BaseUnit.Piece, null)]
    [InlineData(100, MeasureUnit.G, BaseUnit.Milliliter, null)]
    [InlineData(5, MeasureUnit.Ml, BaseUnit.Gram, null)]
    [InlineData(1, MeasureUnit.L, BaseUnit.Piece, 60)]
    public void ToBase_IncompatibleConversions_Fail(double qty, MeasureUnit unit, BaseUnit target, double? pieceWeight)
        => Assert.False(UnitConverter.ToBase((decimal)qty, unit, target, (decimal?)pieceWeight).Success);

    [Fact]
    public void Price_DisplayRoundTrip()
    {
        Assert.Equal(0.0125m, UnitConverter.PricePerBaseFromDisplay(12.50m, BaseUnit.Gram));
        Assert.Equal(12.50m, UnitConverter.PriceDisplayFromBase(0.0125m, BaseUnit.Gram));
        Assert.Equal(0.35m, UnitConverter.PricePerBaseFromDisplay(0.35m, BaseUnit.Piece));
        Assert.Equal("kg", UnitConverter.DisplayUnitLabel(BaseUnit.Gram));
    }
}

public class SlugGeneratorTests
{
    [Theory]
    [InlineData("Le Petit Bistro", "le-petit-bistro")]
    [InlineData("  Chez Hélène & Fils !! ", "chez-helene-fils")]
    [InlineData("L'Écailler du Palais", "l-ecailler-du-palais")]
    public void FromName(string name, string expected) => Assert.Equal(expected, SlugGenerator.FromName(name));
}
```

- [ ] **Step 2 : Lancer les tests pour vérifier l'échec**

Run : `dotnet test --filter "UnitConverterTests|SlugGeneratorTests"`
Expected : erreurs de compilation (types inexistants).

- [ ] **Step 3 : Créer les entités et enums listés dans Interfaces, puis implémenter `UnitConverter` et `SlugGenerator`**

`SlugGenerator` : minuscules, suppression des diacritiques (`NormalizationForm.FormD` + retrait des `NonSpacingMark`), tout caractère non `[a-z0-9]` devient `-`, puis fusion des `-` consécutifs et retrait aux extrémités.

- [ ] **Step 4 : Lancer les tests**

Run : `dotnet test --filter "UnitConverterTests|SlugGeneratorTests"`
Expected : tout PASS.

- [ ] **Step 5 : Commit**

```bash
git add -A && git commit -m "feat: entités du domaine, conversion d'unités et génération de slug"
```

---

### Task 3 : Calcul des coûts (`RecipeBook`), TVA, pastilles

**Files:**
- Create : `src/ChefOS.Core/Costing/{RecipeBook,CostResult,CostIssue,CycleInfo,VatCalculator,FoodCostBadge}.cs`
- Test : `tests/ChefOS.Tests/Core/RecipeBookTests.cs`, `tests/ChefOS.Tests/Core/FoodCostBadgeTests.cs`

**Interfaces:**
- Consumes : entités et `UnitConverter` (Task 2).
- Produces :
  - `enum CostIssueKind { MissingPrice, IncompatibleUnit, InvalidOutputQuantity, UnknownItem, Cycle }`
  - `record CostIssue(CostIssueKind Kind, string ItemName, int? LineIndex)`
  - `record LineCost(int LineIndex, decimal? Cost)`
  - `record CostResult(decimal TotalCost, bool IsComplete, IReadOnlyList<CostIssue> Issues, IReadOnlyList<LineCost> Lines, decimal? PriceExclVat, decimal? FoodCostPercent, decimal? Margin)`
  - `record CycleInfo(string ContainerName, string ContainedName) { string Message => $"{ContainerName} contient déjà {ContainedName} (directement ou indirectement)"; }`
  - `sealed class RecipeBook(IEnumerable<Recipe> recipes, IEnumerable<Ingredient> ingredients)` :
    - `CostResult Cost(int recipeId)` (mémoïsé)
    - `CostResult Cost(Recipe recipe)` : accepte un brouillon non enregistré (lignes résolues via `IngredientId` / `SubRecipeId` dans les dictionnaires)
    - `EuAllergen Allergens(Recipe recipe)` : OU de tous les allergènes des ingrédients, sous-préparations comprises
    - `CycleInfo? FindCycle(Recipe draft)`
    - `IReadOnlyList<Recipe> Recipes`, `IReadOnlyList<Ingredient> Ingredients`
  - `static class VatCalculator { decimal ExclVat(decimal inclVat, decimal rate); }`
  - `enum FoodCostLevel { Good, Warning, Alert, Incomplete }` ; `static class FoodCostBadge { const decimal GoodBelow = 0.28m; FoodCostLevel Classify(CostResult result, decimal alertThreshold); }`

Règles (spec §5) :
- Ligne ingrédient : `ToBase(qty, unit, ingredient.BaseUnit, ingredient.AveragePieceWeightGrams)` ÷ `(YieldPercent / 100)` × `CurrentPricePerBaseUnit`. Prix null → `MissingPrice` ; conversion impossible → `IncompatibleUnit`. Dans les deux cas, coût de la ligne `null`.
- Ligne sous-préparation : `ToBase(qty, unit, prep.OutputUnit, null)` × `Cost(prep).TotalCost ÷ OutputQuantity`. `OutputQuantity` null ou ≤ 0, ou `OutputUnit` null → `InvalidOutputQuantity`. Si la préparation est incomplète, ses issues remontent (avec le `LineIndex` de la ligne parente) et le parent devient incomplet, mais la ligne reste chiffrée sur le coût partiel.
- Id inconnu → `UnknownItem`. Garde-fou : une recette déjà en cours de calcul (pile de visite) → `Cycle`.
- Un ingrédient ou une préparation **archivé(e)** continue d'être chiffré(e).
- `PriceExclVat`, `FoodCostPercent` et `Margin` ne sont renseignés que pour `Kind == Dish` avec `SellingPriceInclVat` et `VatRate` non nulls ; `FoodCostPercent` est null si `PriceExclVat == 0`.
- `FindCycle(draft)` : pour chaque ligne `SubRecipeId = s` du brouillon, si `s == draft.Id` ou si `s` contient transitivement `draft.Id` (`draft.Id != 0`), on renvoie `CycleInfo(nom de s, draft.Name)`.
- `Classify` : `Incomplete` si `!IsComplete` ou `FoodCostPercent` null ; sinon `Alert` si `> alertThreshold` ; sinon `Good` si `< 0.28` ; sinon `Warning`.

- [ ] **Step 1 : Écrire les tests qui échouent**

Jeu de données commun (`BuildBook()`), prix en € par unité de base :
- ingrédients : `1` Os de veau (Gram, 0.004, rendement 100) ; `2` Beurre (Gram, 0.010, 100, Milk) ; `3` Saumon (Gram, 0.020, 65, Fish) ; `4` Safran (Gram, prix null) ; `5` Œuf (Piece, 0.30, 100, Eggs) ; `6` Huile (Milliliter, 0.004, 100)
- `10` Fond de veau (Preparation, OutputQuantity 2000, Milliliter) : Os de veau 1 kg
- `11` Sauce beurre (Preparation, 1000, Milliliter) : Fond 500 ml + Beurre 100 g
- `20` Saumon sauce (Dish, 18.00 TTC, 0.12) : Sauce 10 cl + Saumon 130 g

```csharp
[Fact] public void IngredientLine_AppliesYield()
    => Assert.Equal(4.00m, Math.Round(book.Cost(Dish(lines: Line(ing: 3, 130, MeasureUnit.G))).TotalCost, 2));

[Fact] public void NestedPreparations_TwoLevels()
{
    var r = book.Cost(20);
    Assert.True(r.IsComplete);
    Assert.Equal(4.20m, Math.Round(r.TotalCost, 2));             // 0,20 sauce + 4,00 saumon
}

[Fact] public void Dish_FoodCostOnPriceExclVat()
{
    var r = book.Cost(20);
    Assert.Equal(16.07m, Math.Round(r.PriceExclVat!.Value, 2));
    Assert.Equal(0.261m, Math.Round(r.FoodCostPercent!.Value, 3));
    Assert.Equal(11.87m, Math.Round(r.Margin!.Value, 2));
}

[Fact] public void MissingPrice_IsIncomplete_AndListsIngredient()
{
    var r = book.Cost(Dish(lines: [Line(ing: 4, 1, MeasureUnit.G), Line(ing: 2, 100, MeasureUnit.G)]));
    Assert.False(r.IsComplete);
    Assert.Equal(1.00m, r.TotalCost);
    Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.MissingPrice && i.ItemName == "Safran" && i.LineIndex == 0);
    Assert.Null(r.Lines[0].Cost);
}

[Fact] public void IncompatibleUnit_IsReported()
    => Assert.Contains(book.Cost(Dish(lines: Line(ing: 6, 100, MeasureUnit.G))).Issues, i => i.Kind == CostIssueKind.IncompatibleUnit);

[Fact] public void PieceIngredient_CountedPerPiece()
    => Assert.Equal(0.60m, book.Cost(Dish(lines: Line(ing: 5, 2, MeasureUnit.Piece))).TotalCost);

[Fact] public void Preparation_WithZeroOutput_IsIncompleteNotCrash()   // Review Focus 3
{
    var b = BuildBook(fondOutput: 0m);
    var r = b.Cost(20);
    Assert.False(r.IsComplete);
    Assert.Contains(r.Issues, i => i.Kind == CostIssueKind.InvalidOutputQuantity);
}

[Fact] public void ArchivedIngredient_StillCosted()
    => Assert.Equal(4.20m, Math.Round(BuildBook(archiveSalmon: true).Cost(20).TotalCost, 2));

[Fact] public void Allergens_AggregateThroughPreparations()
    => Assert.Equal(EuAllergen.Milk | EuAllergen.Fish, book.Allergens(book.Recipes.Single(r => r.Id == 20)));

[Fact] public void FindCycle_Indirect()
{
    var fondDraft = Prep(id: 10, name: "Fond de veau", lines: Line(sub: 11, 100, MeasureUnit.Ml));
    var c = book.FindCycle(fondDraft);
    Assert.Equal("Sauce beurre contient déjà Fond de veau (directement ou indirectement)", c!.Message);
}

[Fact] public void FindCycle_Self() => Assert.NotNull(book.FindCycle(Prep(id: 10, name: "Fond de veau", lines: Line(sub: 10, 1, MeasureUnit.L))));

[Fact] public void FindCycle_NewRecipe_None() => Assert.Null(book.FindCycle(Dish(id: 0, lines: Line(sub: 11, 1, MeasureUnit.L))));
```

`FoodCostBadgeTests` : `0.25 → Good`, `0.30 → Warning` (seuil 0.35), `0.36 → Alert`, résultat incomplet `→ Incomplete`, seuil 0.20 avec 0.25 `→ Alert`.

(`Dish(...)`, `Prep(...)`, `Line(...)` sont des helpers privés de la classe de test.)

- [ ] **Step 2 : Lancer les tests pour vérifier l'échec**

Run : `dotnet test --filter "RecipeBookTests|FoodCostBadgeTests"` — Expected : compilation KO.

- [ ] **Step 3 : Implémenter les types listés dans Interfaces selon les règles ci-dessus**

- [ ] **Step 4 : Lancer les tests**

Run : `dotnet test --filter "RecipeBookTests|FoodCostBadgeTests"` — Expected : tout PASS.

- [ ] **Step 5 : Commit**

```bash
git add -A && git commit -m "feat: calcul du coût de revient (rendement, sous-préparations, TVA, cycles)"
```

---

### Task 4 : `ChefOsDbContext`, isolation multi-restaurant, migration initiale

**Files:**
- Create : `src/ChefOS.Infrastructure/Identity/ApplicationUser.cs`, `Data/{ChefOsDbContext,ITenantContext,FixedTenantContext,DesignTimeFactory}.cs`, `Data/Configurations/*.cs`, `Data/Migrations/*`, `.config/dotnet-tools.json`
- Test : `tests/ChefOS.Tests/Infrastructure/{PostgresFixture,TestContextFactory,TenantIsolationTests}.cs`

**Interfaces:**
- Consumes : entités (Task 2).
- Produces :
  - `class ApplicationUser : IdentityUser { int? ActiveRestaurantId { get; set; } }`
  - `interface ITenantContext { int? CurrentRestaurantId { get; } }` ; `sealed record FixedTenantContext(int? CurrentRestaurantId) : ITenantContext`
  - `class ChefOsDbContext(DbContextOptions<ChefOsDbContext> options, ITenantContext tenant) : IdentityDbContext<ApplicationUser>` avec les DbSets `Restaurants`, `Memberships`, `Suppliers`, `Ingredients`, `IngredientPrices`, `Recipes`, `RecipeLines`.
  - Tests : `PostgresFixture` (collection `"postgres"`) avec `Task<DbContextOptions<ChefOsDbContext>> CreateDatabaseAsync()` (base au nom unique, migrations appliquées) ; `TestContextFactory(DbContextOptions<ChefOsDbContext>, int? restaurantId) : IDbContextFactory<ChefOsDbContext>` ; `static Task<int> TestData.CreateRestaurantAsync(options, string name)`.

Points imposés :
- Le filtre global doit lire une **propriété de l'instance** pour être réévalué à chaque contexte : `private int? CurrentRestaurantId => _tenant.CurrentRestaurantId;` puis `HasQueryFilter(e => e.RestaurantId == CurrentRestaurantId)` sur `Supplier`, `Ingredient`, `IngredientPrice`, `Recipe` et `RecipeLine`.
- `SaveChanges` / `SaveChangesAsync` : pour chaque entrée `ITenantScoped` à l'état `Added` : si `CurrentRestaurantId` est null → `InvalidOperationException` ; si `RestaurantId == 0`, il prend la valeur courante ; si `RestaurantId` est différent de la valeur courante → `InvalidOperationException`. Pour les états `Modified` / `Deleted`, une entité dont le `RestaurantId` diffère → `InvalidOperationException`.
- Contraintes : clé de `Membership` = `(UserId, RestaurantId)` ; index unique sur `Restaurant.Slug` ; check `CK_RecipeLine_OneSource` = `("IngredientId" IS NULL) <> ("SubRecipeId" IS NULL)` ; `EuAllergen` stocké en `int` ; enums `BaseUnit` / `MeasureUnit` / `RecipeKind` / `PriceSource` / `MembershipRole` stockés en texte.
- Suppressions : RecipeLine→Recipe `Cascade` ; RecipeLine→Ingredient et RecipeLine→SubRecipe `Restrict` ; Ingredient→Supplier et IngredientPrice→Supplier `SetNull` ; IngredientPrice→Ingredient `Cascade` ; Membership→Restaurant et Membership→User `Cascade` ; chaque entité `ITenantScoped`→Restaurant `Cascade`.
- Précisions de colonnes : voir Global Constraints.
- `DesignTimeFactory : IDesignTimeDbContextFactory<ChefOsDbContext>` utilise la chaîne de dev et `new FixedTenantContext(null)`.

- [ ] **Step 1 : Ajouter les paquets et l'outil**

```bash
dotnet add src/ChefOS.Infrastructure package Npgsql.EntityFrameworkCore.PostgreSQL
dotnet add src/ChefOS.Infrastructure package Microsoft.AspNetCore.Identity.EntityFrameworkCore
dotnet add src/ChefOS.Infrastructure package Microsoft.EntityFrameworkCore.Design
dotnet add tests/ChefOS.Tests package Testcontainers.PostgreSql
dotnet new tool-manifest && dotnet tool install dotnet-ef
```

- [ ] **Step 2 : Écrire les tests qui échouent** (`[Collection("postgres")]`)

```csharp
[Fact] public async Task Query_OnlyReturnsCurrentRestaurantRows()
{   // A crée "Tomate", B crée "Poivron"
    await using var dbA = new TestContextFactory(options, a).CreateDbContext();
    Assert.Equal(["Tomate"], await dbA.Ingredients.Select(i => i.Name).ToListAsync());
}
[Fact] public async Task FindById_OtherRestaurant_ReturnsNull()       // FindAsync ne passe pas par le filtre : utiliser FirstOrDefaultAsync(i => i.Id == id)
[Fact] public async Task Add_StampsRestaurantId()                      // RestaurantId 0 → a
[Fact] public async Task Add_WithoutTenant_Throws()                    // FixedTenantContext(null) → InvalidOperationException
[Fact] public async Task Modify_OtherRestaurantEntity_Throws()         // entité de B attachée au contexte de A puis modifiée → InvalidOperationException
[Fact] public async Task RecipeLine_BothSources_RejectedByDatabase()    // IngredientId et SubRecipeId renseignés → DbUpdateException
```

- [ ] **Step 3 : Lancer les tests pour vérifier l'échec**

Run : `dotnet test --filter TenantIsolationTests` — Expected : compilation KO.

- [ ] **Step 4 : Implémenter le contexte, les configurations, la factory de design et la fixture, puis générer la migration**

Run : `dotnet ef migrations add InitialCreate -p src/ChefOS.Infrastructure -o Data/Migrations`
Expected : `Done.` ; la migration contient la contrainte `CK_RecipeLine_OneSource`.

- [ ] **Step 5 : Lancer les tests**

Run : `dotnet test --filter TenantIsolationTests` — Expected : 6 PASS (Docker doit être démarré).

- [ ] **Step 6 : Commit**

```bash
git add -A && git commit -m "feat: ChefOsDbContext PostgreSQL avec isolation multi-restaurant"
```

---

### Task 5 : `RestaurantService` (création, slug, appartenances, réglages)

**Files:**
- Create : `src/ChefOS.Infrastructure/Services/RestaurantService.cs`, `Services/Inputs.cs` (partie restaurant)
- Test : `tests/ChefOS.Tests/Infrastructure/RestaurantServiceTests.cs`

**Interfaces:**
- Consumes : `ChefOsDbContext`, `SlugGenerator`.
- Produces :
  - `record CreateRestaurantInput(string Name, string? Address, string? VatNumber)`
  - `record RestaurantSettingsInput(string Name, string Slug, string? Address, string? VatNumber, decimal DefaultFoodVatRate, decimal DefaultDrinkVatRate, decimal FoodCostAlertThreshold)`
  - `record MembershipSummary(int RestaurantId, string RestaurantName, MembershipRole Role)`
  - `class RestaurantService(IDbContextFactory<ChefOsDbContext> factory)` :
    - `Task<Restaurant> CreateAsync(string ownerUserId, CreateRestaurantInput input)` : crée le restaurant et la `Membership` `Owner`.
    - `Task<IReadOnlyList<MembershipSummary>> GetMembershipsAsync(string userId)` (triées par nom)
    - `Task<bool> IsMemberAsync(string userId, int restaurantId)`
    - `Task<Restaurant?> GetAsync(int restaurantId)`
    - `Task UpdateSettingsAsync(int restaurantId, RestaurantSettingsInput input)`

Règles : le slug unique est `FromName(name)`, puis `-2`, `-3`… jusqu'à trouver un slug libre ; un slug saisi dans les réglages doit correspondre à `^[a-z0-9]+(-[a-z0-9]+)*$` et être libre, sinon `DomainValidationException("Ce lien est déjà utilisé par un autre restaurant")` ou `("Le lien ne peut contenir que des minuscules, chiffres et tirets")`. Le nom est obligatoire. Les taux doivent être compris entre 0 et 1.

- [ ] **Step 1 : Écrire les tests qui échouent**

```csharp
[Fact] public async Task Create_MakesOwnerMembership()
[Fact] public async Task Create_SameName_GetsSuffixedSlug()            // Review Focus 5 : "le-petit-bistro" puis "le-petit-bistro-2"
[Fact] public async Task GetMemberships_OnlyForUser()
[Fact] public async Task UpdateSettings_TakenSlug_Throws()             // message "Ce lien est déjà utilisé par un autre restaurant"
[Fact] public async Task UpdateSettings_InvalidSlug_Throws()           // "Mon Resto!" → DomainValidationException
```

Les utilisateurs de test sont insérés directement via `db.Users.Add(new ApplicationUser { UserName = ..., Email = ... })`.

- [ ] **Step 2 : Lancer → échec** : `dotnet test --filter RestaurantServiceTests`
- [ ] **Step 3 : Implémenter `RestaurantService`** (`Restaurant` et `Membership` ne sont pas `ITenantScoped` : aucun tenant requis)
- [ ] **Step 4 : Lancer → PASS** : `dotnet test --filter RestaurantServiceTests`
- [ ] **Step 5 : Commit** : `git commit -am "feat: RestaurantService (création, slug unique, réglages)"` (après `git add -A`)

---

### Task 6 : `IngredientService` et `SupplierService`

**Files:**
- Create : `src/ChefOS.Infrastructure/Services/{IngredientService,SupplierService}.cs`, suite de `Services/Inputs.cs`
- Test : `tests/ChefOS.Tests/Infrastructure/IngredientServiceTests.cs`

**Interfaces:**
- Consumes : `ChefOsDbContext` (filtré par restaurant), entités.
- Produces :
  - `record IngredientInput(int? Id, string Name, string Category, EuAllergen Allergens, BaseUnit BaseUnit, decimal? PricePerBaseUnit, decimal YieldPercent, decimal? AveragePieceWeightGrams, int? SupplierId)`
  - `record SupplierInput(int? Id, string Name, string? ContactName, string? Email, string? Phone)`
  - `record DeleteResult(bool Deleted, IReadOnlyList<string> UsedInRecipes)`
  - `class IngredientService(IDbContextFactory<ChefOsDbContext> factory)` :
    - `Task<IReadOnlyList<Ingredient>> ListAsync(bool includeArchived = false)` (triée par catégorie puis nom, avec `Supplier`)
    - `Task<Ingredient?> GetAsync(int id)`
    - `Task<Ingredient> SaveAsync(IngredientInput input)` : si `PricePerBaseUnit` est non null et différent du prix courant, ajoute un `IngredientPrice` (`Manual`, `DateTime.UtcNow`, `SupplierId` de l'ingrédient).
    - `Task SetPriceAsync(int ingredientId, decimal pricePerBaseUnit, PriceSource source, int? supplierId, DateTime dateUtc)` : ajoute l'historique et met à jour `CurrentPricePerBaseUnit` (servira à l'étape 2).
    - `Task<IReadOnlyList<IngredientPrice>> GetPriceHistoryAsync(int ingredientId)` (triée par date croissante)
    - `Task<IReadOnlyList<string>> GetUsageAsync(int ingredientId)` (noms des recettes qui l'utilisent)
    - `Task SetArchivedAsync(int id, bool archived)`
    - `Task<DeleteResult> DeleteAsync(int id)` : refusée si l'ingrédient est utilisé.
  - `class SupplierService(IDbContextFactory<ChefOsDbContext> factory)` : `ListAsync()`, `SaveAsync(SupplierInput)`, `DeleteAsync(int id)` (les ingrédients et prix liés passent à `SupplierId = null` via `SetNull`).

Validation (`DomainValidationException`) : nom obligatoire (« Le nom est obligatoire ») ; `YieldPercent` entre 1 et 100 (« Le rendement doit être compris entre 1 et 100 % ») ; prix ≥ 0 ; `AveragePieceWeightGrams` > 0 s'il est renseigné.

- [ ] **Step 1 : Écrire les tests qui échouent**

```csharp
[Fact] public async Task Save_NewPrice_AddsManualHistoryEntry()
[Fact] public async Task Save_SamePrice_NoHistoryEntry()
[Fact] public async Task SetPrice_UpdatesCurrentAndHistory()            // Source = Receipt conservée
[Theory, InlineData(0), InlineData(101)] public async Task Save_InvalidYield_Throws(decimal y)
[Fact] public async Task Delete_UsedIngredient_RefusedWithRecipeNames()  // UsedInRecipes == ["Carbonnade"]
[Fact] public async Task Delete_UnusedIngredient_Deletes()
[Fact] public async Task List_HidesArchivedByDefault()
[Fact] public async Task DeleteSupplier_NullsIngredientSupplier()
```

- [ ] **Step 2 : Lancer → échec** : `dotnet test --filter IngredientServiceTests`
- [ ] **Step 3 : Implémenter les deux services**
- [ ] **Step 4 : Lancer → PASS** : `dotnet test --filter IngredientServiceTests`
- [ ] **Step 5 : Commit** : `git add -A && git commit -m "feat: services ingrédients (historique de prix, archivage) et fournisseurs"`

---

### Task 7 : `RecipeService` et `DashboardService`

**Files:**
- Create : `src/ChefOS.Infrastructure/Services/{RecipeService,DashboardService}.cs`, suite de `Services/Inputs.cs`
- Test : `tests/ChefOS.Tests/Infrastructure/{RecipeServiceTests,DashboardServiceTests}.cs`

**Interfaces:**
- Consumes : `RecipeBook`, `FoodCostBadge` (Task 3), `ChefOsDbContext`.
- Produces :
  - `record RecipeLineInput(int? IngredientId, int? SubRecipeId, decimal Quantity, MeasureUnit Unit)`
  - `record RecipeInput(int? Id, string Name, RecipeKind Kind, string? Category, decimal? SellingPriceInclVat, decimal? VatRate, decimal? OutputQuantity, BaseUnit? OutputUnit, string? Instructions, IReadOnlyList<RecipeLineInput> Lines)`
  - `record RecipeSummary(int Id, string Name, RecipeKind Kind, string? Category, bool IsArchived, CostResult Cost, EuAllergen Allergens)`
  - `class RecipeService(IDbContextFactory<ChefOsDbContext> factory)` :
    - `Task<RecipeBook> LoadBookAsync()` : toutes les recettes (archivées comprises) avec leurs lignes, et tous les ingrédients.
    - `Task<IReadOnlyList<RecipeSummary>> ListAsync(RecipeKind kind, bool includeArchived = false)`
    - `Task<Recipe?> GetAsync(int id)` (avec les lignes)
    - `Task<Recipe> SaveAsync(RecipeInput input)` : remplace l'ensemble des lignes.
    - `Task SetArchivedAsync(int id, bool archived)`
    - `Task<DeleteResult> DeleteAsync(int id)` : refusée si la recette sert de sous-préparation.
  - `record WatchItem(int RecipeId, string Name, decimal? FoodCostPercent, FoodCostLevel Level)`
  - `record DashboardSummary(int DishCount, int PreparationCount, decimal? AverageFoodCost, IReadOnlyList<WatchItem> WatchList)`
  - `class DashboardService(RecipeService recipes, IDbContextFactory<ChefOsDbContext> factory)` : `Task<DashboardSummary> GetSummaryAsync()`. Le seuil vient du `Restaurant` courant. La moyenne porte sur les plats non archivés au coût complet et au food cost non nul. La `WatchList` contient les plats en `Alert` ou `Incomplete`, triés `Incomplete` d'abord puis par food cost décroissant.

Validation de `SaveAsync` (`DomainValidationException`) : nom obligatoire ; chaque ligne a exactement une source (« Chaque ligne doit contenir un ingrédient ou une préparation ») ; `Quantity > 0` (« La quantité doit être supérieure à 0 ») ; un plat a `SellingPriceInclVat ≥ 0` et `VatRate` entre 0 et 1 ; une préparation a `OutputQuantity > 0` et `OutputUnit` non null (« Indiquez la quantité produite par la préparation ») ; `LoadBookAsync().FindCycle(draft)` non null → `DomainValidationException(cycle.Message)`.

- [ ] **Step 1 : Écrire les tests qui échouent**

```csharp
[Fact] public async Task Save_DishWithLines_ListReturnsCost()
[Fact] public async Task Save_ReplacesLines()
[Fact] public async Task Save_RejectsNonPositiveQuantity()              // Review Focus 4 : quantité 0 et -1
[Fact] public async Task Save_Cycle_ThrowsWithMessage()                 // "Sauce contient déjà Fond (directement ou indirectement)"
[Fact] public async Task Save_PreparationWithoutOutput_Throws()
[Fact] public async Task Delete_PreparationUsedAsSubRecipe_Refused()
[Fact] public async Task PriceChange_PropagatesThroughPreparation()     // Review Focus 2 : SetPriceAsync sur un ingrédient du fond → le coût du plat via la sauce augmente
// DashboardServiceTests
[Fact] public async Task Summary_AverageExcludesIncompleteDishes()
[Fact] public async Task Summary_WatchList_IncompleteFirstThenHighestFoodCost()
```

- [ ] **Step 2 : Lancer → échec** : `dotnet test --filter "RecipeServiceTests|DashboardServiceTests"`
- [ ] **Step 3 : Implémenter les deux services**
- [ ] **Step 4 : Lancer → PASS**
- [ ] **Step 5 : Commit** : `git add -A && git commit -m "feat: services recettes (validation, cycles) et tableau de bord"`

---

### Task 8 : Données de démo et enregistrement des services

**Files:**
- Create : `src/ChefOS.Infrastructure/Seeding/{DemoSeeder,DemoOptions}.cs`, `src/ChefOS.Infrastructure/DependencyInjection.cs`
- Test : `tests/ChefOS.Tests/Infrastructure/DemoSeederTests.cs`

**Interfaces:**
- Consumes : tous les services précédents, `UserManager<ApplicationUser>`.
- Produces :
  - `record DemoOptions { string Email; string Password; }` (section de configuration `Demo`)
  - `class DemoSeeder(UserManager<ApplicationUser> users, DbContextOptions<ChefOsDbContext> options, IOptions<DemoOptions> demo)` : `Task SeedAsync(CancellationToken ct = default)`. Ne fait rien si un `Restaurant` existe déjà. Crée l'utilisateur de démo (`EmailConfirmed = true`, `ActiveRestaurantId` renseigné), « Le Petit Bistro » (slug `le-petit-bistro`) avec sa `Membership` `Owner`, puis écrit les données métier via un `ChefOsDbContext` construit avec `new FixedTenantContext(restaurantId)`.
  - `static IServiceCollection AddChefOsInfrastructure(this IServiceCollection services, string connectionString)` : `AddDbContextFactory<ChefOsDbContext>(o => o.UseNpgsql(cs), ServiceLifetime.Scoped)` (ce qui enregistre aussi le contexte en scoped, nécessaire à Identity), plus `RestaurantService`, `IngredientService`, `SupplierService`, `RecipeService`, `DashboardService` et `DemoSeeder` en scoped.

Contenu imposé (spec §8) : 3 fournisseurs ; au moins 40 ingrédients belges réalistes, chacun avec un prix, un rendement crédible (légumes 80–90 %, poissons entiers 50–65 %, viandes à parer 85–95 %) et ses allergènes ; au moins 3 ingrédients avec au moins 4 entrées `IngredientPrice` réparties sur les 90 derniers jours ; les 5 préparations (fond de veau, mayonnaise maison, sauce lapin à la bière, purée, pâte à gaufre) ; au moins 10 plats, dont carbonnade flamande, boulets liégeois, vol-au-vent, moules-frites, américain préparé et croquettes de crevettes, avec un prix TTC et la TVA à 0.12, chacun utilisant au moins une préparation ou au moins 3 ingrédients. Les prix sont choisis pour que chaque plat ait un food cost compris entre 0.20 et 0.40, sauf **un** plat volontairement au-dessus de 0.35 pour peupler la liste « à surveiller ».

- [ ] **Step 1 : Écrire les tests qui échouent** (le `ServiceProvider` du test enregistre `AddChefOsInfrastructure` sur la base de test, plus `AddIdentityCore<ApplicationUser>().AddEntityFrameworkStores<ChefOsDbContext>()`, `ITenantContext` = `FixedTenantContext(null)` et le logging)

```csharp
[Fact] public async Task Seed_EmptyDatabase_CreatesBistroWithCompleteCosts()
{   // ≥ 40 ingrédients, 5 préparations, ≥ 10 plats
    // pour chaque plat : RecipeBook.Cost(id).IsComplete et FoodCostPercent ∈ [0.20, 0.50]
    // au moins 1 plat au-dessus de 0.35, et tous les autres ≤ 0.40
}
[Fact] public async Task Seed_Twice_IsIdempotent()
[Fact] public async Task Seed_DemoUserCanSignIn()                     // UserManager.CheckPasswordAsync == true
```

- [ ] **Step 2 : Lancer → échec** : `dotnet test --filter DemoSeederTests`
- [ ] **Step 3 : Implémenter `DemoSeeder`, `DemoOptions` et `AddChefOsInfrastructure`**
- [ ] **Step 4 : Lancer → PASS**
- [ ] **Step 5 : Commit** : `git add -A && git commit -m "feat: données de démo Le Petit Bistro et enregistrement des services"`

---

### Task 9 : Back — Identity sur PostgreSQL, restaurant actif, onboarding, mise en page

**Files:**
- Delete : `src/ChefOS.Back/Data/` (le contexte et les migrations SQLite du template), `Components/Pages/{Counter,Weather}.razor`
- Modify : `src/ChefOS.Back/Program.cs`, `appsettings.json`, `appsettings.Development.json`, `Components/App.razor`, `Components/_Imports.razor`, `Components/Layout/{MainLayout,NavMenu}.razor`, toutes les références à `ApplicationDbContext` / `ApplicationUser` dans `Components/Account/**`
- Create : `src/ChefOS.Back/Tenancy/{ClaimsTenantContext,ChefOsClaimsPrincipalFactory,ActiveRestaurantEndpoint}.cs`, `Components/Layout/RestaurantSelector.razor`, `Components/Pages/Onboarding.razor`

**Interfaces:**
- Consumes : `AddChefOsInfrastructure`, `RestaurantService`, `DemoSeeder`, `ApplicationUser`.
- Produces :
  - Claim `"chefos:restaurant"` (id du restaurant actif) dans le cookie d'authentification.
  - `ClaimsTenantContext(AuthenticationStateProvider auth, IHttpContextAccessor http) : ITenantContext` : lit le claim depuis `HttpContext.User` si un `HttpContext` existe, sinon depuis l'état d'authentification du circuit (tâche déjà complétée dans un circuit Blazor Server ; on utilise `.Result` seulement si `IsCompletedSuccessfully`, sinon null).
  - `ChefOsClaimsPrincipalFactory : UserClaimsPrincipalFactory<ApplicationUser>` : si `ActiveRestaurantId` correspond à une `Membership`, ajoute le claim ; sinon prend la première appartenance (par nom), la sauvegarde dans `ActiveRestaurantId` et ajoute le claim ; aucune appartenance → pas de claim.
  - Endpoint `POST /account/active-restaurant` (champ de formulaire `restaurantId`, antiforgery actif, `RequireAuthorization`) : vérifie `IsMemberAsync` (sinon 403), met à jour `ActiveRestaurantId`, appelle `SignInManager.RefreshSignInAsync`, puis redirige vers `returnUrl` local ou `/`.
  - Page `/onboarding` : `[Authorize]`, `[ExcludeFromInteractiveRouting]`, `EditForm` statique avec `FormName="onboarding"` (nom, adresse, n° TVA) → `RestaurantService.CreateAsync`, `ActiveRestaurantId`, `RefreshSignInAsync`, redirection vers `/`.

- [ ] **Step 1 : Brancher l'infrastructure dans `Program.cs`**
  - `AddChefOsInfrastructure(builder.Configuration.GetConnectionString("ChefOs")!)` ; `AddHttpContextAccessor()` ; `AddScoped<ITenantContext, ClaimsTenantContext>()`.
  - Identity : `AddIdentityCore<ApplicationUser>(o => o.SignIn.RequireConfirmedAccount = false).AddEntityFrameworkStores<ChefOsDbContext>().AddSignInManager().AddDefaultTokenProviders().AddClaimsPrincipalFactory<ChefOsClaimsPrincipalFactory>()`.
  - `AddMudServices()` (paquet `MudBlazor`), avec les CSS/JS MudBlazor dans `App.razor` et les providers MudBlazor dans `MainLayout`.
  - Culture : `RequestLocalizationOptions` avec `fr-BE` comme seule culture, et `CultureInfo.DefaultThreadCurrentCulture = DefaultThreadCurrentUICulture = new("fr-BE")`.
  - En Development : `MigrateAsync()`, puis `DemoSeeder.SeedAsync()` dans un scope au démarrage.
  - `appsettings.Development.json` : `ConnectionStrings:ChefOs` (Global Constraints) et `Demo: { Email: "demo@chefos.local", Password: "ChefDemo2026!" }`.
- [ ] **Step 2 : Remplacer les références au contexte du template** dans `Components/Account/**` (`ApplicationDbContext` → `ChefOsDbContext`, `ChefOS.Back.Data.ApplicationUser` → `ChefOS.Infrastructure.Identity.ApplicationUser`), puis supprimer `Data/`, `Counter` et `Weather`.
- [ ] **Step 3 : Créer `ClaimsTenantContext`, `ChefOsClaimsPrincipalFactory`, l'endpoint et `Onboarding.razor`**
- [ ] **Step 4 : Mise en page MudBlazor**
  - `MainLayout` : `MudAppBar` avec le nom du produit et `RestaurantSelector` (un `MudMenu` listant `GetMembershipsAsync` ; chaque entrée est un `<form method="post" action="/account/active-restaurant" data-enhance="false">` avec `<AntiforgeryToken />`, sur le même modèle que le formulaire de déconnexion du template), plus un `MudDrawer` de navigation : Tableau de bord, Recettes, Ingrédients, Fournisseurs, Réglages.
  - Si l'utilisateur est connecté sans claim `chefos:restaurant`, `MainLayout` redirige vers `/onboarding` avec `forceLoad: true`. `Onboarding.razor` utilise `@layout EmptyLayout` (un layout minimal à créer dans `Components/Layout/EmptyLayout.razor`) pour éviter une boucle de redirection.
  - `Components/Pages/_Imports.razor` : `@attribute [Authorize]` (les pages `Account/**` gardent leurs propres attributs).
- [ ] **Step 5 : Vérifier**

Run : `dotnet build && dotnet test` — Expected : build OK, tous les tests précédents PASS.
Run : `docker compose up -d && dotnet run --project src/ChefOS.Back` puis, dans le navigateur :
  1. Connexion avec le compte de démo → arrivée sur `/` ; le sélecteur affiche « Le Petit Bistro ».
  2. Déconnexion, inscription d'un nouveau compte → redirection vers `/onboarding` ; création de « Chez Test » → retour sur `/` avec « Chez Test » dans le sélecteur.
  3. Un `POST /account/active-restaurant` avec l'id du Petit Bistro depuis ce nouveau compte → 403.
- [ ] **Step 6 : Commit** : `git add -A && git commit -m "feat: back chef — Identity PostgreSQL, restaurant actif, onboarding, layout MudBlazor"`

---

### Task 10 : Back — Fournisseurs et Ingrédients

**Files:**
- Create : `Components/Pages/Suppliers/SupplierList.razor`, `Components/Pages/Ingredients/{IngredientList,IngredientDetail}.razor`, `Components/Shared/AllergenChips.razor`

**Interfaces:**
- Consumes : `SupplierService`, `IngredientService`, `UnitConverter.PricePerBaseFromDisplay` / `PriceDisplayFromBase` / `DisplayUnitLabel`.
- Produces : `AllergenChips` (`[Parameter] EuAllergen Value`), qui affiche une puce par allergène avec son libellé français (Gluten, Crustacés, Œufs, Poisson, Arachides, Soja, Lait, Fruits à coque, Céleri, Moutarde, Sésame, Sulfites, Lupin, Mollusques). Réutilisé en Task 11.

Écrans (spec §6) :
- `/suppliers` : `MudTable` ; ajout et modification dans un `MudDialog` ; suppression avec confirmation.
- `/ingredients` : `MudTable` avec recherche par nom, filtre par catégorie (`MudSelect`), interrupteur « Afficher les archivés », colonnes Nom / Catégorie / Prix (`12,50 € / kg`) / Rendement / Fournisseur / Allergènes. Bouton « Nouvel ingrédient » → `/ingredients/new`.
- `/ingredients/{id:int}` et `/ingredients/new` : formulaire Nom, Catégorie (`MudAutocomplete` sur les catégories existantes, saisie libre), Unité de base, Prix **par unité d'affichage** (€/kg, €/l ou €/pièce, converti via `UnitConverter`), Rendement %, Poids moyen d'une pièce (g), Fournisseur, Allergènes (14 cases à cocher). Sous le formulaire : un `MudChart` en lignes de l'historique des prix (unité d'affichage, masqué s'il y a moins de 2 points) et la liste « Utilisé dans » (`GetUsageAsync`). Actions : Enregistrer, Archiver/Désarchiver, Supprimer (si refusé, un `MudAlert` liste les recettes concernées et propose l'archivage).
- Les `DomainValidationException` s'affichent via `ISnackbar` (gravité `Error`), sans faire planter le circuit.
- Un id inexistant ou appartenant à un autre restaurant (`GetAsync` renvoie null à cause du filtre) affiche « Élément introuvable » avec un lien de retour vers la liste. Même règle pour les recettes en Task 11.

- [ ] **Step 1 : Créer `AllergenChips` et `SupplierList`**
- [ ] **Step 2 : Créer `IngredientList`**
- [ ] **Step 3 : Créer `IngredientDetail`**
- [ ] **Step 4 : Build et tests** : `dotnet build && dotnet test` → OK
- [ ] **Step 5 : Vérification manuelle** (`dotnet run --project src/ChefOS.Back`, compte de démo)
  1. **Review Focus 1** : créer « Test virgule », unité Gramme, prix `12,50`, rendement `87,5` → après rechargement, la fiche affiche `12,50 € / kg` et `87,5 %`.
  2. Modifier le prix à `14,00` → le graphique compte un point de plus.
  3. Supprimer « Os de veau » (utilisé) → refus listant « Fond de veau » ; l'archiver → il disparaît de la liste, puis réapparaît avec « Afficher les archivés ».
  4. Rendement `0` → snackbar « Le rendement doit être compris entre 1 et 100 % ».
- [ ] **Step 6 : Commit** : `git add -A && git commit -m "feat: back chef — pages fournisseurs et ingrédients (historique des prix)"`

---

### Task 11 : Back — Liste et éditeur de recettes

**Files:**
- Create : `Components/Pages/Recipes/{RecipeList,RecipeEditor}.razor`, `Components/Shared/FoodCostChip.razor`

**Interfaces:**
- Consumes : `RecipeService` (`LoadBookAsync`, `ListAsync`, `GetAsync`, `SaveAsync`, `SetArchivedAsync`, `DeleteAsync`), `RecipeBook.Cost(Recipe)` / `Allergens(Recipe)`, `FoodCostBadge.Classify`, `AllergenChips`, `RestaurantService.GetAsync` (seuil et TVA par défaut).
- Produces : `FoodCostChip` (`[Parameter] CostResult Cost`, `[Parameter] decimal Threshold`) : 🟢 / 🟠 / 🔴 avec le pourcentage, ou ⚠️ « coût incomplet » accompagné d'une infobulle qui liste `Issues`.

Écrans (spec §6) :
- `/recipes` : `MudTabs` Plats / Préparations ; colonnes Nom, Catégorie, Coût, Prix TTC (plats), Food cost (`FoodCostChip`, plats), Coût par unité produite (préparations, en €/kg, €/l ou €/pièce), Allergènes ; interrupteur archivés ; boutons « Nouveau plat » et « Nouvelle préparation » → `/recipes/new?kind=Dish|Preparation`.
- `/recipes/{id:int}` et `/recipes/new` :
  - En-tête : Nom, Catégorie ; pour un plat, Prix TTC et TVA (pré-remplie avec `DefaultFoodVatRate`, choix 6 / 12 / 21 %) ; pour une préparation, Quantité produite et Unité (affichée en kg, l ou pièces, stockée en unité de base via `UnitConverter.ToBase`).
  - Lignes : `MudAutocomplete` sur ingrédients **et** préparations non archivés (préfixes « 🥕 » et « 🍲 », en excluant la recette elle-même), Quantité, Unité (`MeasureUnit`), Coût de la ligne (`CostResult.Lines[i]`, ou icône d'erreur avec le libellé de l'issue : « Prix manquant », « Unité incompatible — renseignez le poids moyen d'une pièce », « Quantité produite manquante »), bouton supprimer.
  - Panneau récapitulatif, recalculé à **chaque modification** via `book.Cost(draft)` sur un `Recipe` brouillon construit depuis le formulaire : Coût total, Prix HT, Food cost (`FoodCostChip`), Marge, Allergènes (`AllergenChips`).
  - Instructions (`MudTextField` multiligne).
  - Actions : Enregistrer (les erreurs de validation et de cycle s'affichent en snackbar), Archiver, Supprimer (refus → `MudAlert` avec les recettes concernées).

- [ ] **Step 1 : Créer `FoodCostChip`**
- [ ] **Step 2 : Créer `RecipeList`**
- [ ] **Step 3 : Créer `RecipeEditor`**
- [ ] **Step 4 : Build et tests** : `dotnet build && dotnet test` → OK
- [ ] **Step 5 : Vérification manuelle** (compte de démo)
  1. Ouvrir « Carbonnade flamande » : changer une quantité → coût, food cost et marge changent sans rechargement.
  2. Ajouter une ligne « Safran » sans prix (créé auparavant sans prix) → ⚠️ coût incomplet et liste de l'ingrédient concerné.
  3. Dans « Fond de veau », ajouter la préparation « Sauce lapin à la bière » si elle contient le fond (sinon créer une préparation qui le contient) → enregistrement refusé avec « … contient déjà Fond de veau (directement ou indirectement) ».
  4. Dans un plat, ajouter « Œuf » en `G` alors que l'œuf est à la pièce sans poids moyen → ligne en erreur « Unité incompatible… ».
- [ ] **Step 6 : Commit** : `git add -A && git commit -m "feat: back chef — liste et éditeur de recettes avec coût en direct"`

---

### Task 12 : Tableau de bord, réglages, squelette Vitrine, README

**Files:**
- Create : `src/ChefOS.Back/Components/Pages/{Dashboard,Settings}.razor` (le `Home.razor` du template est remplacé par `Dashboard` sur `/`), `README.md`
- Modify : `src/ChefOS.Vitrine/Program.cs`, `src/ChefOS.Vitrine/appsettings.Development.json`, `src/ChefOS.Vitrine/Components/Pages/Home.razor`

**Interfaces:**
- Consumes : `DashboardService.GetSummaryAsync`, `RestaurantService.GetAsync` / `UpdateSettingsAsync`, `FoodCostChip`, `AddChefOsInfrastructure`.

- [ ] **Step 1 : `Dashboard.razor` (`/`)** : trois `MudPaper` de KPI (Plats, Préparations, Food cost moyen en %, ou « — » si null) et un tableau « Plats à surveiller » (nom avec lien vers l'éditeur, `FoodCostChip`). Liste vide → message « Aucun plat à surveiller 👌 ».
- [ ] **Step 2 : `Settings.razor` (`/settings`)** : formulaire `RestaurantSettingsInput` ; taux saisis en % et convertis en fraction ; seuil d'alerte en % ; aperçu du lien public `/{slug}`. Les erreurs (slug pris ou invalide) passent par la snackbar. Réservé au rôle `Owner` : sinon, affichage en lecture seule.
- [ ] **Step 3 : Squelette Vitrine** : `Program.cs` appelle `AddChefOsInfrastructure` et enregistre `ITenantContext` = `FixedTenantContext(null)` ; `appsettings.Development.json` contient la même chaîne de connexion ; `Home.razor` affiche « ChefOS Vitrine — bientôt disponible ».
- [ ] **Step 4 : `README.md`** : prérequis (.NET 10, Docker), `docker compose up -d`, `dotnet run --project src/ChefOS.Back`, identifiants de démo (`demo@chefos.local` / `ChefDemo2026!`, valeurs de dev de `appsettings.Development.json`), `dotnet test` (Docker requis), commande de création d'une migration.
- [ ] **Step 5 : Vérification « terminé » (spec §11)**

Run : `docker compose down -v && docker compose up -d && dotnet test`
Expected : tous les tests PASS.

Run : `dotnet run --project src/ChefOS.Back` sur une base vide.
Expected : les migrations et le seed s'exécutent, le compte de démo arrive sur un tableau de bord rempli avec au moins 1 plat à surveiller ; deux comptes distincts ne voient jamais les recettes l'un de l'autre ; `dotnet run --project src/ChefOS.Vitrine` démarre sans erreur.

- [ ] **Step 6 : Commit** : `git add -A && git commit -m "feat: tableau de bord, réglages restaurant, squelette vitrine et README"`
