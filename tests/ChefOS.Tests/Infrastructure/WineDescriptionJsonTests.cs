using System.Text.Json;
using ChefOS.Core.Vitrine;
using ChefOS.Infrastructure.Wines;

namespace ChefOS.Tests.Infrastructure;

public class WineDescriptionJsonTests
{
    private const string Full =
        """{"appellation":"Saint-Émilion grand cru","region":"Bordeaux","country":"France","color":"Rouge","grapes":"Merlot, Cabernet franc","robe":"Grenat profond.","nez":"Fruits noirs.","bouche":"Ample et soyeuse.","garde":"5 à 10 ans.","service_temperature":"16-18 °C","general_pairings":"Viandes rouges.","suggested_pairing_dish_names":["Entrecôte","Magret"]}""";

    [Fact]
    public void Parse_ReadsEveryField()
    {
        var draft = WineDescriptionJson.Parse(Full);

        Assert.Equal("Saint-Émilion grand cru", draft.Appellation);
        Assert.Equal("Bordeaux", draft.Region);
        Assert.Equal("France", draft.Country);
        Assert.Equal(WineColor.Rouge, draft.Color);
        Assert.Equal("Merlot, Cabernet franc", draft.Grapes);
        Assert.Equal("Grenat profond.", draft.Robe);
        Assert.Equal("Fruits noirs.", draft.Nez);
        Assert.Equal("Ample et soyeuse.", draft.Bouche);
        Assert.Equal("5 à 10 ans.", draft.Garde);
        Assert.Equal("16-18 °C", draft.ServiceTemperature);
        Assert.Equal("Viandes rouges.", draft.GeneralPairings);
        Assert.Equal(["Entrecôte", "Magret"], draft.SuggestedPairingDishNames);
        Assert.Empty(draft.SuggestedPairingRecipeIds);
    }

    [Fact]
    public void Parse_NullAppellation_StaysNull()
    {
        var draft = WineDescriptionJson.Parse(Full.Replace("\"Saint-Émilion grand cru\"", "null"));

        Assert.Null(draft.Appellation);
    }

    [Theory]
    [InlineData("Rouge", WineColor.Rouge)]
    [InlineData("blanc", WineColor.Blanc)]
    [InlineData("Rose", WineColor.Rose)]
    [InlineData("Rosé", WineColor.Rose)]
    [InlineData("Effervescent", WineColor.Effervescent)]
    [InlineData("Doux", WineColor.Doux)]
    public void Parse_Color(string value, WineColor expected)
    {
        Assert.Equal(expected, WineDescriptionJson.Parse(Full.Replace("\"Rouge\"", $"\"{value}\"")).Color);
    }

    [Theory]
    [InlineData("""{"color":"Orange"}""")]
    [InlineData("[]")]
    [InlineData("pas du json")]
    [InlineData("")]
    public void Parse_Unusable_IsUnreadable(string json)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => WineDescriptionJson.Parse(json));

        Assert.Equal("Réponse de l'assistant illisible", ex.Message);
    }

    [Fact]
    public void Parse_TrimsAndClipsToTheColumnSizes()
    {
        var json = Full.Replace("\"Bordeaux\"", $"\"  {new string('r', 150)}  \"").Replace("\"France\"", "null");

        var draft = WineDescriptionJson.Parse(json);

        Assert.Equal(100, draft.Region.Length);
        Assert.Equal("", draft.Country);
    }

    [Fact]
    public void Parse_IgnoresBlankAndNonTextDishNames()
    {
        var json = Full.Replace("[\"Entrecôte\",\"Magret\"]", "[\" Entrecôte \",\"\",3,null]");

        Assert.Equal(["Entrecôte"], WineDescriptionJson.Parse(json).SuggestedPairingDishNames);
    }

    [Fact]
    public void Schema_IsStrict_AndRequiresEveryProperty()
    {
        var schema = JsonSerializer.SerializeToElement(WineDescriptionJson.Schema);

        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).ToHashSet();
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.Equal(properties, required);
        Assert.Equal(12, properties.Count);
    }
}
