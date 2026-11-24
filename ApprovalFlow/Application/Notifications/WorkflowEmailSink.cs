using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Application.Workflow;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Notifications;

/// <summary>流程事件 → Email（與站內通知使用相同文字）</summary>
public sealed class WorkflowEmailSink(EmailService email, IRepository<Employee> employees) : IWorkflowEventSink
{
    /// <summary>收件者是否需要動作（升級通知只有新簽核人需要簽核）</summary>
    private static bool IsActionable(WorkflowEvent e) =>
        e.Type is NotificationType.TaskAssigned or NotificationType.Reminder ||
        (e.Type == NotificationType.Escalated && e.Document.GetCurrentStep()?.Assignees.Any(a => a.EmployeeId == e.RecipientId && a.Decision == AssigneeDecision.Pending) == true);

    public async Task PublishAsync(IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default)
    {
        if (events.Count == 0 || !await email.IsEnabledAsync()) return;
        var all = (await employees.GetAllAsync(ct)).ToDictionary(e => e.Id);
        var names = all.ToDictionary(kv => kv.Key, kv => kv.Value.Name);

        foreach (var e in events)
        {
            if (!all.TryGetValue(e.RecipientId, out var recipient)) continue;
            var (title, message) = WorkflowNotificationSink.Compose(e, names);
            var d = e.Document;
            var fields = new List<EmailField>
            {
                new("單號", string.IsNullOrEmpty(d.DocumentNo) ? "—" : d.DocumentNo),
                new("單別", d.DocumentType.ToDisplay()),
                new("主旨", d.Title),
                new("申請人", names.GetValueOrDefault(d.ApplicantId) ?? "")
            };
            if (d.Amount is decimal amt) fields.Add(new("金額", $"NT$ {amt:N0}"));
            if (d.Hours is decimal hrs) fields.Add(new("時數", $"{hrs:0.#} 小時"));
            if (!string.IsNullOrEmpty(e.StepName)) fields.Add(new("關卡", e.StepName));
            if (d.GetCurrentStep()?.DueAt is { } due && e.Type is NotificationType.TaskAssigned or NotificationType.Reminder or NotificationType.Escalated)
                fields.Add(new("簽核期限", due.ToString("yyyy/MM/dd HH:mm")));
            // 提醒、升級、留言的說明已在內文中，不重複列出
            if (!string.IsNullOrEmpty(e.Comment) && e.Type is not (NotificationType.Reminder or NotificationType.Escalated or NotificationType.System))
                fields.Add(new("意見", e.Comment));

            await email.SendAsync(recipient,
                new EmailContent(title, title, message, fields, $"/Documents/Details/{d.Id}",
                    IsActionable(e) ? "前往簽核" : "查看單據"),
                e.Type.ToString());
        }
    }
}
