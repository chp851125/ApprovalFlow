using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Web.Infrastructure;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace ApprovalFlow.Web.Controllers;

public class AccountController(
    AuthService auth,
    AuditService audit,
    SettingsService settings,
    IRepository<Employee> employees) : Controller
{
    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToLocal(returnUrl);
        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [AllowAnonymous]
    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid) return View(model);

        var result = await auth.LoginAsync(model.Account, model.Password);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            model.Password = "";
            return View(model);
        }

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
            UserClaims.CreatePrincipal(result.Employee!),
            new AuthenticationProperties
            {
                IsPersistent = model.RememberMe,
                ExpiresUtc = model.RememberMe ? DateTimeOffset.UtcNow.AddDays(7) : null,
                AllowRefresh = true
            });
        return RedirectToLocal(model.ReturnUrl);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await audit.LogAsync("Logout", "Employee", User.GetUserId()?.ToString(), "登出");
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction(nameof(Login));
    }

    [HttpGet]
    public async Task<IActionResult> ChangePassword() => View(await BuildChangePasswordVm(new ChangePasswordInput()));

    [HttpPost]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("login")]
    public async Task<IActionResult> ChangePassword([Bind(Prefix = "Input")] ChangePasswordInput input)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var emp = await auth.ChangePasswordAsync(User.GetUserId()!.Value, input.CurrentPassword, input.NewPassword);
                // SecurityStamp 已更新：以新身分重新簽發 Cookie，其他裝置的登入會失效
                await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, UserClaims.CreatePrincipal(emp));
                TempData.SetSuccess("密碼已變更。");
                return RedirectToAction("Index", "Home");
            }
            catch (BusinessException ex) when (ex is not ForbiddenException)
            {
                ModelState.AddBusinessError(ex);
            }
        }
        input.CurrentPassword = input.NewPassword = input.ConfirmPassword = "";
        return View(await BuildChangePasswordVm(input));
    }

    private async Task<ChangePasswordViewModel> BuildChangePasswordVm(ChangePasswordInput input)
    {
        var id = User.GetUserId()!.Value;
        return new ChangePasswordViewModel
        {
            Input = input,
            IsForced = (await employees.GetByIdAsync(id))?.MustChangePassword == true,
            PolicyText = (await settings.GetPasswordPolicyAsync()).Describe()
        };
    }

    [AllowAnonymous]
    public IActionResult AccessDenied()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View();
    }

    private IActionResult RedirectToLocal(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl)
            ? LocalRedirect(returnUrl)
            : RedirectToAction("Index", "Home");
}
