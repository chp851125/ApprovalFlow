using System.Security.Claims;
using ApprovalFlow.Application.Abstractions;

namespace ApprovalFlow.Web.Infrastructure;

/// <summary>由 HttpContext 取得目前登入者</summary>
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? User => accessor.HttpContext?.User;

    public bool IsAuthenticated => User?.Identity?.IsAuthenticated == true;
    public int? Id => User?.GetUserId();
    public string Name => User?.Identity?.Name ?? "";
    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();
    public bool IsInRole(string role) => User?.IsInRole(role) == true;
}
