using ChefOS.Core.Ingredients;
using ChefOS.Core.Recipes;
using ChefOS.Core.Restaurants;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Data;
using ChefOS.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ChefOS.Infrastructure.Seeding;

/// <summary>
/// Remplit une base vide avec « Le Petit Bistro », un bistrot belge crédible (fournisseurs, ingrédients avec historique de prix,
/// préparations, plats) et son compte de démonstration, pour que l'outil du chef paraisse vivant dès le premier lancement.
/// Les données sont écrites directement dans le contexte (pas via les services) mais respectent leurs règles :
/// rendement de 1 à 100, plats avec prix et TVA, préparations avec quantité produite, une seule source par ligne, aucune boucle.
/// </summary>
public class DemoSeeder(UserManager<ApplicationUser> users, DbContextOptions<ChefOsDbContext> options, IOptions<DemoOptions> demo)
{
    private const string BistroName = "Le Petit Bistro";
    private const string BistroSlug = "le-petit-bistro";
    private const decimal FoodVatRate = 0.12m;

    private const string Meats = "Viandes";
    private const string Seafood = "Poissons et fruits de mer";
    private const string Vegetables = "Légumes";
    private const string Fruits = "Fruits";
    private const string Herbs = "Herbes et aromates";
    private const string Dairy = "Produits laitiers";
    private const string Grocery = "Épicerie";
    private const string Drinks = "Boissons";

    /// <summary>Ne fait rien dès qu'un restaurant existe : la démo ne s'écrit que dans une base vide.</summary>
    public async Task SeedAsync(CancellationToken ct = default)
    {
        // Restaurant et Membership ne sont pas rattachés à un restaurant courant : contexte sans restaurant.
        await using var global = new ChefOsDbContext(options, new FixedTenantContext(null));
        if (await global.Restaurants.AnyAsync(ct))
            return;

        var user = await GetOrCreateDemoUserAsync();

        var restaurant = new Restaurant
        {
            Name = BistroName,
            Slug = BistroSlug,
            Address = "Rue du Marché aux Herbes 24, 1000 Bruxelles",
            VatNumber = "BE0123.456.789",
            CreatedAt = DateTime.UtcNow
        };
        global.Memberships.Add(new Membership { UserId = user.Id, Restaurant = restaurant, Role = MembershipRole.Owner });
        await global.SaveChangesAsync(ct);

        try
        {
            user.ActiveRestaurantId = restaurant.Id;
            EnsureSucceeded(await users.UpdateAsync(user), "Impossible de sélectionner le restaurant de démo");

            await using var db = new ChefOsDbContext(options, new FixedTenantContext(restaurant.Id));
            await SeedBusinessDataAsync(db, DateTime.UtcNow, ct);
        }
        catch
        {
            // Pas de demi-bistro : supprimer le restaurant emporte (cascade) l'appartenance et les données déjà écrites,
            // et le prochain démarrage repartira d'une base vide.
            await global.Restaurants.Where(r => r.Id == restaurant.Id).ExecuteDeleteAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<ApplicationUser> GetOrCreateDemoUserAsync()
    {
        var settings = demo.Value;
        if (string.IsNullOrWhiteSpace(settings.Email) || string.IsNullOrWhiteSpace(settings.Password))
            throw new InvalidOperationException("La section de configuration « Demo » (Email, Password) est incomplète.");

        // Un utilisateur déjà créé par une tentative précédente (interrompue avant la fin) est réutilisé.
        if (await users.FindByEmailAsync(settings.Email) is { } existing)
            return existing;

        var user = new ApplicationUser { UserName = settings.Email, Email = settings.Email, EmailConfirmed = true };
        EnsureSucceeded(await users.CreateAsync(user, settings.Password), "Impossible de créer l'utilisateur de démo");
        return user;
    }

    private static void EnsureSucceeded(IdentityResult result, string message)
    {
        if (!result.Succeeded)
            throw new InvalidOperationException($"{message} : {string.Join(" ", result.Errors.Select(e => e.Description))}");
    }

    private static async Task SeedBusinessDataAsync(ChefOsDbContext db, DateTime now, CancellationToken ct)
    {
        var suppliers = AddSuppliers(db);
        var ingredients = AddIngredients(db, suppliers);
        // Les identifiants des ingrédients sont nécessaires à l'historique de prix (sans propriété de navigation).
        await db.SaveChangesAsync(ct);

        AddPriceHistory(db, ingredients, now);
        var pantry = new Pantry(ingredients);
        var preparations = AddPreparations(db, pantry);
        AddDishes(db, pantry, preparations);
        await db.SaveChangesAsync(ct);
    }

    // ── Fournisseurs ─────────────────────────────────────────────────────────

    private static (Supplier Butcher, Supplier Fishmonger, Supplier Grocer) AddSuppliers(ChefOsDbContext db)
    {
        var butcher = new Supplier
        {
            Name = "Boucherie Van der Meulen",
            ContactName = "Pierre Van der Meulen",
            Email = "commandes@vandermeulen.example",
            Phone = "+32 2 555 01 10"
        };
        var fishmonger = new Supplier
        {
            Name = "Poissonnerie Noordzee",
            ContactName = "Sofie Claes",
            Email = "maree@noordzee.example",
            Phone = "+32 50 55 01 20"
        };
        var grocer = new Supplier
        {
            Name = "Brabant Primeurs & Épicerie",
            ContactName = "Karim Benali",
            Email = "livraisons@brabant-primeurs.example",
            Phone = "+32 2 555 01 30"
        };

        db.Suppliers.AddRange(butcher, fishmonger, grocer);
        return (butcher, fishmonger, grocer);
    }

    // ── Ingrédients ──────────────────────────────────────────────────────────

    /// <summary>
    /// Prix de gros belges (€ HTVA) exprimés par kg, par litre ou par pièce selon l'unité de base ;
    /// le rendement est la part utilisable après parage, épluchage ou désarêtage.
    /// </summary>
    private static Dictionary<string, Ingredient> AddIngredients(
        ChefOsDbContext db, (Supplier Butcher, Supplier Fishmonger, Supplier Grocer) suppliers)
    {
        var (butcher, fishmonger, grocer) = suppliers;
        var catalog = new Dictionary<string, Ingredient>();

        void Add(string name, string category, Supplier supplier, BaseUnit unit, decimal price, decimal yield,
            EuAllergen allergens = EuAllergen.None, decimal? pieceWeightGrams = null)
        {
            var ingredient = new Ingredient
            {
                Name = name,
                Category = category,
                Supplier = supplier,
                BaseUnit = unit,
                CurrentPricePerBaseUnit = UnitConverter.PricePerBaseFromDisplay(price, unit),
                YieldPercent = yield,
                Allergens = allergens,
                AveragePieceWeightGrams = pieceWeightGrams
            };
            catalog.Add(name, ingredient);
            db.Ingredients.Add(ingredient);
        }

        const BaseUnit g = BaseUnit.Gram;
        const BaseUnit ml = BaseUnit.Milliliter;
        const BaseUnit piece = BaseUnit.Piece;

        // Viandes et charcuterie
        Add("Paleron de bœuf", Meats, butcher, g, 14.50m, 88m);
        Add("Bœuf haché maigre", Meats, butcher, g, 14.50m, 100m);
        Add("Haché porc-bœuf", Meats, butcher, g, 8.90m, 100m);
        Add("Filet de poulet fermier", Meats, butcher, g, 11.50m, 95m);
        Add("Lapin désossé", Meats, butcher, g, 17.50m, 90m);
        Add("Lardons fumés", Meats, butcher, g, 9.80m, 100m);
        Add("Jambon cuit supérieur", Meats, butcher, g, 11.50m, 95m);
        Add("Os de veau", Meats, butcher, g, 3.20m, 100m);

        // Poissons et fruits de mer (poisson entier : rendement filet)
        Add("Moules de Zélande", Seafood, fishmonger, g, 3.90m, 92m, EuAllergen.Molluscs);
        Add("Crevettes grises décortiquées", Seafood, fishmonger, g, 62.00m, 100m, EuAllergen.Crustaceans);
        Add("Cabillaud entier", Seafood, fishmonger, g, 10.50m, 55m, EuAllergen.Fish);

        // Légumes, fruits, herbes
        Add("Oignons jaunes", Vegetables, grocer, g, 1.10m, 88m);
        Add("Carottes", Vegetables, grocer, g, 1.00m, 85m);
        Add("Céleri-branche", Vegetables, grocer, g, 2.20m, 85m, EuAllergen.Celery);
        Add("Poireaux", Vegetables, grocer, g, 2.40m, 80m);
        Add("Pommes de terre Bintje", Vegetables, grocer, g, 0.95m, 80m);
        Add("Échalotes", Vegetables, grocer, g, 3.20m, 85m);
        Add("Ail", Vegetables, grocer, g, 7.00m, 85m);
        Add("Champignons de Paris", Vegetables, grocer, g, 3.80m, 90m);
        Add("Chicons", Vegetables, grocer, g, 2.40m, 85m);
        Add("Haricots verts", Vegetables, grocer, g, 4.50m, 90m);
        Add("Salade verte", Vegetables, grocer, g, 4.80m, 85m);
        Add("Fraises", Fruits, grocer, g, 7.50m, 93m);
        Add("Citron", Fruits, grocer, piece, 0.30m, 100m, pieceWeightGrams: 120m);
        Add("Persil plat", Herbs, grocer, g, 11.00m, 75m);
        Add("Thym frais", Herbs, grocer, g, 22.00m, 70m);
        Add("Laurier séché", Herbs, grocer, g, 18.00m, 100m);

        // Produits laitiers et œufs
        Add("Beurre doux", Dairy, grocer, g, 9.20m, 100m, EuAllergen.Milk);
        Add("Crème fraîche liquide 35 %", Dairy, grocer, ml, 5.80m, 100m, EuAllergen.Milk);
        Add("Lait entier", Dairy, grocer, ml, 1.10m, 100m, EuAllergen.Milk);
        Add("Gruyère râpé", Dairy, grocer, g, 11.50m, 100m, EuAllergen.Milk);
        Add("Glace vanille", Dairy, grocer, ml, 7.50m, 100m, EuAllergen.Milk | EuAllergen.Eggs);
        Add("Œufs frais", Dairy, grocer, piece, 0.30m, 100m, EuAllergen.Eggs, pieceWeightGrams: 60m);

        // Épicerie
        Add("Farine de blé", Grocery, grocer, g, 1.20m, 100m, EuAllergen.Gluten);
        Add("Cassonade", Grocery, grocer, g, 2.20m, 100m);
        Add("Sucre", Grocery, grocer, g, 1.40m, 100m);
        Add("Sirop de Liège", Grocery, grocer, g, 6.20m, 100m);
        Add("Pain d'épices", Grocery, grocer, g, 5.50m, 100m, EuAllergen.Gluten);
        Add("Moutarde de Dijon", Grocery, grocer, g, 5.60m, 100m, EuAllergen.Mustard);
        Add("Chapelure", Grocery, grocer, g, 2.80m, 100m, EuAllergen.Gluten);
        Add("Huile de tournesol", Grocery, grocer, ml, 2.20m, 100m);
        Add("Vinaigre de vin", Grocery, grocer, ml, 1.80m, 100m, EuAllergen.Sulphites);
        Add("Câpres", Grocery, grocer, g, 9.00m, 100m);
        Add("Cornichons", Grocery, grocer, g, 3.50m, 100m);
        Add("Sauce Worcestershire", Grocery, grocer, ml, 7.00m, 100m, EuAllergen.Fish);
        Add("Concentré de tomate", Grocery, grocer, g, 3.50m, 100m);
        Add("Levure de boulanger", Grocery, grocer, g, 6.00m, 100m);
        Add("Pruneaux d'Agen", Grocery, grocer, g, 6.50m, 100m);
        Add("Bouchée feuilletée", Grocery, grocer, piece, 0.90m, 100m, EuAllergen.Gluten | EuAllergen.Eggs | EuAllergen.Milk);

        // Boissons de cuisine
        Add("Bière brune d'abbaye", Drinks, grocer, ml, 3.60m, 100m, EuAllergen.Gluten);
        Add("Gueuze", Drinks, grocer, ml, 2.60m, 100m, EuAllergen.Gluten | EuAllergen.Sulphites);
        Add("Vin blanc sec", Drinks, grocer, ml, 4.20m, 100m, EuAllergen.Sulphites);

        return catalog;
    }

    // ── Historique de prix ───────────────────────────────────────────────────

    /// <summary>
    /// Chaque ingrédient reçoit une entrée pour son prix courant (la plus récente). Les ingrédients volatils ont en plus
    /// quatre points sur les 90 derniers jours : (jours écoulés, prix d'alors dans l'unité d'affichage).
    /// </summary>
    private static void AddPriceHistory(ChefOsDbContext db, IReadOnlyDictionary<string, Ingredient> catalog, DateTime now)
    {
        var earlierPrices = new Dictionary<string, (int DaysAgo, decimal Price)[]>
        {
            ["Crevettes grises décortiquées"] = [(86, 57.50m), (58, 59.00m), (30, 64.00m)],
            ["Beurre doux"] = [(88, 8.40m), (60, 8.70m), (31, 9.00m)],
            ["Moules de Zélande"] = [(87, 4.60m), (59, 4.20m), (32, 3.60m)],
            ["Paleron de bœuf"] = [(85, 13.60m), (57, 13.90m), (29, 14.20m)],
            ["Œufs frais"] = [(86, 0.26m), (58, 0.28m), (30, 0.29m)],
            ["Huile de tournesol"] = [(84, 2.60m), (56, 2.40m), (28, 2.30m)]
        };

        void Record(Ingredient ingredient, DateTime date, decimal pricePerBaseUnit, PriceSource source) =>
            db.IngredientPrices.Add(new IngredientPrice
            {
                IngredientId = ingredient.Id,
                Date = date,
                PricePerBaseUnit = pricePerBaseUnit,
                SupplierId = ingredient.SupplierId,
                Source = source
            });

        foreach (var (name, ingredient) in catalog)
        {
            Record(ingredient, now.AddDays(-3), ingredient.CurrentPricePerBaseUnit!.Value, PriceSource.Manual);

            if (!earlierPrices.TryGetValue(name, out var earlier))
                continue;

            foreach (var (daysAgo, price) in earlier)
                Record(ingredient, now.AddDays(-daysAgo), UnitConverter.PricePerBaseFromDisplay(price, ingredient.BaseUnit), PriceSource.Receipt);
        }
    }

    // ── Préparations ─────────────────────────────────────────────────────────

    private sealed record Preparations(Recipe FondDeVeau, Recipe Mayonnaise, Recipe SauceLapin, Recipe Puree, Recipe PateGaufre);

    private static Preparations AddPreparations(ChefOsDbContext db, Pantry p)
    {
        var fondDeVeau = Preparation("Fond de veau", "Fonds et sauces", 2000m, BaseUnit.Milliliter,
            "Rôtir les os 45 min à 220 °C avec le concentré de tomate, mouiller à l'eau froide avec les légumes et le bouquet garni, " +
            "mijoter 6 h en écumant, passer et réduire à 2 litres.",
            p.G("Os de veau", 2500), p.G("Carottes", 300), p.G("Oignons jaunes", 300), p.G("Céleri-branche", 150),
            p.G("Concentré de tomate", 60), p.G("Thym frais", 6), p.G("Laurier séché", 2));

        var mayonnaise = Preparation("Mayonnaise maison", "Fonds et sauces", 450m, BaseUnit.Gram,
            "Monter les œufs avec la moutarde, incorporer l'huile en filet, finir au vinaigre. Se conserve 48 h au frais.",
            p.Pc("Œufs frais", 3), p.G("Moutarde de Dijon", 25), p.Ml("Huile de tournesol", 400), p.Ml("Vinaigre de vin", 15));

        var sauceLapin = Preparation("Sauce lapin à la bière", "Fonds et sauces", 1000m, BaseUnit.Milliliter,
            "Suer les oignons au beurre, singer, mouiller à la gueuze et au fond de veau, ajouter pruneaux, sirop de Liège, " +
            "moutarde et laurier, mijoter 1 h et passer.",
            p.G("Oignons jaunes", 400), p.G("Beurre doux", 40), p.G("Farine de blé", 40), p.Ml("Gueuze", 500),
            Use(fondDeVeau, 300, MeasureUnit.Ml), p.G("Pruneaux d'Agen", 120), p.G("Sirop de Liège", 50),
            p.G("Moutarde de Dijon", 30), p.G("Laurier séché", 2));

        var puree = Preparation("Purée", "Garnitures", 1900m, BaseUnit.Gram,
            "Cuire les pommes de terre à l'eau salée, les écraser au presse-purée, monter au beurre et au lait chaud.",
            p.G("Pommes de terre Bintje", 1500), p.G("Beurre doux", 200), p.Ml("Lait entier", 300));

        var pateGaufre = Preparation("Pâte à gaufre", "Pâtisserie", 1400m, BaseUnit.Gram,
            "Mélanger la farine, la levure et le sucre, ajouter le lait, les œufs et le beurre fondu, laisser reposer 1 h.",
            p.G("Farine de blé", 500), p.Ml("Lait entier", 500), p.Pc("Œufs frais", 4), p.G("Beurre doux", 100),
            p.G("Sucre", 50), p.G("Levure de boulanger", 15));

        db.Recipes.AddRange(fondDeVeau, mayonnaise, sauceLapin, puree, pateGaufre);
        return new Preparations(fondDeVeau, mayonnaise, sauceLapin, puree, pateGaufre);
    }

    // ── Plats ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Une portion par plat, prix TTC à 12 % de TVA. Les croquettes de crevettes grises sont volontairement chères
    /// (au-dessus du seuil d'alerte de 35 %) pour peupler la liste « à surveiller » du tableau de bord.
    /// </summary>
    private static void AddDishes(ChefOsDbContext db, Pantry p, Preparations prep)
    {
        db.Recipes.AddRange(
            Dish("Carbonnade flamande", "Plats", 22.50m,
                p.G("Paleron de bœuf", 220), p.G("Oignons jaunes", 120), p.Ml("Bière brune d'abbaye", 150),
                Use(prep.FondDeVeau, 100, MeasureUnit.Ml), p.G("Pain d'épices", 20), p.G("Moutarde de Dijon", 10),
                p.G("Cassonade", 10), p.G("Pommes de terre Bintje", 280), p.Ml("Huile de tournesol", 30)),

            Dish("Boulets liégeois", "Plats", 18.50m,
                p.G("Haché porc-bœuf", 300), p.G("Chapelure", 30), p.Pc("Œufs frais", 0.5m), p.G("Oignons jaunes", 40),
                p.G("Persil plat", 5), Use(prep.SauceLapin, 150, MeasureUnit.Ml), p.G("Pommes de terre Bintje", 280),
                p.Ml("Huile de tournesol", 30), p.G("Salade verte", 40)),

            Dish("Vol-au-vent", "Plats", 21.00m,
                p.G("Filet de poulet fermier", 150), p.G("Haché porc-bœuf", 60), p.G("Champignons de Paris", 80),
                Use(prep.FondDeVeau, 150, MeasureUnit.Ml), p.Ml("Crème fraîche liquide 35 %", 60), p.G("Beurre doux", 25),
                p.G("Farine de blé", 20), p.Pc("Bouchée feuilletée", 1), p.G("Pommes de terre Bintje", 250),
                p.Ml("Huile de tournesol", 30)),

            Dish("Moules-frites", "Plats", 23.50m,
                p.G("Moules de Zélande", 1000), p.G("Céleri-branche", 40), p.G("Oignons jaunes", 60),
                p.Ml("Vin blanc sec", 100), p.G("Beurre doux", 20), p.G("Persil plat", 5),
                Use(prep.Mayonnaise, 40, MeasureUnit.G), p.G("Pommes de terre Bintje", 300), p.Ml("Huile de tournesol", 30)),

            Dish("Américain préparé", "Plats", 19.50m,
                p.G("Bœuf haché maigre", 220), Use(prep.Mayonnaise, 40, MeasureUnit.G), p.G("Oignons jaunes", 20),
                p.G("Câpres", 10), p.G("Cornichons", 15), p.Ml("Sauce Worcestershire", 5), p.G("Moutarde de Dijon", 5),
                p.Pc("Œufs frais", 1), p.G("Salade verte", 40), p.G("Pommes de terre Bintje", 300),
                p.Ml("Huile de tournesol", 30), p.G("Persil plat", 3)),

            Dish("Croquettes de crevettes", "Entrées", 24.00m,
                p.G("Crevettes grises décortiquées", 100), p.G("Beurre doux", 40), p.G("Farine de blé", 40),
                p.Ml("Lait entier", 250), p.G("Chapelure", 40), p.Pc("Œufs frais", 1), p.Ml("Huile de tournesol", 80),
                p.G("Persil plat", 10), p.Pc("Citron", 0.5m), p.G("Salade verte", 40)),

            Dish("Lapin à la bière", "Plats", 25.50m,
                p.G("Lapin désossé", 200), Use(prep.SauceLapin, 180, MeasureUnit.Ml), Use(prep.Puree, 200, MeasureUnit.G),
                p.G("Lardons fumés", 40), p.G("Haricots verts", 80), p.G("Beurre doux", 10)),

            Dish("Waterzooi de poulet", "Plats", 22.00m,
                p.G("Filet de poulet fermier", 180), p.G("Poireaux", 80), p.G("Carottes", 80), p.G("Céleri-branche", 40),
                p.G("Pommes de terre Bintje", 120), p.Ml("Crème fraîche liquide 35 %", 80), p.Pc("Œufs frais", 1),
                p.G("Beurre doux", 20), p.G("Persil plat", 3), Use(prep.FondDeVeau, 200, MeasureUnit.Ml)),

            Dish("Cabillaud beurre blanc", "Plats", 25.50m,
                p.G("Cabillaud entier", 180), p.G("Échalotes", 30), p.Ml("Vin blanc sec", 60), p.G("Beurre doux", 50),
                p.Ml("Crème fraîche liquide 35 %", 40), Use(prep.Puree, 200, MeasureUnit.G), p.G("Haricots verts", 80),
                p.Pc("Citron", 0.25m)),

            Dish("Chicons au gratin", "Plats", 17.50m,
                p.G("Chicons", 300), p.G("Jambon cuit supérieur", 120), p.G("Beurre doux", 30), p.G("Farine de blé", 30),
                p.Ml("Lait entier", 300), p.G("Gruyère râpé", 50), Use(prep.Puree, 200, MeasureUnit.G)),

            Dish("Salade liégeoise", "Entrées", 14.50m,
                p.G("Haricots verts", 150), p.G("Pommes de terre Bintje", 250), p.G("Lardons fumés", 120),
                p.Pc("Œufs frais", 1), p.G("Salade verte", 50), p.G("Échalotes", 20), p.Ml("Vinaigre de vin", 15),
                p.Ml("Huile de tournesol", 30), p.G("Moutarde de Dijon", 5)),

            Dish("Gaufre de Bruxelles", "Desserts", 9.50m,
                Use(prep.PateGaufre, 150, MeasureUnit.G), p.Ml("Glace vanille", 90), p.G("Fraises", 100),
                p.Ml("Crème fraîche liquide 35 %", 50), p.G("Sucre", 10)));
    }

    // ── Aides de construction ────────────────────────────────────────────────

    private static Recipe Preparation(string name, string category, decimal outputQuantity, BaseUnit outputUnit,
        string instructions, params RecipeLine[] lines) => new()
    {
        Name = name,
        Kind = RecipeKind.Preparation,
        Category = category,
        OutputQuantity = outputQuantity,
        OutputUnit = outputUnit,
        Instructions = instructions,
        Lines = [.. lines]
    };

    private static Recipe Dish(string name, string category, decimal sellingPriceInclVat, params RecipeLine[] lines) => new()
    {
        Name = name,
        Kind = RecipeKind.Dish,
        Category = category,
        SellingPriceInclVat = sellingPriceInclVat,
        VatRate = FoodVatRate,
        Lines = [.. lines]
    };

    /// <summary>Ligne qui utilise une préparation (sous-recette).</summary>
    private static RecipeLine Use(Recipe preparation, decimal quantity, MeasureUnit unit) =>
        new() { SubRecipe = preparation, Quantity = quantity, Unit = unit };

    /// <summary>Retrouve les ingrédients par nom pour écrire les lignes de recette comme on les lit en cuisine.</summary>
    private sealed class Pantry(IReadOnlyDictionary<string, Ingredient> ingredients)
    {
        public RecipeLine G(string name, decimal grams) => Line(name, grams, MeasureUnit.G);

        public RecipeLine Ml(string name, decimal milliliters) => Line(name, milliliters, MeasureUnit.Ml);

        public RecipeLine Pc(string name, decimal pieces) => Line(name, pieces, MeasureUnit.Piece);

        private RecipeLine Line(string name, decimal quantity, MeasureUnit unit) =>
            new() { Ingredient = ingredients[name], Quantity = quantity, Unit = unit };
    }
}
