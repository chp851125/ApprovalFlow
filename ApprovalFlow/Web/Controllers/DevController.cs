using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApprovalFlow.Web.Controllers;

/// <summary>Development 專用：快速切換使用者（其他環境一律回傳 404）</summary>
[AllowAnonymous]
[Route("dev")]
public class DevController(IWebHostEnvironment env, IRepository<Employee> employees, AuditService audit) : Controller
{
    [HttpPost("switch-user")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SwitchUser(int employeeId, string? returnUrl)
    {
        if (!env.IsDevelopment()) return NotFound();

        var emp = await employees.GetByIdAsync(employeeId);
        if (emp is null || !emp.IsActive) return NotFound();

        await audit.LogAsync("DevSwitchUser", nameof(Employee), emp.Id.ToString(),
            $"[開發模式] 快速切換為 {emp.Name}（{emp.Account}）", userId: emp.Id, userName: emp.Name);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserClaims.CreatePrincipal(emp));

        TempData.SetInfo($"已切換為 {emp.Name}（{emp.Account}）");
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) && !returnUrl.Contains("/Account/", StringComparison.OrdinalIgnoreCase)
            ? LocalRedirect(returnUrl)
            : RedirectToAction("Index", "Home");
    }
}
