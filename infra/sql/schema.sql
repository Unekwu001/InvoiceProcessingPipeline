-- Run once against sqldb-invoiceproc-<env> after infra deploys.
CREATE TABLE dbo.Invoices
(
    Id              INT IDENTITY(1,1) PRIMARY KEY,
    InvoiceNumber   NVARCHAR(100)   NOT NULL,
    VendorName      NVARCHAR(200)   NOT NULL,
    TotalAmount     DECIMAL(18,2)   NOT NULL,
    DueDate         DATETIME2       NULL,
    Status          NVARCHAR(50)    NOT NULL, -- Approved | AutoApproved
    CorrelationId   NVARCHAR(100)   NOT NULL,
    CreatedAtUtc    DATETIME2       NOT NULL DEFAULT SYSUTCDATETIME()
);

CREATE INDEX IX_Invoices_CorrelationId ON dbo.Invoices (CorrelationId);
CREATE INDEX IX_Invoices_VendorName ON dbo.Invoices (VendorName);
