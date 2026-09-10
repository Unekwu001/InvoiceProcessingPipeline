-- Run once against sqldb-invoiceproc-<env>, connected as the SQL admin (or an Azure AD admin),
-- AFTER the Logic App resource exists. This is the step Bicep/ARM cannot express (contained
-- database users mapped to a managed identity are a data-plane, not control-plane, operation) —
-- see the comment in infra/main.bicep referencing this file.
--
-- Replace 'logicapp-invoiceproc-dev' with the actual Logic App resource name for your environment.

CREATE USER [logicapp-invoiceproc-dev] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datawriter ADD MEMBER [logicapp-invoiceproc-dev];
ALTER ROLE db_datareader ADD MEMBER [logicapp-invoiceproc-dev];
GO
