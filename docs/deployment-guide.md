# Deployment Guide — Invoice Processing & Approval Pipeline

A complete, ordered walkthrough against a real Azure subscription. Read
[`docs/architecture.md`](architecture.md) first if you haven't.

## Contents

0. [Prerequisites — install everything first](#0-prerequisites--install-everything-first)
1. [Open the solution in Visual Studio](#1-open-the-solution-in-visual-studio)
2. [Compile-check locally](#2-compile-check-locally)
3. [Cost awareness — read this before deploying anything](#3-cost-awareness--read-this-before-deploying-anything)
4. [Set up GitHub → Azure OIDC](#4-set-up-github--azure-oidc)
5. [Deploy infrastructure](#5-deploy-infrastructure)
6. [Post-infra manual steps](#6-post-infra-manual-steps-the-parts-bicep-cant-express)
7. [Deploy the app](#7-deploy-the-app)
8. [Generate and capture evidence](#8-generate-and-capture-evidence)
9. [Tear down](#9-tear-down)
10. [Troubleshooting](#10-troubleshooting)

---

## 0. Prerequisites — install everything first

```bash
# Azure CLI
curl -sL https://aka.ms/InstallAzureCLIDeb | sudo bash   # macOS: brew install azure-cli
az --version

# Bicep CLI (ships as an az extension)
az bicep install
az bicep version

# .NET 8 SDK — https://dotnet.microsoft.com/download/dotnet/8.0 if not already current
dotnet --version   # must print 8.x

# Azure Functions Core Tools v4
npm install -g azure-functions-core-tools@4 --unsafe-perm true
func --version     # must print 4.x

# Azurite — local Storage emulator
npm install -g azurite

# jq — the CI pipeline uses it to patch connections.json before packaging
jq --version       # macOS: brew install jq · Debian/Ubuntu: sudo apt install jq
```

**Visual Studio 2022** with the **Azure development** workload installed gives you full native
support for the C# Function project (build, F5 debug, Test Explorer, right-click Publish). It
does **not** give you a Logic Apps Standard designer — see the note at the end of
[`docs/architecture.md`](architecture.md) for why, and what to use instead (Azure Portal's own
Standard Logic App designer, no VS Code required for that part either).

Finally:

```bash
az login
az account show --query "{subscriptionId:id, tenantId:tenantId}" -o table
```

Note both values — you'll need them in step 4.

## 1. Open the solution in Visual Studio

Open `InvoiceProcessingPipeline.sln`. Solution Explorer shows:

- **`InvoiceExtractionFunction`** and **`InvoiceExtractionFunction.Tests`** as real, buildable
  .NET projects — right-click either to Build, Debug (F5), run tests (Test Explorer), or Publish
  directly to Azure.
- **`logicapp`**, **`infra`**, **`docs`**, and **`workflows (CI-CD)`** as solution folders
  surfacing the workflow JSON, Bicep/SQL, documentation, and GitHub Actions YAML as plain files
  you can open and edit in the same window — Visual Studio's JSON editor applies schema
  validation to `workflow.json` and `connections.json` the same as it would to any JSON file.

This gives you one place to see and navigate the entire repository, even though the Logic App
workflow files themselves are edited as JSON here rather than through a visual designer (that
part happens in the Azure Portal or VS Code — see architecture.md).

## 2. Compile-check locally

This repo was built in a sandbox where NuGet access was blocked by network policy, so the C# was
written and reviewed carefully but never `dotnet build`-verified. Do that now, before touching
Azure — either via Visual Studio's Build menu / Test Explorer, or:

```bash
dotnet restore
dotnet build
dotnet test InvoiceExtractionFunction.Tests/InvoiceExtractionFunction.Tests.csproj
```

If `dotnet build` fails on `InvoiceExtractionService.cs`, the most likely spot is the exact
`DocumentIntelligenceClient.AnalyzeDocumentAsync` overload — Azure's Document Intelligence SDK has
shifted option-type names across preview/GA versions. The `.csproj` pins `Azure.AI.DocumentIntelligence`
1.0.0 (GA); check whatever version NuGet actually resolves and adjust the call if its signature
differs. That's a narrow, mechanical fix, not a redesign — everything else should build clean.

## 3. Cost awareness — read this before deploying anything

This is real infrastructure on your own subscription. The **Workflow Standard (WS1)** plan
hosting the Logic App and the **Elastic Premium (EP1)** plan hosting the Function App are the
costly pieces — each roughly $150–200/month if left running continuously (check current pricing
for your region at [azure.microsoft.com/pricing](https://azure.microsoft.com/pricing/details/app-service/windows/)).
SQL Basic, Storage, Key Vault, Document Intelligence S0, and Service Bus Standard are all
comparatively cheap.

Given the goal here is portfolio evidence, not a running product: **deploy, generate the run
history and traces you need, capture the screenshots, then tear everything down** (step 9) within
a few days rather than leaving WS1/EP1 running indefinitely.

## 4. Set up GitHub → Azure OIDC

```bash
az ad app create --display-name "gh-invoiceproc-deploy"
APP_ID=$(az ad app list --display-name "gh-invoiceproc-deploy" --query "[0].appId" -o tsv)
az ad sp create --id $APP_ID

az group create --name rg-invoiceproc-dev --location eastus
az role assignment create --assignee $APP_ID --role Contributor \
  --scope /subscriptions/<your-subscription-id>/resourceGroups/rg-invoiceproc-dev
```

**The part that differs from the obvious/default OIDC setup**: both `deploy-infra.yml` and
`deploy-app.yml`'s `deploy` job declare a job-level `environment: dev`. GitHub sets the OIDC
token's `sub` claim to `repo:<org>/<repo>:environment:dev` for any job that references an
environment — **not** the branch-ref form most OIDC tutorials show. The federated credential must
match that exactly:

```bash
az ad app federated-credential create --id $APP_ID --parameters '{
  "name": "github-dev-environment",
  "issuer": "https://token.actions.githubusercontent.com",
  "subject": "repo:<your-org>/<your-repo>:environment:dev",
  "audiences": ["api://AzureADTokenExchange"]
}'
```

If you later add a `test` or `prod` GitHub Environment, create one more federated credential per
environment name — each needs its own; they don't inherit from each other.

Add to the repo's **Settings → Secrets and variables → Actions**: `AZURE_CLIENT_ID` (the `APP_ID`
above), `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID` (both from `az account show`), and
`SQL_ADMIN_PASSWORD` (a strong password you generate).

## 5. Deploy infrastructure

Run the **Deploy Infrastructure** GitHub Actions workflow (`workflow_dispatch` → `dev`), or
locally first to see errors faster:

```bash
az deployment group create \
  --resource-group rg-invoiceproc-dev \
  --template-file infra/main.bicep \
  --parameters infra/main.parameters.dev.json \
  --parameters sqlAdministratorPassword='<a-strong-password>'
```

This takes several minutes — Key Vault, SQL, and the two App Service plans are the slowest
pieces. RBAC role assignments can take a few minutes to actually propagate after the deployment
reports success; if a subsequent step gets a 403 that looks like a permissions problem, wait and
retry before assuming something's wrong.

## 6. Post-infra manual steps (the parts Bicep can't express)

1. **SQL Azure AD admin** (required before contained database users can be created):
   ```bash
   az sql server ad-admin create \
     --resource-group rg-invoiceproc-dev --server-name sql-invoiceproc-dev \
     --display-name <your-name> --object-id $(az ad signed-in-user show --query id -o tsv)
   ```
2. **Create the schema and grant the Logic App's managed identity access.** Connect via `sqlcmd`
   or Azure Data Studio using **Azure AD authentication** and run, in order:
   `infra/sql/schema.sql`, then `infra/sql/grant-managed-identity-access.sql`.
3. **Create the managed API connections** (Teams and SQL). Open the workflow folder in VS Code
   with the Logic Apps (Standard) extension — this is the one step that genuinely needs VS Code
   rather than Visual Studio, since it's an interactive wizard against the live Azure resource,
   not just file editing. Let it walk you through creating each connection resource; this
   generates the actual connection IDs that the placeholder values in `connections.json` stand in
   for.
4. **Seed the remaining Key Vault secrets**: `invoice-sql-connstr` (the SQL connection string) and
   `invoice-func-key` (from the Function App's **App keys** blade, once step 7 deploys it):
   ```bash
   az keyvault secret set --vault-name kv-invoiceproc-dev --name invoice-sql-connstr --value '<connection-string>'
   ```

## 7. Deploy the app

Run the **Build and Deploy App** GitHub Actions workflow. It builds and unit-tests the Function
project (failing tests block the deploy), packages the Logic App workflows (flipping
`connections.json`'s auth to managed identity as part of packaging), deploys both, and
smoke-tests that the Logic App comes back in a `Running` state.

If this is the very first run and you haven't done step 6.4 yet for the function key, the Logic
App will deploy fine but its calls to the Function will fail with 401 — do step 6.4 first, or
re-run this workflow after.

## 8. Generate and capture evidence

Drop a sample invoice PDF into the `incoming-invoices` blob container. Watch the run appear under
Azure Portal → your Logic App → Workflows → `ProcessInvoiceWorkflow` → **Run History**. Screenshot
a successful run, then upload a corrupt/non-invoice file and screenshot the failed run showing
`HandleInvoiceFailure` firing.

In Application Insights → **Transaction search**, filter by the `x-correlation-id` visible in run
history and pull up the end-to-end trace across the Logic App and Function call. This single
screenshot is your strongest evidence — it shows the distributed tracing actually working, not
just a workflow diagram.

Screenshot a green GitHub Actions run of both `deploy-infra` and `deploy-app`.

Put these under an `evidence/` folder in the repo (redact anything account-specific) and link them
from the README.

## 9. Tear down

Once you have your screenshots:

```bash
az group delete --name rg-invoiceproc-dev --yes --no-wait
```

The code, IaC, and CI/CD pipeline still exist and still prove the same thing — the evidence you
captured in step 8 is what shows it ran; re-deploy any time (steps 5–8 again) to refresh it or
demo it live in an interview.

## 10. Troubleshooting

| Symptom | Likely cause | Fix |
|---|---|---|
| `AADSTS700213: No matching federated identity record found` | Federated credential subject doesn't match the job's actual OIDC claim | Confirm the job declares `environment:` and your federated credential subject is `repo:<org>/<repo>:environment:<name>`, not the ref-based form |
| Function calls from the Logic App return 401 | Function key secret not yet seeded, or Key Vault RBAC hasn't propagated | Do step 6.4; if already done, wait a few minutes for RBAC propagation and retry |
| SQL connector action fails with a login/permission error | Contained database user not created, or SQL AD admin not set | Redo step 6.1 then 6.2, in that order |
| `az logicapp deployment source config-zip` appears to wipe existing workflow state | `--clean false` flag missing or not applied | Confirm `deploy-app.yml`'s deploy step still has `--clean false` |
| Bicep deployment fails on a role assignment with "already exists" | Re-running a deployment that already created that role assignment | Harmless — role assignment names are deterministic (`guid(...)`) specifically so re-runs are idempotent |
