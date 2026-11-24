using System.Security.Claims;
using ApprovalFlow.Domain.Entities;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace ApprovalFlow.Web.Infrastructure;

public static class UserClaims
{
    public const string Account = "af:account";
    public const string DepartmentId = "af:dept";
    public const string SecurityStamp = "af:stamp";

    public static ClaimsPrincipal CreatePrincipal(Employee e)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, e.Id.ToString()),
            new(ClaimTypes.Name, e.Name),
            new(Account, e.Account),
            new(DepartmentId, e.DepartmentId.ToString()),
            new(SecurityStamp, e.SecurityStamp)
        };
        claims.AddRange(e.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    public static int? GetUserId(this ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public static string GetAccount(this ClaimsPrincipal user) => user.FindFirstValue(Account) ?? "";
}
