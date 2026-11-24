using ApprovalFlow.Application.Notifications;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Web.Infrastructure;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace ApprovalFlow.Web.Controllers;

/// <summary>通知中心</summary>
public class NotificationsController(NotificationService notifications) : Controller
{
    private const int PageSize = 20;

    public async Task<IActionResult> Index(string filter = "all", int page = 1)
    {
        var unreadOnly = filter == "unread";
        var list = await notifications.ListAsync(unreadOnly);
        return View(new NotificationIndexViewModel
        {
            Filter = unreadOnly ? "unread" : "all",
            Rows = new PagedList<Notification>(list, page, PageSize),
            UnreadCount = await notifications.UnreadCountAsync()
        });
    }

    /// <summary>點擊通知：標示已讀後前往連結（只允許站內連結）</summary>
    [HttpGet]
    public async Task<IActionResult> Open(int id)
    {
        var n = await notifications.MarkReadAsync(id);
        return !string.IsNullOrEmpty(n.Link) && Url.IsLocalUrl(n.Link) ? LocalRedirect(n.Link) : RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> MarkRead(int id, string? returnUrl)
    {
        await notifications.MarkReadAsync(id);
        if (Request.IsAjax()) return Json(new { success = true, unread = await notifications.UnreadCountAsync() });
        return Back(returnUrl);
    }

    [HttpPost]
    public async Task<IActionResult> MarkAllRead(string? returnUrl)
    {
        var count = await notifications.MarkAllReadAsync();
        if (Request.IsAjax()) return Json(new { success = true, unread = 0 });
        TempData.SetSuccess(count > 0 ? $"已將 {count} 則通知標示為已讀。" : "沒有未讀通知。");
        return Back(returnUrl);
    }

    private IActionResult Back(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index));
}
