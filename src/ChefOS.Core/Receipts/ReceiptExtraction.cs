using ChefOS.Core.Units;

namespace ChefOS.Core.Receipts;

/// <summary>Contenu d'un ticket tel que lu par un <see cref="IReceiptReader"/> ; <c>VatRate</c> est une fraction (0.06m pour 6 %).</summary>
public sealed record ReceiptExtraction(
    string? SupplierName,
    DateOnly? Date,
    string? InvoiceNumber,
    bool AmountsIncludeVat,
    decimal? Total,
    IReadOnlyList<ExtractedLine> Lines);

public sealed record ExtractedLine(
    string Label,
    decimal? Quantity,
    MeasureUnit? Unit,
    decimal? UnitPrice,
    decimal? LineTotal,
    decimal? VatRate);
