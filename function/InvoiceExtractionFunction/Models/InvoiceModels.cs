namespace InvoiceProcessing.ExtractionFunction.Models;

/// <summary>Request body sent by the Logic App's "Extract_invoice_data" HTTP action.</summary>
public sealed record ExtractInvoiceDataRequest(string ContainerName, string BlobName);

/// <summary>
/// The raw fields pulled out of a Document Intelligence AnalyzeResult, before business-rule
/// mapping is applied. Kept as a plain DTO (rather than passing the SDK's AnalyzeResult around)
/// specifically so the mapping logic below can be unit tested without needing to construct or
/// mock the Azure SDK's response types.
/// </summary>
public sealed record AnalyzedInvoiceFields(
    string? VendorNameRaw,
    double VendorNameConfidence,
    string? InvoiceNumberRaw,
    double InvoiceNumberConfidence,
    decimal? TotalAmountRaw,
    double TotalAmountConfidence,
    DateTimeOffset? DueDateRaw,
    double DueDateConfidence);

/// <summary>The structured result returned to the Logic App workflow.</summary>
public sealed record ExtractedInvoiceData(
    string? VendorName,
    string? InvoiceNumber,
    decimal TotalAmount,
    DateTimeOffset? DueDate,
    bool RequiresManualReview,
    IReadOnlyList<string> FieldsBelowConfidenceThreshold);
