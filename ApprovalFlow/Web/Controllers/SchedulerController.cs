using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;
using ApprovalFlow.Web.Infrastructure;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApprovalFlow.Web.Controllers;

/// <summary>排程狀態與 Email 寄送紀錄（系統管理員）</summary>
[Authorize(Policy = Policies.Admin)]
public class SchedulerController(
    BackgroundJobStatus status,
    BackgroundJobRunner runner,
    SettingsService settings,
    AuditService audit,
    IRepository<EmailMessage> emails,
    IRepository<Employee> employees,
    IEmailOutbox outbox) : Controller
{
    private const int PageSize = 20;

    public async Task<IActionResult> Index() => View(new SchedulerIndexViewModel
    {
        Status = status,
        ReminderBeforeHours = await settings.GetIntAsync(SettingKeys.SlaReminderBeforeHours, 4),
        AutoEscalate = await settings.GetBoolAsync(SettingKeys.SlaAutoEscalate, true),
        EscalateAfterHours = await settings.GetIntAsync(SettingKeys.SlaEscalateAfterHours, 8),
        DefaultSlaHours = await settings.GetIntAsync(SettingKeys.SlaDefaultHours, 16),
        DigestEnabled = await settings.GetBoolAsync(SettingKeys.DigestEnabled, true),
        DigestHour = await settings.GetIntAsync(SettingKeys.DigestHour, 8),
        EmailEnabled = await settings.GetBoolAsync(SettingKeys.EmailEnabled, false),
        EmailTotal = (await emails.GetAllAsync()).Count,
        EmailFailed = (await emails.QueryAsync(e => e.Status == EmailStatus.Failed)).Count
    });

    /// <summary>立即執行（測試或補跑用）；每日摘要仍不會對同一人當天重複寄送</summary>
    [HttpPost]
    public async Task<IActionResult> RunNow(string job, CancellationToken ct)
    {
        var (sla, digest) = await runner.RunAsync(runSla: job is "sla" or "all", runDigest: job is "digest" or "all", forceDigest: true, ct);
        await audit.LogAsync("RunJob", "Scheduler", job, $"手動執行排程：{job}");
        var parts = new List<string>();
        if (sla is not null) parts.Add($"時效檢查：{sla}");
        if (digest is not null) parts.Add($"每日摘要：{digest}");
        if (parts.Count > 0) TempData.SetSuccess(string.Join("；", parts) + "。");
        else TempData.SetError("執行失敗：" + (status.LastError ?? "未知錯誤"));
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Emails(string? q, string? category, EmailStatus? emailStatus, int page = 1)
    {
        IEnumerable<EmailMessage> list = await emails.GetAllAsync();
        if (!string.IsNullOrEmpty(category)) list = list.Where(e => e.Category == category);
        if (emailStatus is { } s) list = list.Where(e => e.Status == s);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var k = q.Trim();
            list = list.Where(e => e.To.Contains(k, StringComparison.OrdinalIgnoreCase) || e.Subject.Contains(k, StringComparison.OrdinalIgnoreCase));
        }
        var all = await emails.GetAllAsync();
        return View(new EmailLogViewModel
        {
            Q = q, Category = category, Status = emailStatus,
            Categories = all.Select(e => e.Category).Distinct().Order().ToList(),
            Names = (await employees.GetAllAsync()).ToDictionary(e => e.Id, e => e.Name),
            Rows = new PagedList<EmailMessage>(list.OrderByDescending(e => e.CreatedAt).ThenByDescending(e => e.Id).ToList(), page, PageSize)
        });
    }

    public async Task<IActionResult> Email(int id)
    {
        var e = await emails.GetByIdAsync(id) ?? throw new NotFoundException("Email", id);
        ViewData["RecipientName"] = e.RecipientId is int r ? (await employees.GetByIdAsync(r))?.Name : null;
        return View(e);
    }

    /// <summary>下載 .eml（可用 Outlook / Thunderbird 開啟檢視 HTML 版面）</summary>
    public async Task<IActionResult> Eml(int id)
    {
        var e = await emails.GetByIdAsync(id) ?? throw new NotFoundException("Email", id);
        var stream = e.FileName is null ? null : outbox.OpenRead(e.FileName);
        if (stream is null) throw new BusinessException("找不到 .eml 檔案（可能已被清除）。");
        return File(stream, "message/rfc822", $"email-{e.Id}.eml");
    }
}
