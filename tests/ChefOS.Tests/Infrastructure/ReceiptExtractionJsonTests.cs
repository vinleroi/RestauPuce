using System.Globalization;
using System.Text.Json;
using ChefOS.Core.Units;
using ChefOS.Infrastructure.Receipts;

namespace ChefOS.Tests.Infrastructure;

/// <summary>Schéma envoyé à l'API et lecture de la réponse du lecteur : aucune de ces vérifications n'appelle l'API.</summary>
public class ReceiptExtractionJsonTests
{
    /// <summary>JSON d'une ligne, le champ testé étant le seul à varier.</summary>
    private static string OneLine(string line) =>
        $$"""{ "supplier_name": null, "date": null, "invoice_number": null, "amounts_include_vat": false, "total": null, "lines": [ {{line}} ] }""";

    private static string Line(string label = "\"BEURRE\"", string quantity = "1", string unit = "\"kg\"",
        string unitPrice = "1", string lineTotal = "1", string vatRate = "0.06") =>
        $$"""{ "label": {{label}}, "quantity": {{quantity}}, "unit": {{unit}}, "unit_price": {{unitPrice}}, "line_total": {{lineTotal}}, "vat_rate": {{vatRate}} }""";

    [Fact]
    public void Parse_FullResponse()
    {
        const string json = """
            {
              "supplier_name": "Metro Cash & Carry",
              "date": "2026-11-03",
              "invoice_number": "FA-2026-0042",
              "amounts_include_vat": true,
              "total": 82.15,
              "lines": [
                { "label": "OIGNONS JAUNES FILET 5KG", "quantity": 10, "unit": "kg", "unit_price": 1.12, "line_total": 11.2, "vat_rate": 0.06 },
                { "label": "SACS POUBELLE 100L", "quantity": 2, "unit": "piece", "unit_price": null, "line_total": 9.85, "vat_rate": 0.21 }
              ]
            }
            """;

        var extraction = ReceiptExtractionJson.Parse(json);

        Assert.Equal("Metro Cash & Carry", extraction.SupplierName);
        Assert.Equal(new DateOnly(2026, 11, 3), extraction.Date);
        Assert.Equal("FA-2026-0042", extraction.InvoiceNumber);
        Assert.True(extraction.AmountsIncludeVat);
        Assert.Equal(82.15m, extraction.Total);

        Assert.Equal(2, extraction.Lines.Count);

        var onions = extraction.Lines[0];
        Assert.Equal("OIGNONS JAUNES FILET 5KG", onions.Label);
        Assert.Equal(10m, onions.Quantity);
        Assert.Equal(MeasureUnit.Kg, onions.Unit);
        Assert.Equal(1.12m, onions.UnitPrice);
        Assert.Equal(11.2m, onions.LineTotal);
        Assert.Equal(0.06m, onions.VatRate);

        var bags = extraction.Lines[1];
        Assert.Equal("SACS POUBELLE 100L", bags.Label);
        Assert.Equal(2m, bags.Quantity);
        Assert.Equal(MeasureUnit.Piece, bags.Unit);
        Assert.Null(bags.UnitPrice);
        Assert.Equal(9.85m, bags.LineTotal);
        Assert.Equal(0.21m, bags.VatRate);
    }

    [Fact]
    public void Parse_NullsAndAbsentFields_BecomeNull()
    {
        var extraction = ReceiptExtractionJson.Parse(
            """{ "supplier_name": null, "lines": [ { "label": "LAIT ENTIER 1L" } ] }""");

        Assert.Null(extraction.SupplierName);
        Assert.Null(extraction.Date);
        Assert.Null(extraction.InvoiceNumber);
        Assert.False(extraction.AmountsIncludeVat);
        Assert.Null(extraction.Total);

        var line = Assert.Single(extraction.Lines);
        Assert.Equal("LAIT ENTIER 1L", line.Label);
        Assert.Null(line.Quantity);
        Assert.Null(line.Unit);
        Assert.Null(line.UnitPrice);
        Assert.Null(line.LineTotal);
        Assert.Null(line.VatRate);
    }

    [Fact]
    public void Parse_NoLines_GivesAnEmptyList()
    {
        var extraction = ReceiptExtractionJson.Parse("""{ "supplier_name": "Metro", "lines": [] }""");

        Assert.Empty(extraction.Lines);
    }

    [Fact]
    public void Parse_UnknownUnit_IsNull()
    {
        var extraction = ReceiptExtractionJson.Parse(OneLine(Line(unit: "\"carton\"")));

        Assert.Null(Assert.Single(extraction.Lines).Unit);
    }

    [Theory]
    [InlineData("g", MeasureUnit.G)]
    [InlineData("kg", MeasureUnit.Kg)]
    [InlineData("ml", MeasureUnit.Ml)]
    [InlineData("cl", MeasureUnit.Cl)]
    [InlineData("l", MeasureUnit.L)]
    [InlineData("piece", MeasureUnit.Piece)]
    [InlineData("KG", MeasureUnit.Kg)]
    [InlineData("Cl", MeasureUnit.Cl)]
    [InlineData(" L ", MeasureUnit.L)]
    [InlineData("pièce", MeasureUnit.Piece)]
    [InlineData("Pièce", MeasureUnit.Piece)]
    [InlineData("pc", MeasureUnit.Piece)]
    [InlineData("pcs", MeasureUnit.Piece)]
    public void Parse_Unit_IsMappedCaseInsensitively(string unit, MeasureUnit expected)
    {
        var extraction = ReceiptExtractionJson.Parse(OneLine(Line(unit: $"\"{unit}\"")));

        Assert.Equal(expected, Assert.Single(extraction.Lines).Unit);
    }

    [Theory]
    [InlineData("\"\"")]
    [InlineData("\"   \"")]
    [InlineData("\"u\"")]
    [InlineData("12")]
    [InlineData("true")]
    public void Parse_UnrecognisedUnit_IsNull(string unit)
    {
        var extraction = ReceiptExtractionJson.Parse(OneLine(Line(unit: unit)));

        Assert.Null(Assert.Single(extraction.Lines).Unit);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("{ \"supplier_name\": ")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"texte\"")]
    [InlineData("""{ "lines": 3 }""")]
    [InlineData("""{ "lines": { "label": "X" } }""")]
    [InlineData("""{ "lines": [ "BEURRE" ] }""")]
    public void Parse_InvalidJson_Throws(string json)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ReceiptExtractionJson.Parse(json));

        Assert.Equal("Réponse de lecture illisible", ex.Message);
    }

    [Fact]
    public void Parse_NullJson_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => ReceiptExtractionJson.Parse(null!));

        Assert.Equal("Réponse de lecture illisible", ex.Message);
    }

    [Theory]
    [InlineData("6")]
    [InlineData("21")]
    [InlineData("-0.06")]
    [InlineData("1.01")]
    [InlineData("1000")]
    public void Parse_VatRateOutsideZeroToOne_IsNull(string vatRate)
    {
        var extraction = ReceiptExtractionJson.Parse(OneLine(Line(vatRate: vatRate)));

        Assert.Null(Assert.Single(extraction.Lines).VatRate);
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("0.06", 0.06)]
    [InlineData("0.21", 0.21)]
    [InlineData("1", 1.0)]
    public void Parse_VatRateInsideZeroToOne_IsKept(string vatRate, double expected)
    {
        var extraction = ReceiptExtractionJson.Parse(OneLine(Line(vatRate: vatRate)));

        Assert.Equal((decimal)expected, Assert.Single(extraction.Lines).VatRate);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-0.001")]
    [InlineData("100000000000000")]      // 1e14 : déborde la colonne des quantités
    [InlineData("1e40")]                 // hors de portée d'un decimal
    public void Parse_AbsurdQuantity_IsNull(string quantity)
    {
        var extraction = ReceiptExtractionJson.Parse(OneLine(Line(quantity: quantity)));

        Assert.Null(Assert.Single(extraction.Lines).Quantity);
    }

    [Theory]
    [InlineData("0", 0.0)]
    [InlineData("99999999999999", 99999999999999.0)]   // juste sous 1e14
    public void Parse_QuantityInsideBounds_IsKept(string quantity, double expected)
    {
        var extraction = ReceiptExtractionJson.Parse(OneLine(Line(quantity: quantity)));

        Assert.Equal((decimal)expected, Assert.Single(extraction.Lines).Quantity);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("-0.01")]
    [InlineData("10000000000000000")]    // 1e16 : déborde les colonnes de montants
    [InlineData("1e40")]
    public void Parse_AbsurdAmounts_AreNull(string amount)
    {
        var json = $$"""
            { "supplier_name": null, "date": null, "invoice_number": null, "amounts_include_vat": false, "total": {{amount}},
              "lines": [ {{Line(unitPrice: amount, lineTotal: amount)}} ] }
            """;

        var extraction = ReceiptExtractionJson.Parse(json);

        Assert.Null(extraction.Total);
        var line = Assert.Single(extraction.Lines);
        Assert.Null(line.UnitPrice);
        Assert.Null(line.LineTotal);
    }

    [Fact]
    public void Parse_AmountInsideBounds_IsKept()
    {
        var json = $$"""
            { "supplier_name": null, "date": null, "invoice_number": null, "amounts_include_vat": false, "total": 0,
              "lines": [ {{Line(unitPrice: "9999999999999999.99", lineTotal: "0")}} ] }
            """;

        var extraction = ReceiptExtractionJson.Parse(json);

        Assert.Equal(0m, extraction.Total);
        var line = Assert.Single(extraction.Lines);
        Assert.Equal(9999999999999999.99m, line.UnitPrice);
        Assert.Equal(0m, line.LineTotal);
    }

    [Fact]
    public void Parse_Labels_AreTrimmedAndEmptyOnesDropped()
    {
        var json = $$"""
            { "supplier_name": null, "date": null, "invoice_number": null, "amounts_include_vat": false, "total": null,
              "lines": [ {{Line(label: "\"  BEURRE DOUX 1KG \"")}}, {{Line(label: "\"\"")}}, {{Line(label: "\"   \"")}},
                         {{Line(label: "null")}}, {{Line(label: "\"CREME 1L\"")}} ] }
            """;

        var extraction = ReceiptExtractionJson.Parse(json);

        Assert.Equal(["BEURRE DOUX 1KG", "CREME 1L"], extraction.Lines.Select(l => l.Label));
    }

    [Fact]
    public void Parse_HeaderTexts_AreTrimmedAndBlankOnesBecomeNull()
    {
        var extraction = ReceiptExtractionJson.Parse(
            """{ "supplier_name": "  Metro  ", "invoice_number": "   ", "date": null, "lines": [] }""");

        Assert.Equal("Metro", extraction.SupplierName);
        Assert.Null(extraction.InvoiceNumber);
    }

    [Theory]
    [InlineData("03/11/2026")]
    [InlineData("2026-13-40")]
    [InlineData("2026-11-3")]
    [InlineData("hier")]
    [InlineData("")]
    public void Parse_DateNotInIsoFormat_IsNull(string date)
    {
        var extraction = ReceiptExtractionJson.Parse($$"""{ "date": "{{date}}", "lines": [] }""");

        Assert.Null(extraction.Date);
    }

    [Fact]
    public void Parse_Decimals_AreReadInInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("fr-BE");
        try
        {
            var extraction = ReceiptExtractionJson.Parse(
                """{ "total": 1234.56, "date": "2026-11-03", "lines": [ { "label": "X", "quantity": 2.5, "unit_price": 3.75, "line_total": 9.375, "vat_rate": 0.125 } ] }""");

            Assert.Equal(1234.56m, extraction.Total);
            Assert.Equal(new DateOnly(2026, 11, 3), extraction.Date);
            var line = Assert.Single(extraction.Lines);
            Assert.Equal(2.5m, line.Quantity);
            Assert.Equal(3.75m, line.UnitPrice);
            Assert.Equal(9.375m, line.LineTotal);
            Assert.Equal(0.125m, line.VatRate);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Schema_IsStrictObject()
    {
        var schema = ReceiptExtractionJson.Schema;

        Assert.Equal("object", schema["type"].GetString());
        Assert.False(schema["additionalProperties"].GetBoolean());

        string[] fields = ["supplier_name", "date", "invoice_number", "amounts_include_vat", "total", "lines"];
        Assert.Equal(fields.Order(), schema["properties"].EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(fields.Order(), schema["required"].EnumerateArray().Select(e => e.GetString()!).Order());
    }

    [Fact]
    public void Schema_Lines_AreStrictObjectsWithEveryFieldRequired()
    {
        var lines = ReceiptExtractionJson.Schema["properties"].GetProperty("lines");
        Assert.Equal("array", lines.GetProperty("type").GetString());

        var item = lines.GetProperty("items");
        Assert.Equal("object", item.GetProperty("type").GetString());
        Assert.False(item.GetProperty("additionalProperties").GetBoolean());

        string[] fields = ["label", "quantity", "unit", "unit_price", "line_total", "vat_rate"];
        Assert.Equal(fields.Order(), item.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(fields.Order(), item.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order());
    }

    [Fact]
    public void Schema_UnitEnum_ListsTheSixUnitsTheParserMaps()
    {
        var unit = ReceiptExtractionJson.Schema["properties"].GetProperty("lines").GetProperty("items")
            .GetProperty("properties").GetProperty("unit");

        var values = new List<string>();
        CollectEnumValues(unit, values);

        Assert.Equal(["cl", "g", "kg", "l", "ml", "piece"], values.Order());

        // Chaque valeur annoncée à l'API est comprise par le parseur.
        foreach (var value in values)
        {
            var extraction = ReceiptExtractionJson.Parse(OneLine(Line(unit: $"\"{value}\"")));
            Assert.NotNull(Assert.Single(extraction.Lines).Unit);
        }
    }

    [Fact]
    public void Schema_NullableFields_AllowNull()
    {
        var properties = ReceiptExtractionJson.Schema["properties"];
        var lineProperties = properties.GetProperty("lines").GetProperty("items").GetProperty("properties");

        foreach (var nullable in new[]
                 {
                     properties.GetProperty("supplier_name"), properties.GetProperty("date"), properties.GetProperty("invoice_number"),
                     properties.GetProperty("total"), lineProperties.GetProperty("quantity"), lineProperties.GetProperty("unit"),
                     lineProperties.GetProperty("unit_price"), lineProperties.GetProperty("line_total"), lineProperties.GetProperty("vat_rate")
                 })
            Assert.True(AllowsNull(nullable), nullable.GetRawText());

        Assert.False(AllowsNull(properties.GetProperty("amounts_include_vat")));
        Assert.False(AllowsNull(lineProperties.GetProperty("label")));
    }

    private static bool AllowsNull(JsonElement property) =>
        property.TryGetProperty("anyOf", out var options)
        && options.EnumerateArray().Any(o => o.TryGetProperty("type", out var type) && type.GetString() == "null");

    private static void CollectEnumValues(JsonElement element, List<string> values)
    {
        if (element.TryGetProperty("enum", out var enumerated))
            values.AddRange(enumerated.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!));

        if (element.TryGetProperty("anyOf", out var options))
            foreach (var option in options.EnumerateArray())
                CollectEnumValues(option, values);
    }
}
