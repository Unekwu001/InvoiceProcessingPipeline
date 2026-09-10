using InvoiceProcessing.ExtractionFunction.Models;

namespace InvoiceProcessing.ExtractionFunction.Services;

/// <summary>
/// Pure business-rule mapping from raw Document Intelligence fields to the structured result the
/// Logic App validates against. Deliberately has no dependency on the Azure SDK or network I/O so
/// it is fully unit-testable — see InvoiceExtractionFunction.Tests/InvoiceFieldMapperTests.cs.
/// </summary>
public static class InvoiceFieldMapper
{
    /// <summary>
    /// Fields below this confidence score are still returned (so a human can review them) but are
    /// flagged in <see cref="ExtractedInvoiceData.FieldsBelowConfidenceThreshold"/>, which the
    /// Logic App's validation step treats as a reason to route to manual review rather than
    /// auto-processing a low-confidence extraction.
    /// </summary>
    public const double ConfidenceThreshold = 0.7;

    public static ExtractedInvoiceData Map(AnalyzedInvoiceFields fields)
    {
        ArgumentNullException.ThrowIfNull(fields);

        var lowConfidenceFields = new List<string>();

        if (fields.VendorNameConfidence < ConfidenceThreshold)
        {
            lowConfidenceFields.Add(nameof(fields.VendorNameRaw));
        }

        if (fields.InvoiceNumberConfidence < ConfidenceThreshold)
        {
            lowConfidenceFields.Add(nameof(fields.InvoiceNumberRaw));
        }

        if (fields.TotalAmountConfidence < ConfidenceThreshold)
        {
            lowConfidenceFields.Add(nameof(fields.TotalAmountRaw));
        }

        if (fields.DueDateConfidence < ConfidenceThreshold)
        {
            lowConfidenceFields.Add(nameof(fields.DueDateRaw));
        }

        var totalAmount = fields.TotalAmountRaw is > 0 ? fields.TotalAmountRaw.Value : 0m;

        return new ExtractedInvoiceData(
            VendorName: string.IsNullOrWhiteSpace(fields.VendorNameRaw) ? null : fields.VendorNameRaw.Trim(),
            InvoiceNumber: string.IsNullOrWhiteSpace(fields.InvoiceNumberRaw) ? null : fields.InvoiceNumberRaw.Trim(),
            TotalAmount: totalAmount,
            DueDate: fields.DueDateRaw,
            RequiresManualReview: lowConfidenceFields.Count > 0 || totalAmount <= 0,
            FieldsBelowConfidenceThreshold: lowConfidenceFields);
    }
}
