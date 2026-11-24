using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Domain.Entities;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ApprovalFlow.Web.Infrastructure;

/// <summary>
/// 每次請求驗證登入 Cookie：帳號被停用、重設密碼或角色異動（SecurityStamp 變更）時立即登出。
/// 角色若有異動，Cookie 內的角色會在下次驗證時更新。
/// </summary>
public sealed class CookieValidator(IRepository<Employee> employees) : CookieAuthenticationEvents
{
    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var id = context.Principal?.GetUserId();
        var stamp = context.Principal?.FindFirst(UserClaims.SecurityStamp)?.Value;
        var emp = id is int i ? await employees.GetByIdAsync(i) : null;

        if (emp is null || !emp.IsActive || emp.SecurityStamp != stamp)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return;
        }

        // 姓名或角色異動時刷新 Cookie
        var currentRoles = context.Principal!.Claims
            .Where(c => c.Type == System.Security.Claims.ClaimTypes.Role).Select(c => c.Value).Order();
        if (context.Principal.Identity?.Name != emp.Name || !currentRoles.SequenceEqual(emp.Roles.Order()))
        {
            context.ReplacePrincipal(UserClaims.CreatePrincipal(emp));
            context.ShouldRenew = true;
        }
    }
}
