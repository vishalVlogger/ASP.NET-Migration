using System.Text.RegularExpressions;
using WebFormsMigrator.Models;

namespace WebFormsMigrator.Services;

public sealed partial class ModernizationAssessmentService(
    ModernizationScoreService scoring,
    MigrationEffortEstimator effortEstimator)
{
    private sealed record Rule(string Id, string Category, string Title, string Pattern,
        FindingSeverity Severity, string Explanation, string Why, string Recommendation,
        RegexOptions Options = RegexOptions.IgnoreCase);

    private static readonly Rule[] Rules =
    [
        new("ado-net", "Data Access", "ADO.NET usage", @"\b(?:System\.Data|IDbConnection|DbConnection)\b", FindingSeverity.Medium, "Direct ADO.NET APIs are used.", "Database behavior and transaction boundaries need to remain equivalent.", "Preserve proven behavior first; isolate database calls behind injected services."),
        new("sql-connection", "Data Access", "SqlConnection usage", @"\bSqlConnection\b", FindingSeverity.Medium, "SQL Server connections are opened directly.", "Connection lifetime, secrets, and retries must be modernized safely.", "Use injected configuration and scoped repository or query services."),
        new("sql-command", "Data Access", "SqlCommand usage", @"\bSqlCommand\b", FindingSeverity.Medium, "SQL commands are constructed directly.", "Parameters and command behavior require review before translation.", "Preserve parameterization and classify each command before changing its data technology."),
        new("sql-adapter", "Data Access", "SqlDataAdapter usage", @"\bSqlDataAdapter\b", FindingSeverity.High, "Disconnected DataSet-style loading is present.", "Adapters do not have a direct EF Core equivalent.", "Map result shapes explicitly; consider preserved ADO.NET or Dapper."),
        new("dataset", "Data Access", "DataSet usage", @"\bDataSet\b", FindingSeverity.High, "Untyped DataSet data structures are used.", "Untyped tables obscure contracts and nullability.", "Introduce typed result models incrementally after behavior is characterized."),
        new("datatable", "Data Access", "DataTable usage", @"\bDataTable\b", FindingSeverity.Medium, "DataTable result structures are used.", "Dynamic column access needs explicit mapping and tests.", "Map stable result shapes to typed models; preserve dynamic reports for manual review."),
        new("execute-reader", "Data Access", "ExecuteReader calls", @"\.ExecuteReader(?:Async)?\s*\(", FindingSeverity.Medium, "Reader-based queries were found.", "Column ordering and null handling can be behavior-sensitive.", "Automate only simple parameterized reads with known result shapes."),
        new("execute-scalar", "Data Access", "ExecuteScalar calls", @"\.ExecuteScalar(?:Async)?\s*\(", FindingSeverity.Low, "Scalar database operations were found.", "Return-type conversion and null semantics must be retained.", "These are candidates for focused Dapper or ADO.NET wrappers after tests are added."),
        new("stored-procedure", "Data Access", "Stored procedure references", @"CommandType\s*=\s*CommandType\.StoredProcedure|CommandType\.StoredProcedure", FindingSeverity.High, "Commands explicitly use stored-procedure mode.", "Stored procedures may contain business rules that source analysis cannot inspect.", "Preserve stored procedures initially rather than rewriting them automatically."),
        new("configuration-manager", "Configuration", "ConfigurationManager usage", @"\bConfigurationManager\b", FindingSeverity.Medium, "Legacy static configuration access is used.", "ASP.NET Core configuration is injected and environment-aware.", "Move settings to typed options or IConfiguration; keep secrets outside generated files."),
        new("connection-strings", "Configuration", "Connection string usage", @"ConnectionStrings\s*\[|<connectionStrings\b", FindingSeverity.High, "Connection-string access or configuration exists.", "Credentials must not be copied into source or reports.", "Use named secret-free placeholders and supply values through user secrets or environment variables."),
        new("session", "State", "Session state usage", @"\bSession\s*(?:\[|\.)", FindingSeverity.Medium, "Per-user Session state is read or written.", "In-memory Session needs affinity or a distributed store when an application scales out.", "Move durable state to a database, distributed cache, claims, or explicit request models."),
        new("viewstate", "State", "ViewState usage", @"\bViewState\s*(?:\[|\.)|__VIEWSTATE", FindingSeverity.High, "Web Forms ViewState is used.", "MVC does not recreate the hidden page-state lifecycle.", "Replace ViewState with typed view models, request values, or explicit client state."),
        new("application-state", "State", "Application state usage", @"\bApplication\s*(?:\[|\.)", FindingSeverity.High, "Process-wide application state is used.", "Process memory is not shared or durable across instances.", "Move shared state to an external store and define concurrency behavior."),
        new("cache", "State", "Cache usage", @"\b(?:HttpRuntime\.)?Cache\s*(?:\[|\.)", FindingSeverity.Medium, "Legacy cache APIs are used.", "Eviction and distribution semantics differ in ASP.NET Core.", "Choose IMemoryCache or IDistributedCache explicitly and test expiration behavior."),
        new("forms-auth", "Authentication", "Forms Authentication", @"\bFormsAuthentication\b|<forms\b", FindingSeverity.Critical, "Forms Authentication is configured or called.", "Authentication cookies, redirects, and ticket protection differ in ASP.NET Core.", "Convert Forms Authentication to ASP.NET Core Cookie Authentication."),
        new("membership", "Authentication", "MembershipProvider", @"\bMembershipProvider\b|<membership\b|\bMembership\.", FindingSeverity.Critical, "Legacy Membership APIs are used.", "The provider model is not available directly in ASP.NET Core.", "Map user stores and password compatibility deliberately, usually behind ASP.NET Core Identity or a custom service."),
        new("roles", "Authentication", "RoleProvider", @"\bRoleProvider\b|<roleManager\b|\bRoles\.", FindingSeverity.High, "Legacy role-provider APIs are used.", "Role loading and authorization policies need explicit migration.", "Translate roles to claims and policy-based authorization."),
        new("authorization", "Authentication", "Authorization configuration", @"<authorization\b|<allow\s+|<deny\s+", FindingSeverity.High, "Location or Web.config authorization rules are present.", "Path-based Web.config authorization is not automatically applied in ASP.NET Core.", "Map each allow/deny rule to endpoint authorization policies and test precedence."),
        new("handler", "Web Infrastructure", "Custom HTTP handlers", @"<httpHandlers\b|<handlers\b|IHttpHandler\b|\.ashx\b", FindingSeverity.High, "Custom HTTP handler behavior is present.", "The System.Web handler pipeline does not exist in ASP.NET Core.", "Convert handlers to endpoints, controllers, or middleware."),
        new("module", "Web Infrastructure", "Custom HTTP modules", @"<httpModules\b|<modules\b|IHttpModule\b", FindingSeverity.High, "Custom HTTP modules are configured or implemented.", "Pipeline order and lifecycle events differ in ASP.NET Core.", "Convert modules to focused middleware and verify ordering."),
        new("server-controls", "UI", "ASP.NET server controls", @"<asp:[A-Za-z][A-Za-z0-9]*\b", FindingSeverity.Medium, "Web Forms server controls are present.", "Server controls depend on page lifecycle, postback, and ViewState.", "Replace controls with Razor HTML, tag helpers, partials, and explicit endpoints."),
        new("gridview", "UI", "GridView controls", @"<asp:GridView\b", FindingSeverity.High, "GridView controls are present.", "Binding, paging, editing, and events need explicit MVC equivalents.", "Replace GridView with Razor views, partials, or a modern grid library."),
        new("repeater", "UI", "Repeater controls", @"<asp:Repeater\b", FindingSeverity.Medium, "Repeater controls are present.", "Templated binding must become explicit Razor iteration.", "Render typed collections with Razor foreach or a partial."),
        new("datalist", "UI", "DataList controls", @"<asp:DataList\b", FindingSeverity.Medium, "DataList controls are present.", "Layout templates and item events need explicit replacements.", "Use typed Razor components or partials and controller actions."),
        new("updatepanel", "UI", "UpdatePanel controls", @"<asp:UpdatePanel\b", FindingSeverity.High, "Partial-postback panels are present.", "ASP.NET Core has no Microsoft AJAX partial-postback lifecycle.", "Replace with fetch, HTMX, or focused partial endpoints."),
        new("scriptmanager", "UI", "ScriptManager controls", @"<asp:ScriptManager\b", FindingSeverity.Medium, "ScriptManager is present.", "Automatic Web Forms script registration will not carry over.", "Inventory scripts and load only explicit modern dependencies."),
        new("validators", "UI", "Web Forms validators", @"<asp:(?:RequiredFieldValidator|RangeValidator|CompareValidator|RegularExpressionValidator|CustomValidator|ValidationSummary)\b", FindingSeverity.Medium, "Web Forms validation controls are present.", "Validation rules need server-side model validation and optional client adapters.", "Translate rules to data annotations or FluentValidation-style explicit validators."),
        new("file-upload", "UI", "File upload controls", @"<asp:FileUpload\b", FindingSeverity.High, "Legacy file upload controls are present.", "Upload limits, storage, scanning, and path safety need explicit handling.", "Use IFormFile with size/type validation and non-executable storage."),
        new("telerik", "Dependencies", "Telerik components", @"\bTelerik\b|<telerik:", FindingSeverity.Critical, "Telerik references or controls are present.", "Vendor controls usually require product-specific replacements and licensing decisions.", "Inventory each control and select supported ASP.NET Core replacements."),
        new("devexpress", "Dependencies", "DevExpress components", @"\bDevExpress\b|<dx:", FindingSeverity.Critical, "DevExpress references or controls are present.", "Vendor controls require product-specific migration and licensing review.", "Map each component to a supported ASP.NET Core DevExpress or alternative control."),
        new("ajax-toolkit", "Dependencies", "AjaxControlToolkit components", @"\bAjaxControlToolkit\b|<ajaxToolkit:", FindingSeverity.High, "Ajax Control Toolkit components are present.", "These controls depend on the Web Forms AJAX lifecycle.", "Replace behavior with focused JavaScript and accessible HTML components."),
        new("crystal", "Dependencies", "Crystal Reports", @"\bCrystalDecisions\b|CrystalReportViewer", FindingSeverity.Critical, "Crystal Reports references are present.", "Report runtimes and viewer hosting need a separate supported deployment design.", "Treat reports as a dedicated migration stream and verify runtime support/licensing."),
        new("wcf", "Integration", "WCF references", @"System\.ServiceModel|<system\.serviceModel\b|\.svc\b", FindingSeverity.High, "WCF client, service, or configuration evidence is present.", "Server-side WCF is not part of ASP.NET Core and bindings are compatibility-sensitive.", "Inventory contracts and bindings; choose CoreWCF, REST, or gRPC based on constraints."),
        new("soap", "Integration", "SOAP/Web Service references", @"\.asmx\b|SoapHttpClientProtocol|WebService\b|<applicationSettings\b[^>]*>", FindingSeverity.High, "SOAP or legacy Web Service evidence is present.", "Generated proxies and XML contracts can be behavior-sensitive.", "Preserve contracts initially and regenerate supported clients with integration tests."),
        new("javascript", "Dependencies", "JavaScript dependencies", @"<script\b[^>]*src\s*=|\b(?:jQuery|jquery|prototype\.js|MicrosoftAjax)\b", FindingSeverity.Medium, "Explicit JavaScript dependencies were found.", "Plugin compatibility and load ordering can affect migrated behavior.", "Inventory versions and replace only after browser behavior is characterized.")
    ];

    public MigrationReadinessReport Assess(string projectName, IReadOnlyCollection<SourceFile> sources,
        ModernizationStrategy strategy, DataAccessStrategy dataStrategy)
    {
        var files = sources.Where(file => !file.IsSkipped).ToList();
        var text = files.Where(file => !file.IsBinary).ToList();
        var findings = new List<ModernizationFinding>();
        AddPathFindings(text, findings);
        foreach (var rule in Rules) AddRuleFinding(text, findings, rule);
        AddProjectAndPackageFindings(text, findings);

        var inventory = new SourceInventory
        {
            TotalFiles = files.Count, TextFiles = text.Count, BinaryFiles = files.Count - text.Count,
            WebFormsPages = CountExtension(files, ".aspx"), UserControls = CountExtension(files, ".ascx"),
            MasterPages = CountExtension(files, ".master"),
            CodeBehindFiles = files.Count(file => Regex.IsMatch(file.Path, @"\.(?:aspx|ascx|master)\.(?:cs|vb)$", RegexOptions.IgnoreCase)),
            StaticAssets = files.Count(MigrationFileClassifier.IsWebAsset),
            JavaScriptFiles = CountExtension(files, ".js")
        };
        var framework = DetectFramework(text);
        var stack = BuildStack(inventory, findings);
        var recommendations = findings.Select(item => item.Recommendation).Distinct().ToList();
        recommendations.Insert(0, StrategyRecommendation(strategy));
        recommendations.Add(DataRecommendation(dataStrategy, findings));
        var report = new MigrationReadinessReport
        {
            ProjectName = projectName, DetectedFramework = framework, DetectedTechnologyStack = stack,
            SourceFiles = inventory, Findings = findings.OrderByDescending(item => item.Severity).ThenBy(item => item.Title).ToList(),
            WebFormsArtifacts = findings.Where(item => item.Category is "Web Forms" or "UI").Select(item => item.Title).Distinct().ToList(),
            DataAccessFindings = findings.Where(item => item.Category == "Data Access").Select(item => $"{item.Title}: {item.OccurrenceCount}").ToList(),
            AuthenticationFindings = findings.Where(item => item.Category == "Authentication").Select(item => item.Title).ToList(),
            ConfigurationFindings = findings.Where(item => item.Category == "Configuration").Select(item => item.Title).ToList(),
            ThirdPartyDependencies = findings.Where(item => item.Category == "Dependencies" && item.Id is not "javascript").Select(item => item.Title).ToList(),
            MigrationRisks = findings.Where(item => item.Severity >= FindingSeverity.High).Select(item => item.Title).ToList(),
            ModernizationRecommendations = recommendations, Strategy = strategy, DataAccessStrategy = dataStrategy,
            DataOperations = ClassifyDataOperations(findings, dataStrategy)
        };
        report.ModernizationScore = scoring.Score(report);
        report.AutomationPercentage = report.ModernizationScore.AutomationPotential;
        report.ManualReviewPercentage = Math.Max(0, 100 - report.AutomationPercentage - HighRiskShare(findings));
        report.HighRiskPercentage = 100 - report.AutomationPercentage - report.ManualReviewPercentage;
        report.Effort = effortEstimator.Estimate(report);
        return report;
    }

    private static void AddPathFindings(List<SourceFile> files, List<ModernizationFinding> findings)
    {
        AddPath("aspx", "Web Forms", "ASP.NET Web Forms pages", files.Where(f => HasExtension(f.Path, ".aspx")), FindingSeverity.Medium, "Web Forms pages use a stateful page lifecycle.", "Each page needs an explicit MVC route, action, and view.", "Convert pages to controllers and Razor views.", findings);
        AddPath("ascx", "Web Forms", "ASCX user controls", files.Where(f => HasExtension(f.Path, ".ascx")), FindingSeverity.Medium, "Reusable Web Forms user controls were found.", "Control properties and events need explicit component contracts.", "Convert controls to Razor partials, view components, or tag helpers.", findings);
        AddPath("master", "Web Forms", "Master pages", files.Where(f => HasExtension(f.Path, ".master")), FindingSeverity.Medium, "Master pages define shared layout.", "ContentPlaceHolder behavior maps imperfectly to Razor sections.", "Convert master pages to shared Razor layouts.", findings);
        AddPath("code-behind", "Web Forms", "Code-behind files", files.Where(f => Regex.IsMatch(f.Path, @"\.(?:aspx|ascx|master)\.(?:cs|vb)$", RegexOptions.IgnoreCase)), FindingSeverity.High, "Page behavior exists in code-behind.", "Lifecycle and control coupling require semantic review.", "Move business logic into injected services and map events to actions.", findings);
        AddPath("app-code", "Web Forms", "App_Code folder", files.Where(f => f.Path.Replace('\\', '/').Contains("/App_Code/", StringComparison.OrdinalIgnoreCase) || f.Path.StartsWith("App_Code/", StringComparison.OrdinalIgnoreCase)), FindingSeverity.High, "Dynamically compiled App_Code sources were found.", "ASP.NET Core requires normal compiled project items.", "Move App_Code types into explicit domain, service, or infrastructure folders.", findings);
        AddPath("global-asax", "Web Infrastructure", "Global.asax", files.Where(f => Path.GetFileName(f.Path).Equals("Global.asax", StringComparison.OrdinalIgnoreCase)), FindingSeverity.High, "Application lifecycle hooks are defined in Global.asax.", "Startup, errors, routes, and request events move to different ASP.NET Core mechanisms.", "Map startup to Program.cs and request events to middleware.", findings);
        AddPath("web-config", "Configuration", "Web.config", files.Where(f => Path.GetFileName(f.Path).Equals("Web.config", StringComparison.OrdinalIgnoreCase)), FindingSeverity.Medium, "Legacy application configuration was found.", "System.Web sections are not consumed by ASP.NET Core.", "Translate only required settings and keep secret values external.", findings);
        AddPath("packages-config", "Dependencies", "packages.config", files.Where(f => Path.GetFileName(f.Path).Equals("packages.config", StringComparison.OrdinalIgnoreCase)), FindingSeverity.Medium, "Legacy NuGet packages.config is used.", "SDK-style projects use PackageReference and some packages may be framework-only.", "Review each exact package and migrate supported dependencies to PackageReference.", findings);
        AddPath("static-assets", "Assets", "Static assets", files.Where(MigrationFileClassifier.IsWebAsset), FindingSeverity.Low, "Static web assets were found.", "Paths and server-side URL rewriting may change.", "Preserve assets under wwwroot and validate every reference.", findings);
    }

    private static void AddProjectAndPackageFindings(List<SourceFile> files, List<ModernizationFinding> findings)
    {
        var oldProjects = files.Where(file => file.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) &&
            !Regex.IsMatch(file.Content, @"<Project\s+Sdk\s*=", RegexOptions.IgnoreCase));
        AddPath("old-csproj", "Configuration", "Old .csproj format", oldProjects, FindingSeverity.High, "A non-SDK-style C# project file was found.", "Legacy imports and implicit compile items do not map directly.", "Create an SDK-style web project and add only required references.", findings);
        var thirdParty = files.Where(file => !file.IsBinary).SelectMany(file => ThirdPartyRegistrationRegex().Matches(file.Content)
            .Select(match => new AssessmentEvidence(file.Path, "registered server-control namespace", 1))).ToList();
        if (thirdParty.Count > 0) findings.Add(Create("third-party-controls", "Dependencies", "Third-party server controls", FindingSeverity.High,
            "Registered non-ASP.NET server-control namespaces were found.", "Unknown control lifecycle and licensing requirements prevent safe automatic conversion.",
            "Inventory registered controls and select supported replacements.", thirdParty));
    }

    private static void AddRuleFinding(List<SourceFile> files, List<ModernizationFinding> findings, Rule rule)
    {
        var evidence = files.Select(file => (file, matches: Regex.Matches(file.Content, rule.Pattern, rule.Options).Count))
            .Where(item => item.matches > 0).Select(item => new AssessmentEvidence(item.file.Path, rule.Title, item.matches)).ToList();
        if (evidence.Count > 0) findings.Add(Create(rule.Id, rule.Category, rule.Title, rule.Severity, rule.Explanation, rule.Why, rule.Recommendation, evidence));
    }

    private static void AddPath(string id, string category, string title, IEnumerable<SourceFile> matches,
        FindingSeverity severity, string explanation, string why, string recommendation, List<ModernizationFinding> findings)
    {
        var evidence = matches.Select(file => new AssessmentEvidence(file.Path, $"file type: {Path.GetExtension(file.Path)}")).ToList();
        if (evidence.Count > 0) findings.Add(Create(id, category, title, severity, explanation, why, recommendation, evidence));
    }

    private static ModernizationFinding Create(string id, string category, string title, FindingSeverity severity,
        string explanation, string why, string recommendation, List<AssessmentEvidence> evidence) => new()
        { Id = id, Category = category, Title = title, Severity = severity, Explanation = explanation, WhyItMatters = why, Recommendation = recommendation, Evidence = evidence };

    private static string DetectFramework(List<SourceFile> files)
    {
        foreach (var file in files.Where(file => file.Path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || file.Path.EndsWith(".config", StringComparison.OrdinalIgnoreCase)))
        {
            var match = FrameworkRegex().Match(file.Content);
            if (match.Success) return $".NET Framework {match.Groups[1].Value.TrimStart('v', 'V')} (declared in {file.Path})";
        }
        return "Unknown (not declared in uploaded source)";
    }

    private static List<string> BuildStack(SourceInventory inventory, List<ModernizationFinding> findings)
    {
        var stack = new List<string>();
        if (inventory.WebFormsPages + inventory.UserControls > 0) stack.Add("ASP.NET Web Forms");
        if (findings.Any(f => f.Category == "Data Access")) stack.Add("ADO.NET / SQL Server");
        if (findings.Any(f => f.Id == "wcf")) stack.Add("WCF");
        if (findings.Any(f => f.Id == "soap")) stack.Add("SOAP Web Services");
        if (inventory.JavaScriptFiles > 0 || findings.Any(f => f.Id == "javascript")) stack.Add("JavaScript");
        return stack;
    }

    private static List<DataOperationAssessment> ClassifyDataOperations(List<ModernizationFinding> findings, DataAccessStrategy strategy)
    {
        var operations = findings.Where(f => f.Category == "Data Access" && f.Id is not "ado-net").Select(f => new DataOperationAssessment
        {
            Operation = f.Title, Count = f.OccurrenceCount,
            AutomationClassification = f.Id == "stored-procedure" || f.Id is "dataset" or "sql-adapter" ? "Manual review required" :
                strategy is DataAccessStrategy.Dapper or DataAccessStrategy.PreserveAdoNet ? "Candidate for assisted conversion" : "Review before automation",
            Reason = f.Id == "stored-procedure" ? "Procedure implementation is outside the uploaded C# source and is never rewritten automatically." :
                strategy == DataAccessStrategy.EfCore ? "EF Core conversion requires a known schema, keys, relationships, and behavioral tests." : "Simple parameterized operations with known result shapes are safer than dynamic SQL."
        }).ToList();
        return operations;
    }

    private static string StrategyRecommendation(ModernizationStrategy strategy) => strategy switch
    {
        ModernizationStrategy.PreserveBehavior => "Preserve behavior first: minimise architectural change and retain proven ADO.NET and stored procedures.",
        ModernizationStrategy.AggressiveModernization => "Use a layered target architecture and redesign state only behind characterization tests and explicit review gates.",
        _ => "Use injected services and modern configuration while preserving risky database behavior initially."
    };

    private static string DataRecommendation(DataAccessStrategy strategy, List<ModernizationFinding> findings) => strategy switch
    {
        DataAccessStrategy.Dapper => "Use Dapper only for simple, parameterized operations with known result shapes; review stored procedures and dynamic SQL.",
        DataAccessStrategy.EfCore => "Identify EF Core candidates, but do not translate stored procedures or complex SQL without schema and behavioral review.",
        DataAccessStrategy.PreserveAdoNet => "Preserve ADO.NET behind injected services and centralize connection management.",
        _ => findings.Any(f => f.Category == "Data Access") ? "Analyse data operations before selecting a target persistence technology." : "No concrete data-access API was detected in the uploaded source."
    };

    private static int HighRiskShare(List<ModernizationFinding> findings) => findings.Count == 0 ? 0 :
        Math.Clamp((int)Math.Round(100d * findings.Count(f => f.Severity >= FindingSeverity.High) / findings.Count), 0, 35);
    private static bool HasExtension(string path, string extension) => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);
    private static int CountExtension(IEnumerable<SourceFile> files, string extension) => files.Count(file => HasExtension(file.Path, extension));

    [GeneratedRegex(@"(?:TargetFrameworkVersion\s*>\s*|targetFramework\s*=\s*[\""'])(v?\d+(?:\.\d+){1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex FrameworkRegex();
    [GeneratedRegex(@"<%@\s*Register\b[^%]*(?:Namespace|TagPrefix)\s*=", RegexOptions.IgnoreCase)]
    private static partial Regex ThirdPartyRegistrationRegex();
}
