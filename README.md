# Invoice Processing & Approval Pipeline

![Azure](https://img.shields.io/badge/Azure-Logic%20Apps%20Standard-0078D4?logo=microsoftazure&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-8-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-12-239120?logo=csharp&logoColor=white)
![Bicep](https://img.shields.io/badge/IaC-Bicep-orange)
![GitHub Actions](https://img.shields.io/badge/CI%2FCD-GitHub%20Actions-2088FF?logo=githubactions&logoColor=white)
![Visual Studio](https://img.shields.io/badge/IDE-Visual%20Studio%202022-5C2D91?logo=visualstudio&logoColor=white)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)

A production-pattern **Azure Logic Apps (Standard)** system that automates vendor invoice
intake: extraction via Azure AI Document Intelligence, business-rule validation, conditional
Teams-based approval, and SQL persistence — with an explicit, reusable failure-handling path
rather than anything that fails silently.

**[Architecture & design rationale](docs/architecture.md)** · **[Full deployment guide](docs/deployment-guide.md)**

This is one of two related portfolio systems; the companion —
[**Order Fulfillment Orchestrator**](https://github.com/<your-username>/order-fulfillment-orchestrator) —
demonstrates a saga-style compensating-transaction pattern instead. They're deliberately kept as
separate repositories, each with its own domain-specific documentation, rather than one combined
mono-repo.

## Contents

- [What this does](#what-this-does)
- [Skills demonstrated](#skills-demonstrated)
- [Opening this in Visual Studio](#opening-this-in-visual-studio)
- [Repository layout](#repository-layout)
- [Quick start](#quick-start)
- [Evidence this runs](#evidence-this-runs)
- [Honest limitations of this build](#honest-limitations-of-this-build)
- [Author](#author)

## What this does

| | |
|---|---|
| **Trigger** | Blob Storage (invoice PDF uploaded to `incoming-invoices`) |
| **Pattern demonstrated** | Document-driven workflow + human-in-the-loop approval |
| **Custom code** | C# Azure Function calling Azure AI Document Intelligence's prebuilt invoice model |
| **Failure handling** | Reusable child workflow (`HandleInvoiceFailure`), invoked from every failure branch |

## Skills demonstrated

- **Azure Logic Apps (Standard/single-tenant)**: stateful workflows, child-workflow composition,
  conditional branching, retry policies, Adaptive Card approvals via Teams.
- **C#/.NET 8**: isolated-worker Azure Functions, dependency injection, primary constructors,
  clean separation between HTTP-layer code and testable business logic.
- **Azure integration services**: Blob Storage, Azure AI Document Intelligence, Service Bus,
  Azure SQL, Microsoft Teams.
- **Infrastructure as code**: Bicep provisioning the full resource set with least-privilege RBAC
  role assignments and diagnostic settings — no manual portal clicking.
- **CI/CD**: GitHub Actions with OIDC federated login (no stored client secret), separate
  infra/app pipelines, automated unit test gating.
- **Security**: managed identity end-to-end, Key Vault-referenced secrets, zero credentials in
  source control.
- **Reliability patterns**: explicit failure handling via a reusable child workflow, correlation-ID-
  based distributed tracing.
- **Testing**: xUnit unit tests for the extraction confidence/validation business rules, wired
  into the CI pipeline as a deploy gate.

## Opening this in Visual Studio

Open **`InvoiceProcessingPipeline.sln`**. Solution Explorer gives you:

- `InvoiceExtractionFunction` and `InvoiceExtractionFunction.Tests` as fully native .NET
  projects — build, F5 debug, Test Explorer, and right-click Publish all work exactly as they
  would for any other .NET solution.
- `logicapp`, `infra`, `docs`, and `workflows (CI-CD)` as solution folders, so the Logic App
  workflow JSON, Bicep/SQL scripts, documentation, and GitHub Actions YAML are all visible and
  editable in the same window — the whole repository organized in one place.

One honest caveat: Visual Studio has no Standard Logic App designer (Microsoft has confirmed
there's no plan to bring the VS Code extension's tooling to Visual Studio 2022). The workflow
JSON here is meant to be edited as plain JSON in Visual Studio, or visually through the **Azure
Portal's own Standard Logic App designer** — see the note at the end of
[`docs/architecture.md`](docs/architecture.md) for the full explanation.

## Repository layout

```
InvoiceProcessingPipeline.sln   Open this in Visual Studio
logicapp/                       Standard Logic App project (workflows, connections.json, host.json)
  ProcessInvoiceWorkflow/
  HandleInvoiceFailure/         Reusable child workflow for every failure path
function/                       C# Azure Function (invoice extraction) + xUnit test project
infra/                          Bicep + parameter files + SQL schema/grant scripts
.github/workflows/               deploy-infra.yml, deploy-app.yml
docs/
  architecture.md               Full design rationale, mapped to specific best practices
  deployment-guide.md           Step-by-step: tool install, OIDC setup, deploy, evidence capture
```

## Quick start

Full detail, exact commands, and a troubleshooting table are in
[`docs/deployment-guide.md`](docs/deployment-guide.md) — this is just the shape of it:

1. Install prerequisites and `az login` (guide §0), open the solution in Visual Studio (§1), and
   compile-check it (§2) — this repo was built where NuGet was network-blocked, so this hasn't
   run yet.
2. Read the cost note (§3) before deploying — the Workflow Standard + Elastic Premium plans bill
   continuously; the guide's last step tears everything down once you have your evidence.
3. Set up GitHub → Azure OIDC and repo secrets (§4) — note the guide's warning about the
   `environment:`-scoped OIDC subject claim, which trips up most tutorials' default setup.
4. Deploy infrastructure (§5), then the manual post-infra steps (§6) — SQL schema, the Teams/SQL
   managed API connections, remaining Key Vault secrets.
5. Deploy the app (§7).
6. Trigger the workflow and capture the evidence below (§8), then tear down (§9).

## Evidence this runs

_Screenshots go here once deployed — see [`docs/deployment-guide.md`](docs/deployment-guide.md) §8
for exactly what to capture:_

- [ ] Logic App run history — a successful run
- [ ] Logic App run history — a failure routed through `HandleInvoiceFailure`
- [ ] Application Insights end-to-end trace by correlation ID
- [ ] Green GitHub Actions runs for both `deploy-infra` and `deploy-app`

Source code alone is weaker evidence than source code plus proof it ran — this checklist is the
difference between "I know Logic Apps" and something a hiring manager can independently verify.

## Honest limitations of this build

- **Not compiled in the environment that produced it.** The sandbox that generated this code had
  network policy blocking NuGet (api.nuget.org), so the C# was written carefully and reviewed by
  hand but not `dotnet build`-verified. Run `dotnet restore && dotnet build && dotnet test` (or
  use Visual Studio's Build/Test Explorer) as your first step — see the deployment guide §2 for
  the one spot most likely to need a small fix.
- **JSON/YAML/Bicep were syntactically validated** (parsed successfully, brace-balance checked)
  but the workflow definitions haven't been deployed and run against real Azure resources yet —
  that happens when you actually deploy, which is also step 1 of generating the evidence above.
- **A few steps can't be expressed in Bicep** (contained SQL database users mapped to a managed
  identity, and creating the Teams/SQL managed API connections interactively) and are called out
  explicitly as manual steps in the deployment guide rather than glossed over.
- **No Visual Studio designer for the Logic App workflow itself** — see "Opening this in Visual
  Studio" above. This is a Microsoft tooling gap, not something specific to this repo.

## Author

**Theophilus Unekwu Shaibu** — Senior Backend Engineer (.NET/C#, 9 years)
[Linkedin](https://www.linkedin.com/in/unekwutheoshaibu/).

## License

[MIT](LICENSE)
