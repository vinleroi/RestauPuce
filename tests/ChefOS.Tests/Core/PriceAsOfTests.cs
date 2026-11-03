using System.Reflection;
using ChefOS.Core.Ingredients;
using ChefOS.Core.Sales;

namespace ChefOS.Tests.Core;

public class PriceAsOfTests
{
    private static IngredientPrice Price(int id, int ingredientId, DateTime date, decimal price) =>
        new() { Id = id, IngredientId = ingredientId, Date = date, PricePerBaseUnit = price };

    private static readonly DateTime Jan1 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jan10 = new(2026, 1, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jan15 = new(2026, 1, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Jan20 = new(2026, 1, 20, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PriceAsOf_LatestEntryOnOrBeforeDay()
    {
        // Entrées du 1er, du 10 et du 20 ; le 15 → prix du 10.
        // L'ordre de la liste est volontairement mélangé : seul `Date` compte.
        var history = new[]
        {
            Price(3, 1, Jan20, 30m),
            Price(1, 1, Jan1, 10m),
            Price(2, 1, Jan10, 20m),
        };

        Assert.Equal(20m, PriceAsOf.At(history, Jan15, currentPrice: 99m));
    }

    [Fact]
    public void PriceAsOf_SameDayLaterTimeCounts()
    {
        // Une entrée à 18 h le jour même compte, même si on interroge le jour à 00:00.
        var history = new[]
        {
            Price(1, 1, Jan10, 20m),
            Price(2, 1, Jan15.AddHours(18), 25m),
        };

        Assert.Equal(25m, PriceAsOf.At(history, Jan15, currentPrice: 99m));
        // ... et la même entrée ne compte pas encore la veille.
        Assert.Equal(20m, PriceAsOf.At(history, Jan15.AddDays(-1), currentPrice: 99m));
    }

    [Fact]
    public void PriceAsOf_NoEntryBefore_UsesCurrent()
    {
        var history = new[] { Price(1, 1, Jan20, 30m) };

        Assert.Equal(99m, PriceAsOf.At(history, Jan15, currentPrice: 99m));
        Assert.Equal(99m, PriceAsOf.At([], Jan15, currentPrice: 99m));
        Assert.Null(PriceAsOf.At([], Jan15, currentPrice: null));
    }

    [Fact]
    public void PriceAsOf_SameDate_HigherIdWins()
    {
        var history = new[]
        {
            Price(7, 1, Jan10, 70m),
            Price(9, 1, Jan10, 90m),
            Price(8, 1, Jan10, 80m),
        };

        Assert.Equal(90m, PriceAsOf.At(history, Jan15, currentPrice: null));
    }

    [Fact]
    public void PriceAsOf_WithPricesAt_ReturnsCopies()
    {
        var salmon = new Ingredient { Id = 1, Name = "Saumon", CurrentPricePerBaseUnit = 99m };
        var butter = new Ingredient { Id = 2, Name = "Beurre", CurrentPricePerBaseUnit = 5m };
        var oil = new Ingredient { Id = 3, Name = "Huile", CurrentPricePerBaseUnit = null };
        var history = new[]
        {
            Price(1, 1, Jan1, 10m),
            Price(2, 1, Jan10, 20m),
            Price(3, 1, Jan20, 30m),
            Price(4, 3, Jan1, 4m),
        }.ToLookup(p => p.IngredientId);

        var result = PriceAsOf.WithPricesAt([salmon, butter, oil], history, Jan15);

        Assert.Equal(3, result.Count);
        // Prix en vigueur au 15 ; sans historique, le prix courant de l'ingrédient est conservé.
        Assert.Equal(20m, result[0].CurrentPricePerBaseUnit);
        Assert.Equal(5m, result[1].CurrentPricePerBaseUnit);
        Assert.Equal(4m, result[2].CurrentPricePerBaseUnit);
        // Des copies : jamais les instances d'origine, qui restent intactes.
        Assert.NotSame(salmon, result[0]);
        Assert.NotSame(butter, result[1]);
        Assert.NotSame(oil, result[2]);
        Assert.Equal(99m, salmon.CurrentPricePerBaseUnit);
        Assert.Equal(5m, butter.CurrentPricePerBaseUnit);
        Assert.Null(oil.CurrentPricePerBaseUnit);
    }

    [Fact]
    public void PriceAsOf_WithPricesAt_PreservesInputOrder()
    {
        var a = new Ingredient { Id = 30 };
        var b = new Ingredient { Id = 10 };
        var c = new Ingredient { Id = 20 };

        var result = PriceAsOf.WithPricesAt([a, b, c], Array.Empty<IngredientPrice>().ToLookup(p => p.IngredientId), Jan15);

        Assert.Equal([30, 10, 20], result.Select(i => i.Id));
    }

    [Fact]
    public void PriceAsOf_WithPricesAt_CopiesPreserveAllOtherProperties()
    {
        // Chaque propriété inscriptible reçoit une valeur non par défaut (par réflexion) :
        // si une propriété est ajoutée plus tard à Ingredient et oubliée par la copie, ce test échoue.
        var original = new Ingredient();
        var properties = typeof(Ingredient)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToList();
        foreach (var property in properties)
            property.SetValue(original, NonDefaultValue(property));

        var history = new[] { Price(1, original.Id, Jan10, 20m) }.ToLookup(p => p.IngredientId);

        var copy = Assert.Single(PriceAsOf.WithPricesAt([original], history, Jan15));

        Assert.NotSame(original, copy);
        Assert.Equal(20m, copy.CurrentPricePerBaseUnit);
        Assert.NotEqual(20m, original.CurrentPricePerBaseUnit);
        foreach (var property in properties.Where(p => p.Name != nameof(Ingredient.CurrentPricePerBaseUnit)))
        {
            var expected = property.GetValue(original);
            var actual = property.GetValue(copy);
            Assert.True(
                Equals(expected, actual),
                $"La propriété {property.Name} n'est pas recopiée (attendu {expected ?? "null"}, obtenu {actual ?? "null"}).");
        }
    }

    private static object NonDefaultValue(PropertyInfo property)
    {
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (type == typeof(int)) return 7;
        if (type == typeof(bool)) return true;
        if (type == typeof(decimal)) return 3.5m;
        if (type == typeof(string)) return "valeur-" + property.Name;
        if (type.IsEnum)
            return Enum.GetValues(type).Cast<object>().Last(v => Convert.ToInt64(v) != 0);
        if (type == typeof(Supplier)) return new Supplier { Id = 4, Name = "Fournisseur" };
        throw new NotSupportedException(
            $"Type de propriété non géré par le test : {property.Name} ({type.Name}). Ajoutez-le à NonDefaultValue.");
    }
}
