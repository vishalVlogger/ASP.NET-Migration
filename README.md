# Reframe — Legacy .NET Modernization Platform

Reframe is a local-first ASP.NET Core modernization platform. It accepts a classic ASP.NET Web Forms project ZIP—or individual source files—produces an evidence-backed migration readiness assessment, identifies risks, scaffolds an ASP.NET Core MVC target, and compiles the generated project. AI is an optional accelerator, never a startup or analysis requirement.

It has two operating modes:

- **Local structural migration:** works without credentials, scaffolds every Web Forms page and user control, and creates a source-to-target coverage inventory.
- **AI migration:** when `OPENAI_API_KEY` is present, sends the supplied source to the OpenAI Responses API with a strict structured-output schema and generates a semantic vertical slice.
- **Live progress:** asynchronous jobs report real analysis, conversion, validation, and packaging stages in the browser.
- **Build verification:** generated projects are compiled automatically and compiler diagnostics link back to individual migrated files.
- **Completion pipeline:** the actual generated `.csproj` is built, compiler failures can be sent through multiple AI repair rounds, and MVC structure is validated before a package is marked ready.
- **Source coverage:** every ZIP entry is reported as migrated, fallback, skipped, pending, or explicitly reviewed, with source-to-target paths.
- **Dependency-aware batches:** large projects are ordered into foundation, shared-code, user-control, and page batches; markup stays with its code-behind and failed AI batches fall back locally without losing successful work.
- **Side-by-side editor:** compare legacy source with editable MVC code, save changes with automatic rebuild, or regenerate one selected file without rerunning the project.

Implemented product capabilities include deterministic modernization analysis, explainable 0–100 readiness scoring, risk and dependency detection, strategy-aware local migration, source coverage, engineering-effort ranges, compile verification, and Markdown/JSON technical reports. The dashboard exposes the exact affected files and named source pattern behind every finding without displaying uploaded connection-string values.

> Screenshot placeholder: project assessment dashboard showing readiness score, automation potential, complexity categories, risks, and evidence.

## Run

Requirements: .NET 10 SDK (the generated output can target .NET 10 or .NET 8).

```powershell
dotnet run --project .\WebFormsMigrator\WebFormsMigrator.csproj
```

Open the URL printed by ASP.NET Core, upload the project `.zip` (or related `.aspx`, `.ascx`, `.master`, code-behind, and configuration files), then select **Analyze & migrate**.

To enable Gemini conversion for the current PowerShell session:

```powershell
$env:GEMINI_API_KEY = "your-rotated-api-key"
dotnet run --project .\WebFormsMigrator\WebFormsMigrator.csproj
```

Gemini defaults to the stable `gemini-2.5-pro`. You can select it explicitly or override the model:

```powershell
$env:AI__Provider = "Gemini"
$env:Gemini__Model = "gemini-2.5-pro"
```

OpenAI remains supported:

```powershell
$env:OPENAI_API_KEY = "your-api-key"
dotnet run --project .\WebFormsMigrator\WebFormsMigrator.csproj
```

The model defaults to `gpt-5.6-sol`. Override it without changing source:

```powershell
$env:OpenAI__Model = "your-model-id"
```

OpenRouter is supported through its OpenAI-compatible Chat Completions endpoint. For local development, use .NET User Secrets:

```powershell
cd .\WebFormsMigrator
dotnet user-secrets init
dotnet user-secrets set "OpenRouter:ApiKey" "your-key"
dotnet user-secrets set "AI:Provider" "OpenRouter"
dotnet user-secrets set "OpenRouter:Model" "openai/gpt-oss-20b:free"
dotnet run
```

For ordered model failover, configure the `Models` array. The legacy `OpenRouter:Model` value remains the final fallback for backward compatibility:

```powershell
dotnet user-secrets set "OpenRouter:Models:0" "inclusionai/ling-3.0-flash:free"
dotnet user-secrets set "OpenRouter:Models:1" "qwen/qwen3-coder:free"
dotnet user-secrets set "OpenRouter:Models:2" "nvidia/nemotron-3-ultra-550b-a55b:free"
```

The pool tries models in order for model-specific rate limits, timeouts, unavailable endpoints, and malformed responses. Authentication, credit, and account-wide daily-quota failures stop further AI requests and preserve remaining batches as local fallback. OpenRouter batches default to a 180-second per-model limit and 12,000 output tokens. Override completion and repair limits with User Secrets when needed:

```powershell
dotnet user-secrets set "OpenRouter:TimeoutSeconds" "300"
dotnet user-secrets set "OpenRouter:MaxOutputTokens" "16000"
dotnet user-secrets set "AI:MaxRepairRounds" "2"
```

With `AI__Provider=Auto` (the default), provider priority is OpenAI, Gemini, then OpenRouter. Set `AI__Provider` to `Gemini`, `OpenAI`, or `OpenRouter` for deterministic selection. Never store provider keys in `appsettings.json` or source control.

## Fully functional local / no-AI mode

An API key is not required to process the whole ZIP. Local mode creates an MVC controller and Razor view for every `.aspx` page, a shared partial for every `.ascx`, a layout for master-page usage, and `Migration/SourceInventory.md` covering every accepted source file.

For focused review, upload one `.aspx` page with its optional code-behind. The result shows the legacy source mapping, exact destination path, copyable migrated code, and the complete target project tree. A standalone `.aspx.cs` or `.aspx.vb` file also produces a controller and placeholder view when its markup is unavailable.

Without any provider key, Reframe starts normally and provides project assessment, scoring, strategy selection, structural migration, build verification, reports, and the dashboard. Local rules cannot prove the semantics of arbitrary business logic, database behavior, third-party controls, or application-specific state; those sections are explicitly marked for review. Configure Gemini, OpenAI, or OpenRouter only when semantic code-behind assistance is wanted. AI output is merged with the complete local baseline so every source artifact remains accounted for.

## Safety and limits

- API keys are read from configuration or the environment and are never submitted by the browser.
- ZIP uploads are capped at 25 MB compressed, 50 MB expanded, 500 supported source entries, and 2 MB per entry.
- `bin`, `obj`, `packages`, `.git`, `.vs`, and `node_modules` directories are ignored during extraction.
- Generated ZIP paths are normalized to prevent path traversal.
- Generated-project verification confines every destination to its temporary workspace, and report exports include evidence labels rather than raw source excerpts.
- Uploaded connection-string values are converted to named, secret-free placeholders; passwords are not rendered in assessments or technical report exports.
- Migration results expire from the in-memory cache after one hour.
- Generated code is a reviewable starting point; validate authorization, persistence, and business behavior before production use.

## Verify

```powershell
dotnet build .\WebFormsMigrator.slnx
```

## Architecture

- `WebFormsAnalyzer` performs deterministic framework-pattern discovery.
- `ModernizationAssessmentService` inventories Web Forms, data access, state, authentication, configuration, integrations, assets, and vendor dependencies using concrete per-file evidence.
- `ModernizationScoreService` converts weighted, deterministic evidence into explainable category complexity, overall readiness, and automation/manual-review percentages.
- `MigrationEffortEstimator` returns planning ranges and primary evidence-based effort drivers.
- `MigrationReportExporter` creates source-safe Markdown and JSON technical reports.
- `MigrationOrchestrator` runs dependency batches, source coverage, completion classification, and safe local fallback.
- `OpenAiMigrationService` uses the Responses API and strict JSON Schema output.
- `OpenRouterMigrationService` uses an ordered, failure-aware model pool.
- `GeneratedProjectVerifier` builds the actual generated project rather than a synthetic verification project.
- `AiCompilerRepairService` feeds compiler errors back to AI for bounded repair rounds.
- `MvcStructureValidator` checks MVC registration, routes, controllers, views, services, configuration, and static assets.
- `MigrationResultStore` persists generated packages and explicit file review state.

The lightweight domain also defines `Workspace`, `ModernizationProject`, `MigrationRun`, `Assessment`, and `MigrationArtifact` concepts for future multi-tenant evolution. `IFeatureEntitlementService` provides a single future plan boundary; all existing local features remain enabled. No billing or tenant-isolation claim is made in this version.

## Modernization strategies

- **Preserve Behavior:** minimises architectural change and keeps proven ADO.NET/stored-procedure behavior behind explicit seams.
- **Balanced Modernization:** introduces dependency injection, services, and modern configuration while preserving risky database behavior initially.
- **Aggressive Modernization:** recommends layered architecture, EF Core candidates, and state redesign opportunities, with manual review gates.

Data access can be assessed as Preserve ADO.NET, Dapper, EF Core, or Analyse Only. Stored procedures, dynamic SQL, adapters, and schema-dependent operations are never presented as safe automatic EF Core rewrites.

## Commercial roadmap

### Free / local capabilities (implemented)

- Deterministic analysis and readiness reports
- Local Web Forms-to-MVC structural migration
- Strategy and data-access classification
- Risk, source coverage, and effort guidance
- Generated-project compilation and repair workflow
- Optional configured AI providers

### Future hosted capabilities (not implemented)

- Managed project storage and larger hosted workloads
- GitHub import and hosted migration-run history
- Subscription plans and usage entitlements

### Future team capabilities (not implemented)

- Multi-user workspaces, roles, review assignments, and audit history
- Shared migration findings and approval workflows

## Testing and CI

`WebFormsMigrator.Tests` contains deterministic fixtures for simple pages, Session/ViewState, Forms Authentication, ADO.NET/stored procedures, and third-party controls. Startup and local migration tests do not call an AI API. GitHub Actions restores, builds Release, and runs the complete test suite on pushes and pull requests without secrets.
