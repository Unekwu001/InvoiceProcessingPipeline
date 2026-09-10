using System.Net;
using System.Text.Json;
using InvoiceProcessing.ExtractionFunction.Models;
using InvoiceProcessing.ExtractionFunction.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace InvoiceProcessing.ExtractionFunction.Functions;

/// <summary>
/// Called by the ProcessInvoiceWorkflow Logic App's "Extract_invoice_data" HTTP action. Kept
/// deliberately thin — all extraction logic lives in InvoiceExtractionService so it can be unit
/// tested and reused (e.g. from a future batch-reprocessing tool) independent of the HTTP layer.
/// </summary>
public sealed class ExtractInvoiceDataFunction(
    IInvoiceExtractionService extractionService,
    ILogger<ExtractInvoiceDataFunction> logger)
{
    [Function("ExtractInvoiceData")]
    public async Task<HttpResponseData> RunAsync(
        [HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequestData req,
        CancellationToken cancellationToken)
    {
        var correlationId = req.Headers.TryGetValues("x-correlation-id", out var values)
            ? values.FirstOrDefault()
            : null;

        using var _ = logger.BeginScope(new Dictionary<string, object?> { ["CorrelationId"] = correlationId });

        ExtractInvoiceDataRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<ExtractInvoiceDataRequest>(
                req.Body, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Malformed request body.");
            return await WriteErrorAsync(req, HttpStatusCode.BadRequest, "Request body was not valid JSON.");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.BlobName) || string.IsNullOrWhiteSpace(request.ContainerName))
        {
            return await WriteErrorAsync(req, HttpStatusCode.BadRequest, "containerName and blobName are required.");
        }

        try
        {
            var extracted = await extractionService.ExtractAsync(request, cancellationToken);
            var response = req.CreateResponse(HttpStatusCode.OK);
            await response.WriteAsJsonAsync(extracted, cancellationToken);
            return response;
        }
        catch (InvalidOperationException ex)
        {
            // Blob missing, or Document Intelligence returned nothing usable — a caller error,
            // not a transient fault, so we return 4xx rather than letting the Logic App's retry
            // policy burn attempts on something a retry can't fix.
            logger.LogWarning(ex, "Extraction could not proceed for {BlobName}.", request.BlobName);
            return await WriteErrorAsync(req, HttpStatusCode.UnprocessableEntity, ex.Message);
        }
        catch (Exception ex)
        {
            // Unexpected/transient failure (Document Intelligence throttling, network blip, etc.)
            // — return 5xx so the Logic App's exponential retry policy on this action kicks in.
            logger.LogError(ex, "Unexpected error extracting invoice data for {BlobName}.", request.BlobName);
            return await WriteErrorAsync(req, HttpStatusCode.InternalServerError, "Extraction failed unexpectedly.");
        }
    }

    private static async Task<HttpResponseData> WriteErrorAsync(HttpRequestData req, HttpStatusCode statusCode, string message)
    {
        var response = req.CreateResponse(statusCode);
        await response.WriteAsJsonAsync(new { error = message }, statusCode);
        return response;
    }
}
