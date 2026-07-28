# Reframe — Web Forms to ASP.NET Core MVC

Reframe is a runnable ASP.NET Core MVC migration assistant. It accepts a complete classic ASP.NET Web Forms project ZIP—or individual markup and code-behind files—identifies framework-specific patterns, and produces a migration plan plus downloadable modern MVC source files.

It has two operating modes:

- **Local structural migration:** works without credentials, scaffolds every Web Forms page and user control, and creates a source-to-target coverage inventory.
- **AI migration:** when `OPENAI_API_KEY` is present, sends the supplied source to the OpenAI Responses API with a strict structured-output schema and generates a semantic vertical slice.
- **Live progress:** asynchronous jobs report real analysis, conversion, validation, and packaging stages in the browser.
- **Build verification:** generated projects are compiled automatically and compiler diagnostics link back to individual migrated files.
- **Completion pipeline:** the actual generated `.csproj` is built, compiler failures can be sent through multiple AI repair rounds, and MVC structure is validated before a package is marked ready.
- **Source coverage:** every ZIP entry is reported as migrated, fallback, skipped, pending, or explicitly reviewed, with source-to-target paths.
- **Dependency-aware batches:** large projects are ordered into foundation, shared-code, user-control, and page batches; markup stays with its code-behind and failed AI batches fall back locally without losing successful work.
- **Side-by-side editor:** compare legacy source with editable MVC code, save changes with automatic rebuild, or regenerate one selected file without rerunning the project.

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

## Local mode versus AI mode

An API key is not required to process the whole ZIP. Local mode creates an MVC controller and Razor view for every `.aspx` page, a shared partial for every `.ascx`, a layout for master-page usage, and `Migration/SourceInventory.md` covering every accepted source file.

For focused review, upload one `.aspx` page with its optional code-behind. The result shows the legacy source mapping, exact destination path, copyable migrated code, and the complete target project tree. A standalone `.aspx.cs` or `.aspx.vb` file also produces a controller and placeholder view when its markup is unavailable.

Local rules cannot reliably understand arbitrary business logic, database behavior, third-party controls, or application-specific state. Those sections are marked for review. Configure Gemini, OpenAI, or OpenRouter when you want semantic code-behind conversion; AI output is merged with the complete local baseline so every source artifact remains accounted for.

## Safety and limits

- API keys are read from configuration or the environment and are never submitted by the browser.
- ZIP uploads are capped at 25 MB compressed, 50 MB expanded, 500 supported source entries, and 2 MB per entry.
- `bin`, `obj`, `packages`, `.git`, `.vs`, and `node_modules` directories are ignored during extraction.
- Generated ZIP paths are normalized to prevent path traversal.
- Migration results expire from the in-memory cache after one hour.
- Generated code is a reviewable starting point; validate authorization, persistence, and business behavior before production use.

## Verify

```powershell
dotnet build .\WebFormsMigrator.slnx
```

## Architecture

- `WebFormsAnalyzer` performs deterministic framework-pattern discovery.
- `MigrationOrchestrator` runs dependency batches, source coverage, completion classification, and safe local fallback.
- `OpenAiMigrationService` uses the Responses API and strict JSON Schema output.
- `OpenRouterMigrationService` uses an ordered, failure-aware model pool.
- `GeneratedProjectVerifier` builds the actual generated project rather than a synthetic verification project.
- `AiCompilerRepairService` feeds compiler errors back to AI for bounded repair rounds.
- `MvcStructureValidator` checks MVC registration, routes, controllers, views, services, configuration, and static assets.
- `MigrationResultStore` persists generated packages and explicit file review state.
