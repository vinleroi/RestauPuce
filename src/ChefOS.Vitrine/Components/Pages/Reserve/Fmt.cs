using System.Globalization;

namespace ChefOS.Vitrine.Components.Pages.Reserve;

/// <summary>Formats français de Belgique indépendants de la culture du serveur : « 12,60 € », « 10 € », « 1 234,50 € » (espaces insécables).</summary>
public static class Fmt
{
    private const string Nbsp = " ";

    /// <summary>Montant en euros : sans décimales s'il est rond, avec deux sinon.</summary>
    public static string Money(decimal amount)
    {
        var whole = amount == decimal.Truncate(amount);
        var text = amount.ToString(whole ? "#,##0" : "#,##0.00", CultureInfo.InvariantCulture)
            .Replace(',', '\u0001').Replace('.', ',').Replace("\u0001", Nbsp);
        return text + Nbsp + "€";
    }

    public static string Covers(int count) => count == 1 ? "1 couvert" : $"{count} couverts";

    public static string LocalDateTime(DateOnly date, TimeOnly time) =>
        $"{date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)} à {time.ToString("HH\\:mm", CultureInfo.InvariantCulture)}";
}
