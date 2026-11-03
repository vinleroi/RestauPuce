using ChefOS.Core.Receipts;
using ChefOS.Core.Units;

namespace ChefOS.Infrastructure.Receipts;

/// <summary>
/// Lecteur de démonstration : ignore le fichier et renvoie toujours le même ticket Metro, daté du jour, hors TVA, après une pause qui simule la lecture.
/// Les libellés sont ceux de « Le Petit Bistro » (voir <c>DemoSeeder</c>), avec une hausse d'environ 10 % sur le beurre,
/// des prix proches du seed sur les autres lignes et une ligne (« SACS POUBELLE 100L ») que le chef ignorera.
/// </summary>
public sealed class DemoReceiptReader : IReceiptReader
{
    private static readonly TimeSpan ReadDelay = TimeSpan.FromMilliseconds(800);

    public async Task<ReceiptExtraction> ReadAsync(byte[] content, string contentType, CancellationToken ct = default)
    {
        await Task.Delay(ReadDelay, ct);

        // Quantité totale et prix hors TVA par unité de la quantité, comme le demande la consigne du lecteur IA.
        ExtractedLine[] lines =
        [
            Line("BEURRE DOUX 82% 1KG", 5m, MeasureUnit.Kg, 10.12m, 50.60m, 0.06m),               // seed : 9,20 €/kg, soit +10 %
            Line("OIGNONS JAUNES FILET 5KG", 10m, MeasureUnit.Kg, 1.12m, 11.20m, 0.06m),          // seed : 1,10 €/kg
            Line("BIERE BRUNE 33CL X24", 7.92m, MeasureUnit.L, 3.64m, 28.80m, 0.21m),             // 24 x 33 cl ; seed : 3,60 €/l
            Line("CREME FRAICHE 35% 1L X6", 6m, MeasureUnit.L, 5.85m, 35.10m, 0.06m),             // seed : 5,80 €/l
            Line("OEUFS FRAIS CAT.A X30", 30m, MeasureUnit.Piece, 0.29m, 8.70m, 0.06m),           // seed : 0,30 € la pièce
            Line("MOULES DE ZELANDE FILET 2KG", 6m, MeasureUnit.Kg, 3.85m, 23.10m, 0.06m),        // seed : 3,90 €/kg
            Line("SACS POUBELLE 100L", 1m, MeasureUnit.Piece, 12.40m, 12.40m, 0.21m)              // aucun ingrédient correspondant
        ];

        return new ReceiptExtraction(
            "Metro Cash & Carry",
            DateOnly.FromDateTime(DateTime.Now),
            "DEMO-0427",
            AmountsIncludeVat: false,
            lines.Sum(l => l.LineTotal),
            lines);
    }

    private static ExtractedLine Line(string label, decimal quantity, MeasureUnit unit, decimal unitPrice, decimal lineTotal, decimal vatRate) =>
        new(label, quantity, unit, unitPrice, lineTotal, vatRate);
}
