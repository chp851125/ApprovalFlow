using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Common;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Notifications;

public sealed record DigestRunResult(bool Ran, string Reason, int Sent, int AlreadySent)
{
    public override string ToString() => Ran ? $"寄出 {Sent} 份，今日已寄過 {AlreadySent} 份" : Reason;
}

/// <summary>
/// 每日待辦摘要：工作日的指定時刻後，寄給有待簽核單據的人（含代理中的單據）。
/// 以「今天是否已寄過」判斷，重複執行不會重複寄送。
/// </summary>
public sealed class DigestService(
    IRepository<Document> documents,
    IRepository<Employee> employees,
    IRepository<Delegation> delegations,
    IRepository<Notification> notifications,
    IRepository<Holiday> holidays,
    EmailService email,
    SettingsService settings,
    IClock clock)
{
    /// <param name="force">忽略寄送時刻與工作日限制（管理員手動執行）；仍不會重複寄送</param>
    public async Task<DigestRunResult> RunAsync(bool force = false, CancellationToken ct = default)
    {
        var now = clock.Now;
        var today = clock.Today;
        if (!await settings.GetBoolAsync(SettingKeys.DigestEnabled, true)) return new(false, "每日摘要未啟用", 0, 0);
        var hour = await settings.GetIntAsync(SettingKeys.DigestHour, 8);
        if (!force)
        {
            if (now.Hour < hour) return new(false, $"尚未到寄送時刻（{hour}:00）", 0, 0);
            if (!new WorkCalendar(await holidays.GetAllAsync(ct)).IsWorkday(today)) return new(false, "今天不是工作日", 0, 0);
        }

        var active = (await documents.QueryAsync(d => d.Status == DocumentStatus.InProgress || d.Status == DocumentStatus.Overdue, ct)).ToList();
        var effective = (await delegations.GetAllAsync(ct)).Where(d => d.IsEffectiveOn(today)).ToList();
        var sentToday = (await notifications.QueryAsync(n => n.Type == NotificationType.DailyDigest, ct))
            .Where(n => DateOnly.FromDateTime(n.CreatedAt.DateTime) == today).Select(n => n.RecipientId).ToHashSet();
        var names = (await employees.GetAllAsync(ct)).ToDictionary(e => e.Id, e => e.Name);

        int sent = 0, already = 0;
        foreach (var emp in await employees.QueryAsync(e => e.IsActive, ct))
        {
            var mine = active.Where(d => d.GetCurrentStep() is { Status: StepStatus.Active } s &&
                    s.Assignees.Any(a => a.Decision == AssigneeDecision.Pending &&
                        (a.EmployeeId == emp.Id || (d.ApplicantId != emp.Id && effective.Any(x => x.DelegateId == emp.Id && x.PrincipalId == a.EmployeeId &&
                            (x.DocumentTypes.Count == 0 || x.DocumentTypes.Contains(d.DocumentType)))))))
                .OrderByDescending(d => d.Status == DocumentStatus.Overdue).ThenBy(d => d.GetCurrentStep()!.DueAt).ToList();
            if (mine.Count == 0) continue;
            if (sentToday.Contains(emp.Id)) { already++; continue; }

            var overdueCount = mine.Count(d => d.Status == DocumentStatus.Overdue);
            var title = $"每日待辦摘要：{mine.Count} 張待簽核" + (overdueCount > 0 ? $"（{overdueCount} 張逾時）" : "");
            var items = mine.Take(15).Select(d =>
                $"{(d.Status == DocumentStatus.Overdue ? "【逾時】" : d.IsUrgent ? "【急件】" : "")}{d.DocumentNo} {d.Title}（{names.GetValueOrDefault(d.ApplicantId)}，期限 {d.GetCurrentStep()!.DueAt:MM/dd HH:mm}）").ToList();
            if (mine.Count > 15) items.Add($"……另有 {mine.Count - 15} 張");

            await notifications.AddAsync(new Notification
            {
                RecipientId = emp.Id,
                Type = NotificationType.DailyDigest,
                Title = title,
                Message = $"您目前有 {mine.Count} 張單據待簽核" + (overdueCount > 0 ? $"，其中 {overdueCount} 張已逾時" : "") + "。",
                Link = "/Documents?box=todo",
                CreatedAt = now
            }, ct);
            await email.SendAsync(emp, new EmailContent(title, $"{today:M 月 d 日} 待辦摘要",
                $"您目前有 {mine.Count} 張單據等待簽核" + (overdueCount > 0 ? $"，其中 {overdueCount} 張已超過簽核期限，請優先處理" : "") + "：",
                [], "/Documents?box=todo", "前往待辦清單") { ListItems = items }, "DailyDigest");
            sent++;
        }
        return new(true, "", sent, already);
    }
}
