using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace ApprovalFlow.Web.Infrastructure;

/// <summary>分頁結果</summary>
public sealed class PagedList<T>
{
    public IReadOnlyList<T> Items { get; }
    public int Page { get; }
    public int PageSize { get; }
    public int TotalCount { get; }
    public int TotalPages => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public int FirstItem => TotalCount == 0 ? 0 : (Page - 1) * PageSize + 1;
    public int LastItem => Math.Min(Page * PageSize, TotalCount);

    public PagedList(IEnumerable<T> source, int page, int pageSize)
    {
        var list = source as IReadOnlyList<T> ?? source.ToList();
        PageSize = Math.Clamp(pageSize, 5, 100);
        TotalCount = list.Count;
        Page = Math.Clamp(page, 1, Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize)));
        Items = list.Skip((Page - 1) * PageSize).Take(PageSize).ToList();
    }
}

/// <summary>分頁列所需資訊（保留目前查詢條件）</summary>
public sealed record PagerModel(int Page, int TotalPages, int TotalCount, int FirstItem, int LastItem, string Action, IDictionary<string, string?> RouteValues);

public static class ModelStateExtensions
{
    /// <summary>將商業例外加入 ModelState；ValidationException 會對應到欄位</summary>
    public static void AddBusinessError(this ModelStateDictionary modelState, BusinessException ex, string? prefix = "Input")
    {
        if (ex is MultiValidationException multi)
        {
            // 同時放在欄位與摘要：欄位旁顯示，頁首摘要列出全部
            foreach (var (field, message) in multi.Errors)
            {
                modelState.AddModelError(string.Empty, message);
                if (!string.IsNullOrEmpty(field))
                    modelState.AddModelError(field == "Files" || string.IsNullOrEmpty(prefix) ? field : $"{prefix}.{field}", message);
            }
            return;
        }
        var key = ex is ValidationException { Field: { Length: > 0 } f }
            ? (string.IsNullOrEmpty(prefix) ? f : $"{prefix}.{f}")
            : string.Empty;
        modelState.AddModelError(key, ex.Message);
    }
}

/// <summary>
/// 須變更密碼（新帳號或被重設密碼）的使用者，只能存取變更密碼、登出與錯誤頁。
/// </summary>
public sealed class MustChangePasswordFilter(IRepository<Employee> employees) : IAsyncActionFilter
{
    private static readonly HashSet<string> AllowedControllers = new(StringComparer.OrdinalIgnoreCase) { "Account", "Dev" };

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated == true &&
            context.ActionDescriptor is ControllerActionDescriptor cad &&
            !AllowedControllers.Contains(cad.ControllerName) &&
            !(cad.ControllerName == "Home" && cad.ActionName is "Error" or "StatusCodePage") &&
            http.User.GetUserId() is int id &&
            await employees.GetByIdAsync(id) is { MustChangePassword: true })
        {
            if (http.Request.IsAjax())
                context.Result = new JsonResult(new { success = false, message = "請先變更密碼。" }) { StatusCode = StatusCodes.Status403Forbidden };
            else
                context.Result = new RedirectToActionResult("ChangePassword", "Account", null);
            return;
        }
        await next();
    }
}
