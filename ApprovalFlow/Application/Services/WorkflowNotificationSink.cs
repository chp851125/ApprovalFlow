using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Workflow;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Services;

/// <summary>把流程事件轉為站內通知（Email 於通知階段另行加入）</summary>
public sealed class WorkflowNotificationSink(
    IRepository<Notification> notifications,
    IRepository<Employee> employees,
    IClock clock) : IWorkflowEventSink
{
    public async Task PublishAsync(IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default)
    {
        var names = (await employees.GetAllAsync(ct)).ToDictionary(e => e.Id, e => e.Name);
        foreach (var e in events)
        {
            var (title, message) = Compose(e, names);
            await notifications.AddAsync(new Notification
            {
                RecipientId = e.RecipientId,
                Type = e.Type,
                Title = title,
                Message = message,
                DocumentId = e.Document.Id,
                Link = $"/Documents/Details/{e.Document.Id}",
                CreatedAt = clock.Now
            }, ct);
        }
    }

    public static (string Title, string Message) Compose(WorkflowEvent e, IReadOnlyDictionary<int, string> names)
    {
        var doc = e.Document;
        var typeName = doc.DocumentType.ToDisplay();
        var applicant = names.GetValueOrDefault(doc.ApplicantId) ?? "";
        var actor = e.ActorId is int a ? names.GetValueOrDefault(a) ?? "" : "系統";
        var comment = string.IsNullOrEmpty(e.Comment) ? "" : $"意見：{e.Comment}";
        return e.Type switch
        {
            NotificationType.TaskAssigned when e.PrincipalId is int p =>
                ($"代理待簽核：{doc.DocumentNo}", $"{applicant} 的{typeName}「{doc.Title}」已送達「{e.StepName}」，您是 {names.GetValueOrDefault(p)} 的代理人，可代為簽核。"),
            NotificationType.TaskAssigned =>
                ($"待簽核：{doc.DocumentNo}", $"{applicant} 的{typeName}「{doc.Title}」已送達「{e.StepName}」，請您簽核。"),
            NotificationType.Approved => ($"已核准：{doc.DocumentNo}", $"您的{typeName}「{doc.Title}」已完成簽核並核准。"),
            NotificationType.Rejected => ($"已駁回：{doc.DocumentNo}", $"您的{typeName}「{doc.Title}」已被 {actor} 駁回。{comment}"),
            NotificationType.Returned => ($"已退回：{doc.DocumentNo}", $"您的{typeName}「{doc.Title}」已被 {actor} 退回。{comment}"),
            NotificationType.Withdrawn => ($"已撤回：{doc.DocumentNo}", $"{applicant} 已撤回{typeName}「{doc.Title}」，您無需再簽核。"),
            NotificationType.Cc => ($"抄送：{doc.DocumentNo}", $"{applicant} 的{typeName}「{doc.Title}」已核准，抄送給您知悉。"),
            NotificationType.System => ($"新留言：{doc.DocumentNo}", $"{actor} 在您的{typeName}「{doc.Title}」留言：{e.Comment}"),
            NotificationType.Reminder => ($"簽核提醒：{doc.DocumentNo}", $"{applicant} 的{typeName}「{doc.Title}」在「{e.StepName}」{e.Comment}，請儘速處理。"),
            NotificationType.Escalated => ($"逾時升級：{doc.DocumentNo}", $"{applicant} 的{typeName}「{doc.Title}」在「{e.StepName}」{e.Comment}。"),
            _ => (doc.DocumentNo, doc.Title)
        };
    }
}
