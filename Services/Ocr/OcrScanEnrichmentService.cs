using ApprovalPO.Services.Stock;

namespace ApprovalPO.Services.Ocr;

/// <summary>Aligns OCR line items to stock master data by item code (SQL API / ST_ITEM).</summary>
public sealed class OcrScanEnrichmentService
{
    private readonly IStockItemLookup _stock;

    public OcrScanEnrichmentService(IStockItemLookup stock)
    {
        _stock = stock;
    }

    public async Task<OcrFields?> EnrichAsync(OcrFields? fields, CancellationToken cancellationToken = default)
    {
        if (fields?.Items is null || fields.Items.Count == 0)
            return fields;

        foreach (var item in fields.Items)
        {
            var code = (item.Code ?? "").Trim();
            if (code.Length == 0) continue;

            var lookup = await _stock.LookupByCodeAsync(code, cancellationToken).ConfigureAwait(false);
            if (lookup is null) continue;

            item.ScannedDescription ??= (item.Description ?? "").Trim();
            if (string.IsNullOrEmpty(item.ScannedDescription))
                item.ScannedDescription = item.Description;

            item.Description = lookup.Description;
            item.DescriptionSource = lookup.Source;
            item.DescriptionCorrected = !DescriptionsMatch(item.ScannedDescription, lookup.Description);
        }

        return fields;
    }

    private static bool DescriptionsMatch(string? a, string? b)
    {
        var na = NormDesc(a);
        var nb = NormDesc(b);
        if (na.Length == 0 && nb.Length == 0) return true;
        if (na.Length == 0 || nb.Length == 0) return false;
        if (na == nb) return true;
        return na.Length >= 4 && nb.Length >= 4 && (na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal));
    }

    private static string NormDesc(string? s) =>
        string.Join(' ', (s ?? "").Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
