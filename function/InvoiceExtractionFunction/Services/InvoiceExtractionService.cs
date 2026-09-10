using Azure;
using Azure.AI.DocumentIntelligence;
using Azure.Storage.Blobs;
using InvoiceProcessing.ExtractionFunction.Models;
using Microsoft.Extensions.Logging;

namespace InvoiceProcessing.ExtractionFunction.Services;

public interface IInvoiceExtractionService
{
    Task<ExtractedInvoiceData> ExtractAsync(ExtractInvoiceDataRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Downloads the invoice PDF from Blob Storage and runs it through Document Intelligence's
/// prebuilt invoice model. Both clients authenticate via the managed identity configured in
/// Program.cs — this class never sees a connection string or API key.
/// </summary>
public sealed class InvoiceExtractionService(
    BlobServiceClient blobServiceClient,
    DocumentIntelligenceClient documentIntelligenceClient,
    ILogger<InvoiceExtractionService> logger) : IInvoiceExtractionService
{
    private const string PrebuiltInvoiceModelId = "prebuilt-invoice";

    public async Task<ExtractedInvoiceData> ExtractAsync(ExtractInvoiceDataRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        logger.LogInformation(
            "Downloading blob {BlobName} from container {ContainerName} for extraction.",
            request.BlobName, request.ContainerName);

        var containerClient = blobServiceClient.GetBlobContainerClient(request.ContainerName);
        var blobClient = containerClient.GetBlobClient(request.BlobName);

        BinaryData invoiceContent;
        try
        {
            var downloadResult = await blobClient.DownloadContentAsync(cancellationToken);
            invoiceContent = downloadResult.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException(
                $"Blob '{request.BlobName}' was not found in container '{request.ContainerName}'. " +
                "It may have already been moved by a previous (retried) run.", ex);
        }

        Operation<AnalyzeResult> operation = await documentIntelligenceClient.AnalyzeDocumentAsync(
            WaitUntil.Completed,
            PrebuiltInvoiceModelId,
            invoiceContent,
            cancellationToken: cancellationToken);

        AnalyzeResult analyzeResult = operation.Value;

        if (analyzeResult.Documents.Count == 0)
        {
            logger.LogWarning(
                "Document Intelligence returned no documents for blob {BlobName}; treating all fields as missing.",
                request.BlobName);
            return InvoiceFieldMapper.Map(new AnalyzedInvoiceFields(null, 0, null, 0, null, 0, null, 0));
        }

        var fields = analyzeResult.Documents[0].Fields;
        var analyzedFields = new AnalyzedInvoiceFields(
            VendorNameRaw: TryGetString(fields, "VendorName", out var vendorConfidence),
            VendorNameConfidence: vendorConfidence,
            InvoiceNumberRaw: TryGetString(fields, "InvoiceId", out var invoiceIdConfidence),
            InvoiceNumberConfidence: invoiceIdConfidence,
            TotalAmountRaw: TryGetCurrency(fields, "InvoiceTotal", out var totalConfidence),
            TotalAmountConfidence: totalConfidence,
            DueDateRaw: TryGetDate(fields, "DueDate", out var dueDateConfidence),
            DueDateConfidence: dueDateConfidence);

        var extracted = InvoiceFieldMapper.Map(analyzedFields);

        logger.LogInformation(
            "Extraction complete for {BlobName}. RequiresManualReview={RequiresManualReview}, LowConfidenceFields={LowConfidenceFields}",
            request.BlobName, extracted.RequiresManualReview, string.Join(",", extracted.FieldsBelowConfidenceThreshold));

        return extracted;
    }

    private static string? TryGetString(IReadOnlyDictionary<string, DocumentField> fields, string fieldName, out double confidence)
    {
        confidence = 0;
        if (!fields.TryGetValue(fieldName, out var field))
        {
            return null;
        }

        confidence = field.Confidence ?? 0;
        return field.FieldType == DocumentFieldType.String ? field.ValueString : field.Content;
    }

    private static decimal? TryGetCurrency(IReadOnlyDictionary<string, DocumentField> fields, string fieldName, out double confidence)
    {
        confidence = 0;
        if (!fields.TryGetValue(fieldName, out var field))
        {
            return null;
        }

        confidence = field.Confidence ?? 0;
        return field.FieldType == DocumentFieldType.Currency && field.ValueCurrency is not null
            ? (decimal)field.ValueCurrency.Amount
            : null;
    }

    private static DateTimeOffset? TryGetDate(IReadOnlyDictionary<string, DocumentField> fields, string fieldName, out double confidence)
    {
        confidence = 0;
        if (!fields.TryGetValue(fieldName, out var field))
        {
            return null;
        }

        confidence = field.Confidence ?? 0;
        return field.FieldType == DocumentFieldType.Date ? field.ValueDate : null;
    }
}
