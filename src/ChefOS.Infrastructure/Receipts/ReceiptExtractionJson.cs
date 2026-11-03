using System.Globalization;
using System.Text.Json;
using ChefOS.Core.Receipts;
using ChefOS.Core.Units;

namespace ChefOS.Infrastructure.Receipts;

/// <summary>
/// Contrat JSON entre ChefOS et le lecteur IA : <see cref="Schema"/> est le schéma envoyé à l'API (sortie structurée), <see cref="Parse"/> lit la réponse.
/// Le schéma est strict (objets fermés, tous les champs présents, valeur <c>null</c> quand le ticket ne dit rien), mais <see cref="Parse"/> ne s'y fie pas :
/// il normalise ce que le lecteur renvoie pour que <see cref="Services.ReceiptService"/> ne reçoive que des valeurs que la base accepte.
/// </summary>
public static class ReceiptExtractionJson
{
    internal const string UnreadableMessage = "Réponse de lecture illisible";

    // Colonnes numeric : quantités sur 14 chiffres entiers, montants sur 16. Au-delà, l'enregistrement du ticket échouerait.
    private const decimal QuantityLimit = 100_000_000_000_000m;
    private const decimal AmountLimit = 10_000_000_000_000_000m;

    private const string SchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["supplier_name", "date", "invoice_number", "amounts_include_vat", "total", "lines"],
          "properties": {
            "supplier_name": {
              "description": "Nom du fournisseur tel qu'imprimé en en-tête du ticket ; null s'il est illisible.",
              "anyOf": [{ "type": "string" }, { "type": "null" }]
            },
            "date": {
              "description": "Date du ticket au format yyyy-MM-dd ; null si elle est illisible.",
              "anyOf": [{ "type": "string" }, { "type": "null" }]
            },
            "invoice_number": {
              "description": "Numéro de facture ou de ticket ; null s'il n'y en a pas.",
              "anyOf": [{ "type": "string" }, { "type": "null" }]
            },
            "amounts_include_vat": {
              "description": "true si les prix et totaux du ticket sont TTC (TVA comprise), false s'ils sont hors TVA.",
              "type": "boolean"
            },
            "total": {
              "description": "Total du ticket dans la même base que les lignes (TTC ou hors TVA) ; null s'il est illisible.",
              "anyOf": [{ "type": "number" }, { "type": "null" }]
            },
            "lines": {
              "description": "Lignes d'articles du ticket, dans l'ordre. Sans les lignes de remise, de consigne ni de sous-total.",
              "type": "array",
              "items": {
                "type": "object",
                "additionalProperties": false,
                "required": ["label", "quantity", "unit", "unit_price", "line_total", "vat_rate"],
                "properties": {
                  "label": {
                    "description": "Libellé recopié tel qu'imprimé.",
                    "type": "string"
                  },
                  "quantity": {
                    "description": "Quantité totale achetée, conditionnement développé (6 x 1L donne 6 avec l'unité l) ; null si illisible.",
                    "anyOf": [{ "type": "number" }, { "type": "null" }]
                  },
                  "unit": {
                    "description": "Unité de la quantité : kg ou g pour une ligne au poids, l, cl ou ml pour un liquide, piece pour une ligne à la pièce ou au colis sans poids ; null si illisible.",
                    "anyOf": [
                      { "type": "string", "enum": ["g", "kg", "ml", "cl", "l", "piece"] },
                      { "type": "null" }
                    ]
                  },
                  "unit_price": {
                    "description": "Prix pour une unité de la quantité ci-dessus ; null si absent.",
                    "anyOf": [{ "type": "number" }, { "type": "null" }]
                  },
                  "line_total": {
                    "description": "Total de la ligne tel qu'imprimé (même base TTC ou hors TVA que amounts_include_vat) ; null si illisible.",
                    "anyOf": [{ "type": "number" }, { "type": "null" }]
                  },
                  "vat_rate": {
                    "description": "Taux de TVA de la ligne en fraction (0.06 pour 6 %, 0.21 pour 21 %) ; null s'il n'est pas indiqué.",
                    "anyOf": [{ "type": "number" }, { "type": "null" }]
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>Schéma JSON de la réponse attendue, au format que l'API accepte pour la sortie structurée.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Schema { get; } = BuildSchema();

    /// <summary>
    /// Lit la réponse du lecteur. Les unités <c>g, kg, ml, cl, l, piece</c> (sans tenir compte de la casse ; « pièce », « pc » et « pcs » valent <c>piece</c>)
    /// deviennent un <see cref="MeasureUnit"/>, les autres valeurs <c>null</c>. La date est lue au format <c>yyyy-MM-dd</c>, les décimaux en culture invariante.
    /// Un taux de TVA hors de [0, 1], une quantité ou un montant négatif ou trop grand pour sa colonne deviennent <c>null</c> ; les libellés sont rognés
    /// et les lignes sans libellé écartées.
    /// </summary>
    /// <exception cref="InvalidOperationException">« Réponse de lecture illisible » : ce n'est pas un objet JSON, ou <c>lines</c> n'est pas un tableau d'objets.</exception>
    public static ReceiptExtraction Parse(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException)
        {
            throw new InvalidOperationException(UnreadableMessage, ex);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(UnreadableMessage);

            return new ReceiptExtraction(
                ReadText(root, "supplier_name"),
                ReadDate(root, "date"),
                ReadText(root, "invoice_number"),
                root.TryGetProperty("amounts_include_vat", out var includesVat) && includesVat.ValueKind == JsonValueKind.True,
                ReadAmount(root, "total"),
                ReadLines(root));
        }
    }

    private static IReadOnlyList<ExtractedLine> ReadLines(JsonElement root)
    {
        if (!root.TryGetProperty("lines", out var lines) || lines.ValueKind == JsonValueKind.Null)
            return [];

        if (lines.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(UnreadableMessage);

        var result = new List<ExtractedLine>();
        foreach (var line in lines.EnumerateArray())
        {
            if (line.ValueKind != JsonValueKind.Object)
                throw new InvalidOperationException(UnreadableMessage);

            if (ReadText(line, "label") is not { } label)
                continue;

            result.Add(new ExtractedLine(
                label,
                ReadDecimal(line, "quantity", QuantityLimit),
                ReadUnit(line, "unit"),
                ReadAmount(line, "unit_price"),
                ReadAmount(line, "line_total"),
                ReadVatRate(line, "vat_rate")));
        }

        return result;
    }

    private static string? ReadText(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static DateOnly? ReadDate(JsonElement parent, string name) =>
        ReadText(parent, name) is { } text
        && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    private static MeasureUnit? ReadUnit(JsonElement parent, string name) =>
        ReadText(parent, name)?.ToLowerInvariant() switch
        {
            "g" => MeasureUnit.G,
            "kg" => MeasureUnit.Kg,
            "ml" => MeasureUnit.Ml,
            "cl" => MeasureUnit.Cl,
            "l" => MeasureUnit.L,
            "piece" or "pièce" or "pc" or "pcs" => MeasureUnit.Piece,
            _ => null
        };

    private static decimal? ReadAmount(JsonElement parent, string name) => ReadDecimal(parent, name, AmountLimit);

    /// <summary>Taux de TVA en fraction : de 0 à 1 (« 21 » au lieu de 0.21 n'est pas un taux, il est ignoré).</summary>
    private static decimal? ReadVatRate(JsonElement parent, string name) =>
        ReadDecimal(parent, name, limit: null) is <= 1m and var rate ? rate : null;

    /// <summary>Nombre JSON (toujours avec un point décimal : <see cref="JsonElement.TryGetDecimal"/> est indépendant de la culture) positif ou nul et inférieur à <paramref name="limit"/>.</summary>
    private static decimal? ReadDecimal(JsonElement parent, string name, decimal? limit)
    {
        if (!parent.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetDecimal(out var number))
            return null;

        return number >= 0m && (limit is not { } max || number < max) ? number : null;
    }

    private static IReadOnlyDictionary<string, JsonElement> BuildSchema()
    {
        using var document = JsonDocument.Parse(SchemaJson);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
