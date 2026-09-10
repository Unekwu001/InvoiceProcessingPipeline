# Architecture — Invoice Processing & Approval Pipeline

## Business scenario

A finance team receives vendor invoices as PDFs. Today someone manually opens each one, keys the
data into an ERP, and routes anything over a threshold for manager approval by email. This system
automates that end to end — the single most common real-world reason enterprises adopt Logic
Apps (AP automation / document-driven workflow).

## Why Standard, not Consumption

This system uses the **Standard (single-tenant)** Logic Apps tier rather than Consumption because
Standard:

- Separates app code from infrastructure, so workflow changes deploy independently of infra changes.
- Supports local development and source control via VS Code + the Azure Functions Core Tools
  runtime (Consumption's browser-only JSON designer doesn't).
- Runs on the Azure Functions extensibility model, so the custom C# extraction function sits
  right next to the workflow instead of being bolted on as an external HTTP call.
- Supports VNET integration and private endpoints for production network isolation.
- Is what current Microsoft DevOps guidance treats as the CI/CD-friendly, production-grade tier.

## Flow

1. **Trigger** — Blob created in the `incoming-invoices` container (Azure Blob Storage connector,
   polling trigger). Represents an invoice landing from email-to-blob, a scanner, or an upload portal.
2. **Extract** — Calls a custom C# Azure Function (`InvoiceExtractionFunction`) that sends the PDF
   to Azure AI Document Intelligence's prebuilt invoice model and returns structured JSON
   (vendor, invoice number, total, due date, line items).
3. **Validate** — A `Condition` action checks required fields are present and the total is a
   sane positive number. Anything that fails validation branches into the failure path (step 6)
   instead of continuing.
4. **Route for approval** — A `Condition` on invoice total: below the threshold auto-approves;
   at/above threshold posts an Adaptive Card approval request (Teams connector) to the finance
   approver and waits on the response.
5. **Persist** — On approval, writes the invoice record to Azure SQL (parameterized SQL connector
   using managed identity, not a SQL login) and moves the blob to a `processed` container.
6. **Failure/compensation path** — Any action failure (extraction error, validation failure,
   rejection, or persistence failure) is routed to a **child workflow**, `HandleInvoiceFailure`,
   which writes an error record with the run's correlation ID, moves the blob to a `failed`
   container, sends a Teams alert, and drops a message on a Service Bus dead-letter queue for
   manual reprocessing — so nothing silently disappears.

   This is deliberately a child workflow rather than a single shared error-handling block: Logic
   Apps' workflow definition language has no "goto" — you cannot jump from inside a nested `If`
   branch to an arbitrary sibling action elsewhere in the workflow. The three places that need
   the same cleanup logic (a technical failure calling the extraction function, a business-rule
   validation failure, and an approver rejection) each invoke `HandleInvoiceFailure` directly via
   the built-in "Invoke a workflow in this logic app" action instead.

## Best practices demonstrated (and exactly where)

| Practice | Where it lives |
|---|---|
| Managed identity over keys | System-assigned managed identity on the Logic App and Function App; role assignments scoped to least privilege (`Storage Blob/Queue/Table Data Contributor`, `Key Vault Secrets User`, `Cognitive Services User`) in `infra/main.bicep` |
| No secrets in source control | `local.settings.json` is git-ignored; every secret is an `@Microsoft.KeyVault(...)` app setting reference |
| Parameterized connections | `logicapp/connections.json` uses `@appsetting('...')` throughout — the identical file deploys to dev/test/prod unchanged |
| Immutable deployment artifact | `.github/workflows/deploy-app.yml` packages one zip and deploys it as-is; only app settings differ per environment |
| Infra/app pipeline separation | `deploy-infra.yml` (Bicep, changes rarely) is separate from `deploy-app.yml` (workflow + function code, changes often) |
| Explicit error handling | Every failure path routes to `HandleInvoiceFailure` — see Flow step 6 above |
| Observability | Application Insights wired via `APPLICATIONINSIGHTS_CONNECTION_STRING`; `workflow().run.name` used as a correlation ID threaded through every downstream call and log line |
| Retry policies | Exponential backoff (`type: exponential`) on the extraction Function call and every SQL/Service Bus write, not left to connector defaults |
| Naming convention | `rg-invoiceproc-<env>`, `logicapp-invoiceproc-<env>`, `func-invoiceproc-<env>`, `kv-invoiceproc-<env>` — environment and system always identifiable from the resource name |
| Testing | `InvoiceFieldMapperTests.cs` unit-tests the confidence-threshold/validation business rules independent of the Azure SDK — see the honest boundary noted in the deployment guide for what still needs Azurite/integration testing |

## A note on Visual Studio vs VS Code

This solution (`InvoiceProcessingPipeline.sln`) is organized so the **C# side is a fully native
Visual Studio experience**: open the `.sln`, and the `InvoiceExtractionFunction` and its test
project build, debug (F5), and unit-test (Test Explorer) exactly like any other .NET project,
with Solution Explorer also surfacing the Logic App workflow files, Bicep, SQL scripts, docs, and
CI/CD YAML as organized solution folders so the whole repository is visible in one place.

What Visual Studio **cannot** do — and this is a hard constraint, not an oversight in how this
repo is organized — is provide a Standard Logic App designer, local workflow execution, or a
connections wizard. Microsoft's Logic Apps engineering team has confirmed there is no plan to
bring that tooling to Visual Studio 2022; it exists only as a VS Code extension
(`ms-azuretools.vscode-azurelogicapps`). Given that, the workflow JSON files here are meant to be
edited either directly as JSON in Visual Studio (they're plain, schema-following JSON — no
proprietary format) or visually through the **Azure Portal's own Standard Logic App designer**
(Portal → your Logic App → Workflows → the workflow → Designer), which needs no VS Code either.
VS Code remains the only option specifically for local-run/debug of the workflow itself and the
interactive connection-creation wizard referenced in the deployment guide's post-infra steps.
