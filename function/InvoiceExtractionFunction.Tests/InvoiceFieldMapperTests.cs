using InvoiceProcessing.ExtractionFunction.Models;
using InvoiceProcessing.ExtractionFunction.Services;
using Xunit;

namespace InvoiceExtractionFunction.Tests;

public class InvoiceFieldMapperTests
{
    [Fact]
    public void Map_AllFieldsHighConfidence_ReturnsCleanResultNotFlaggedForReview()
    {
        var fields = new AnalyzedInvoiceFields(
            VendorNameRaw: "Acme Supplies Ltd",
            VendorNameConfidence: 0.95,
            InvoiceNumberRaw: "INV-2027-0042",
            InvoiceNumberConfidence: 0.98,
            TotalAmountRaw: 1250.00m,
            TotalAmountConfidence: 0.99,
            DueDateRaw: new DateTimeOffset(2027, 2, 15, 0, 0, 0, TimeSpan.Zero),
            DueDateConfidence: 0.9);

        var result = InvoiceFieldMapper.Map(fields);

        Assert.Equal("Acme Supplies Ltd", result.VendorName);
        Assert.Equal("INV-2027-0042", result.InvoiceNumber);
        Assert.Equal(1250.00m, result.TotalAmount);
        Assert.False(result.RequiresManualReview);
        Assert.Empty(result.FieldsBelowConfidenceThreshold);
    }

    [Fact]
    public void Map_LowConfidenceVendorName_FlagsFieldAndRequiresManualReview()
    {
        var fields = new AnalyzedInvoiceFields(
            VendorNameRaw: "Ac?me Sup?lies",
            VendorNameConfidence: 0.4,
            InvoiceNumberRaw: "INV-2027-0042",
            InvoiceNumberConfidence: 0.98,
            TotalAmountRaw: 1250.00m,
            TotalAmountConfidence: 0.99,
            DueDateRaw: null,
            DueDateConfidence: 0);

        var result = InvoiceFieldMapper.Map(fields);

        Assert.True(result.RequiresManualReview);
        Assert.Contains(nameof(AnalyzedInvoiceFields.VendorNameRaw), result.FieldsBelowConfidenceThreshold);
    }

    [Fact]
    public void Map_MissingTotalAmount_DefaultsToZeroAndRequiresManualReview()
    {
        var fields = new AnalyzedInvoiceFields(
            VendorNameRaw: "Acme Supplies Ltd",
            VendorNameConfidence: 0.95,
            InvoiceNumberRaw: "INV-2027-0042",
            InvoiceNumberConfidence: 0.98,
            TotalAmountRaw: null,
            TotalAmountConfidence: 0,
            DueDateRaw: null,
            DueDateConfidence: 0);

        var result = InvoiceFieldMapper.Map(fields);

        Assert.Equal(0m, result.TotalAmount);
        Assert.True(result.RequiresManualReview);
        // This is exactly the case the Logic App's "Validate_extraction" condition (totalAmount > 0)
        // is designed to catch and route to the HandleInvoiceFailure child workflow.
    }

    [Fact]
    public void Map_NegativeTotalAmount_TreatedAsZero()
    {
        var fields = new AnalyzedInvoiceFields(
            VendorNameRaw: "Acme Supplies Ltd",
            VendorNameConfidence: 0.95,
            InvoiceNumberRaw: "INV-2027-0042",
            InvoiceNumberConfidence: 0.98,
            TotalAmountRaw: -50m,
            TotalAmountConfidence: 0.9,
            DueDateRaw: null,
            DueDateConfidence: 0);

        var result = InvoiceFieldMapper.Map(fields);

        Assert.Equal(0m, result.TotalAmount);
        Assert.True(result.RequiresManualReview);
    }

    [Fact]
    public void Map_WhitespaceOnlyVendorName_TreatedAsNull()
    {
        var fields = new AnalyzedInvoiceFields(
            VendorNameRaw: "   ",
            VendorNameConfidence: 0.95,
            InvoiceNumberRaw: "INV-2027-0042",
            InvoiceNumberConfidence: 0.98,
            TotalAmountRaw: 100m,
            TotalAmountConfidence: 0.95,
            DueDateRaw: null,
            DueDateConfidence: 0);

        var result = InvoiceFieldMapper.Map(fields);

        Assert.Null(result.VendorName);
    }
}
