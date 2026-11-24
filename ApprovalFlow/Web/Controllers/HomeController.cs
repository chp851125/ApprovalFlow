using System.Diagnostics;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;
using ApprovalFlow.Web.Infrastructure;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ApprovalFlow.Web.Controllers;

public class HomeController(
    IClock clock,
    IRepository<Document> documents,
    IRepository<Employee> employees,
    IRepository<Notification> notifications,
    IRepository<Holiday> holidays,
    IRepository<Delegation> delegations,
    IWebHostEnvironment env) : Controller
{
    public async Task<IActionResult> Index()
    {
        var me = User.GetUserId()!.Value;
        var now = clock.Now;
        var today = clock.Today;

        // 我是被代理人的委託（今天有效）→ 我也能看到被代理人的待辦
        var acting = (await delegations.QueryAsync(d => d.DelegateId == me)).Where(d => d.IsEffectiveOn(today)).ToList();

        var all = await documents.GetAllAsync();
        var mine = all.Where(d => d.ApplicantId == me).ToList();
        var pendingDocs = all.Where(d => d.Status.IsActiveFlow() && d.GetCurrentStep() is { } s &&
                s.Assignees.Any(a => a.Decision == AssigneeDecision.Pending &&
                    (a.EmployeeId == me || (d.ApplicantId != me && acting.Any(x => x.PrincipalId == a.EmployeeId && x.IsEffectiveOn(today, d.DocumentType))))))
            .OrderByDescending(d => d.IsUrgent).ThenBy(d => d.GetCurrentStep()!.DueAt).ToList();
        var pending = pendingDocs.Count;

        var names = (await employees.GetAllAsync()).ToDictionary(e => e.Id, e => e.Name);
        var myDelegations = (await delegations.QueryAsync(d => d.PrincipalId == me || d.DelegateId == me))
            .Where(d => d.IsEnabled && d.EndDate >= today).OrderBy(d => d.StartDate).ToList();

        var vm = new HomeViewModel
        {
            UserName = User.Identity?.Name ?? "",
            Greeting = now.Hour switch { < 12 => "早安", < 18 => "午安", _ => "晚安" },
            TodayText = $"{today:yyyy 年 M 月 d 日}（{"日一二三四五六"[(int)today.DayOfWeek]}）",
            PendingCount = pending,
            InProgressCount = mine.Count(d => d.Status.IsActiveFlow()),
            DraftCount = mine.Count(d => d.Status is DocumentStatus.Draft or DocumentStatus.Returned),
            ApprovedThisMonth = mine.Count(d => d.Status == DocumentStatus.Approved && d.CompletedAt is { } c && c.Year == now.Year && c.Month == now.Month),
            UnreadNotifications = (await notifications.QueryAsync(n => n.RecipientId == me && !n.IsRead)).Count,
            RecentDocuments = mine.OrderByDescending(d => d.UpdatedAt).Take(6)
                .Select(d => new HomeDocumentRow(d.Id, d.DocumentNo, d.DocumentType, d.Title, d.Status, d.UpdatedAt, d.GetCurrentStep()?.Name))
                .ToList(),
            PendingDocuments = pendingDocs.Take(6)
                .Select(d => new HomeDocumentRow(d.Id, d.DocumentNo, d.DocumentType, d.Title, d.Status,
                    d.GetCurrentStep()!.DueAt ?? d.UpdatedAt, names.GetValueOrDefault(d.ApplicantId)))
                .ToList(),
            UpcomingHolidays = (await holidays.QueryAsync(h => h.Date >= today)).OrderBy(h => h.Date).Take(4).ToList(),
            ActiveDelegations = myDelegations,
            EmployeeNames = names
        };
        return View(vm);
    }

    [AllowAnonymous]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
    {
        var feature = HttpContext.Features.Get<IExceptionHandlerPathFeature>();
        var vm = new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
            Detail = env.IsDevelopment() ? feature?.Error.ToString() : null
        };
        Response.StatusCode = StatusCodes.Status500InternalServerError;
        return View(vm);
    }

    [AllowAnonymous]
    [Route("Home/StatusCode/{code:int}")]
    public IActionResult StatusCodePage(int code)
    {
        var vm = new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier,
            StatusCode = code,
            Title = code switch { 404 => "找不到頁面", 403 => "沒有權限", 400 => "請求無效", 429 => "操作過於頻繁", _ => $"發生錯誤（{code}）" },
            Message = code switch
            {
                404 => "您要找的頁面不存在或已被移除，請確認網址是否正確。",
                403 => "您沒有權限存取此頁面。",
                400 => "請求內容有誤，請重新整理頁面後再試一次。",
                429 => "短時間內嘗試次數過多，請稍候再試。",
                _ => "處理您的要求時發生問題，請稍後再試。"
            }
        };
        Response.StatusCode = code;
        return View("Error", vm);
    }
}
