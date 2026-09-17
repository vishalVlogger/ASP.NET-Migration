using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using WebFormsMigrator.Services;

namespace WebFormsMigrator.Controllers;

[Route("Access")]
public sealed class AccessController(AdminCredentialVerifier verifier, IOptions<AccessOptions> options) : Controller
{
    [AllowAnonymous, HttpGet("Login")]
    public IActionResult Login(string? returnUrl = null) { ViewBag.ReturnUrl = returnUrl; return View(); }

    [AllowAnonymous, HttpPost("Login"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(string? username, string? password, string? returnUrl = null)
    {
        if (!verifier.Verify(username, password)) { ModelState.AddModelError(string.Empty, "The username or password is incorrect."); ViewBag.ReturnUrl = returnUrl; return View(); }
        var claims = new[] { new Claim(ClaimTypes.Name, username!), new Claim("tenant_id", options.Value.DefaultTenantId) };
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)), new AuthenticationProperties { IsPersistent = false });
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/Workspaces");
    }

    [HttpPost("Logout"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout() { await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return RedirectToAction(nameof(Login)); }
}
