using System.Text.Json;
using ChefOS.Core.Vitrine;

namespace ChefOS.Infrastructure.Wines;

/// <summary>
/// Contrat JSON entre ChefOS et l'assistant de rédaction des fiches de vin : <see cref="Schema"/> est le schéma envoyé à l'API (sortie structurée,
/// strict : objet fermé, tous les champs présents, <c>null</c> permis seulement pour l'appellation), <see cref="Parse"/> lit la réponse
/// et la ramène à ce que la base accepte (textes rognés, longueurs des colonnes respectées).
/// </summary>
public static class WineDescriptionJson
{
    internal const string UnreadableMessage = "Réponse de l'assistant illisible";

    private const string SchemaJson = """
        {
          "type": "object",
          "additionalProperties": false,
          "required": ["appellation", "region", "country", "color", "grapes", "robe", "nez", "bouche", "garde", "service_temperature", "general_pairings", "suggested_pairing_dish_names"],
          "properties": {
            "appellation": {
              "description": "Appellation du vin (par exemple Saint-Émilion grand cru) ; null s'il n'y en a pas ou si elle est inconnue.",
              "anyOf": [{ "type": "string" }, { "type": "null" }]
            },
            "region": { "description": "Région viticole (par exemple Bordeaux).", "type": "string" },
            "country": { "description": "Pays (par exemple France).", "type": "string" },
            "color": {
              "description": "Couleur du vin.",
              "type": "string",
              "enum": ["Rouge", "Blanc", "Rose", "Effervescent", "Doux"]
            },
            "grapes": { "description": "Cépages, séparés par des virgules.", "type": "string" },
            "robe": { "description": "Description de la robe, 2 à 3 phrases.", "type": "string" },
            "nez": { "description": "Description du nez, 2 à 3 phrases.", "type": "string" },
            "bouche": { "description": "Description de la bouche, 2 à 3 phrases.", "type": "string" },
            "garde": { "description": "Potentiel de garde et moment idéal pour le boire.", "type": "string" },
            "service_temperature": { "description": "Température de service (par exemple 16-18 °C).", "type": "string" },
            "general_pairings": { "description": "Accords mets et vin en général, 2 à 3 phrases.", "type": "string" },
            "suggested_pairing_dish_names": {
              "description": "Plats de la liste fournie qui s'accordent avec ce vin, noms recopiés tels quels ; tableau vide si aucun ne convient.",
              "type": "array",
              "items": { "type": "string" }
            }
          }
        }
        """;

    /// <summary>Schéma JSON de la réponse attendue, au format que l'API accepte pour la sortie structurée.</summary>
    public static IReadOnlyDictionary<string, JsonElement> Schema { get; } = BuildSchema();

    /// <summary>
    /// Lit la réponse de l'assistant. « Rosé » vaut <see cref="WineColor.Rose"/> ; les textes sont rognés et coupés à la taille de leur colonne.
    /// </summary>
    /// <exception cref="InvalidOperationException">« Réponse de l'assistant illisible » : ce n'est pas un objet JSON, ou la couleur est inconnue.</exception>
    public static WineDraft Parse(string json)
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

            var color = ReadColor(Text(root, "color"))
                ?? throw new InvalidOperationException(UnreadableMessage);

            var dishes = new List<string>();
            if (root.TryGetProperty("suggested_pairing_dish_names", out var names) && names.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in names.EnumerateArray())
                {
                    if (name.ValueKind == JsonValueKind.String && name.GetString()?.Trim() is { Length: > 0 } dish)
                        dishes.Add(dish);
                }
            }

            return new WineDraft(
                NullIfEmpty(Clip(Text(root, "appellation"), 200)),
                Clip(Text(root, "region"), 100),
                Clip(Text(root, "country"), 100),
                color,
                Clip(Text(root, "grapes"), 300),
                Text(root, "robe"),
                Text(root, "nez"),
                Text(root, "bouche"),
                Text(root, "garde"),
                Clip(Text(root, "service_temperature"), 100),
                Text(root, "general_pairings"),
                dishes);
        }
    }

    private static WineColor? ReadColor(string value) => value.ToLowerInvariant() switch
    {
        "rouge" => WineColor.Rouge,
        "blanc" => WineColor.Blanc,
        "rose" or "rosé" => WineColor.Rose,
        "effervescent" => WineColor.Effervescent,
        "doux" => WineColor.Doux,
        _ => null
    };

    private static string Text(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() ?? "" : "";

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max].TrimEnd();

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    private static IReadOnlyDictionary<string, JsonElement> BuildSchema()
    {
        using var document = JsonDocument.Parse(SchemaJson);
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
}
