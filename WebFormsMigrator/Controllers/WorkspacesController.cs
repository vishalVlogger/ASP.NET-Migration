using Microsoft.AspNetCore.Mvc;
using WebFormsMigrator.Models;
using WebFormsMigrator.Persistence;

namespace WebFormsMigrator.Controllers;

[Route("Workspaces")]
public sealed class WorkspacesController(ModernizationPortfolioStore store) : Controller
{
    [HttpGet("")]
    public IActionResult Index(bool archived = false) { ViewBag.Archived = archived; return View(store.ListWorkspaces(archived).Where(item => archived || !item.IsArchived).ToList()); }

    [HttpGet("Create")]
    public IActionResult Create() => View(new CreateWorkspaceViewModel());

    [HttpPost("Create"), ValidateAntiForgeryToken]
    public IActionResult Create(CreateWorkspaceViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var workspace = store.CreateWorkspace(model.Name, model.Description); return RedirectToAction(nameof(Details), new { id = workspace.Id });
    }

    [HttpGet("{id}")]
    public IActionResult Details(string id, [FromQuery] ProjectListQuery query)
    {
        var workspace = store.GetWorkspace(id); if (workspace is null) return NotFound();
        return View(new WorkspaceDetailsViewModel { Workspace = workspace, Projects = store.ListProjects(id, query), Query = query });
    }

    [HttpGet("{id}/Edit")]
    public IActionResult Edit(string id) { var workspace = store.GetWorkspace(id); return workspace is null ? NotFound() : View(workspace); }

    [HttpPost("{id}/Edit"), ValidateAntiForgeryToken]
    public IActionResult Edit(string id, string name, string description)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80) { ModelState.AddModelError("name", "Enter a workspace name up to 80 characters."); var workspace = store.GetWorkspace(id); return workspace is null ? NotFound() : View(workspace); }
        store.UpdateWorkspace(id, name, description ?? ""); return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id}/Archive"), ValidateAntiForgeryToken]
    public IActionResult Archive(string id) { if (!store.SetWorkspaceArchived(id, true)) return NotFound(); return RedirectToAction(nameof(Index)); }

    [HttpPost("{id}/Restore"), ValidateAntiForgeryToken]
    public IActionResult Restore(string id) { if (!store.SetWorkspaceArchived(id, false)) return NotFound(); return RedirectToAction(nameof(Details), new { id }); }
}
