using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Common;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace ApprovalFlow.Application.Workflow;

public sealed record SlaRunResult(int Checked, int Reminded, int MarkedOverdue, int Escalated, int Conflicts)
{
    public override string ToString() => $"檢查 {Checked} 張、提醒 {Reminded}、轉為逾時 {MarkedOverdue}、自動升級 {Escalated}" + (Conflicts > 0 ? $"、略過 {Conflicts}（同時有人操作）" : "");
}

/// <summary>
/// 關卡時效（SLA，以工作小時計）監控，由背景排程定期執行：
/// <list type="number">
/// <item>到期前 N 個工作小時：提醒簽核人（每個關卡只提醒一次）。</item>
/// <item>超過期限：單據狀態改為「逾時」並通知簽核人。</item>
/// <item>逾時超過 M 個工作小時仍未處理：自動升級給簽核人的上一層主管（每個關卡一次），並重新計算期限。</item>
/// </list>
/// 每張單據以 Version 保護；若剛好有人在簽核，跳過該單據，下次排程再處理。
/// </summary>
public sealed class SlaMonitorService(
    IRepository<Document> documents,
    IRepository<Employee> employees,
    IRepository<Department> departments,
    IRepository<Holiday> holidays,
    SettingsService settings,
    AuditService audit,
    IEnumerable<IWorkflowEventSink> sinks,
    IClock clock,
    ILogger<SlaMonitorService> logger)
{
    public async Task<SlaRunResult> RunAsync(CancellationToken ct = default)
    {
        var now = clock.Now;
        var reminderBefore = await settings.GetIntAsync(SettingKeys.SlaReminderBeforeHours, 4);
        var autoEscalate = await settings.GetBoolAsync(SettingKeys.SlaAutoEscalate, true);
        var escalateAfter = await settings.GetIntAsync(SettingKeys.SlaEscalateAfterHours, 8);
        var defaultSla = await settings.GetIntAsync(SettingKeys.SlaDefaultHours, 16);
        var calendar = new WorkCalendar(await holidays.GetAllAsync(ct));
        var org = new OrgSnapshot(await employees.GetAllAsync(ct), await departments.GetAllAsync(ct));

        int checkedCount = 0, reminded = 0, overdue = 0, escalated = 0, conflicts = 0;
        var candidates = await documents.QueryAsync(d => d.Status == DocumentStatus.InProgress || d.Status == DocumentStatus.Overdue, ct);

        foreach (var doc in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (doc.GetCurrentStep() is not { Status: StepStatus.Active, DueAt: { } due } step) continue;
            var pending = step.Assignees.Where(a => a.Decision == AssigneeDecision.Pending).ToList();
            if (pending.Count == 0) continue;
            checkedCount++;

            var events = new List<WorkflowEvent>();
            var actions = new List<string>();
            var sla = step.SlaHours ?? defaultSla;

            // 1. 到期前提醒
            if (step.RemindedAt is null && reminderBefore > 0 && now < due && calendar.WorkingHoursBetween(now, due) <= reminderBefore)
            {
                step.RemindedAt = now;
                Record(doc, ApprovalActionType.Remind, step, now, $"距簽核期限（{due:MM/dd HH:mm}）不到 {reminderBefore} 個工作小時，已提醒簽核人。");
                foreach (var a in pending)
                    events.Add(new WorkflowEvent(NotificationType.Reminder, a.EmployeeId, doc, null, step.Name, $"將於 {due:MM/dd HH:mm} 到期"));
                reminded++;
                actions.Add("提醒");
            }

            // 2. 超過期限 → 逾時
            if (doc.Status == DocumentStatus.InProgress && now >= due && WorkflowStateMachine.CanPerform(doc.Status, ApprovalActionType.Remind))
            {
                Record(doc, ApprovalActionType.Remind, step, now, $"已超過關卡時效（{sla} 個工作小時，期限 {due:MM/dd HH:mm}），狀態改為逾時。",
                    from: DocumentStatus.InProgress, to: DocumentStatus.Overdue);
                doc.Status = DocumentStatus.Overdue;
                foreach (var a in pending)
                    events.Add(new WorkflowEvent(NotificationType.Reminder, a.EmployeeId, doc, null, step.Name, $"已超過簽核期限（{due:MM/dd HH:mm}）"));
                overdue++;
                actions.Add("逾時");
            }

            // 3. 逾時太久 → 自動升級給上一層主管
            if (autoEscalate && doc.Status == DocumentStatus.Overdue && !step.IsEscalated && now >= due &&
                calendar.WorkingHoursBetween(due, now) >= escalateAfter &&
                WorkflowStateMachine.CanPerform(doc.Status, ApprovalActionType.Escalate))
            {
                var added = new List<Employee>();
                foreach (var a in pending)
                {
                    var mgr = org.ActiveManagerOf(a.EmployeeId);
                    if (mgr is null || mgr.Id == doc.ApplicantId || added.Any(x => x.Id == mgr.Id) ||
                        step.Assignees.Any(x => x.EmployeeId == mgr.Id && x.Decision == AssigneeDecision.Pending))
                        continue;
                    a.Decision = AssigneeDecision.Escalated;
                    a.DecidedAt = now;
                    a.Comment = "逾時未簽核，系統自動升級";
                    step.Assignees.Add(new StepAssignee { EmployeeId = mgr.Id, Source = AssigneeSource.Escalated });
                    added.Add(mgr);
                    events.Add(new WorkflowEvent(NotificationType.Escalated, mgr.Id, doc, null, step.Name,
                        $"原簽核人 {org.Employee(a.EmployeeId)?.Name} 逾時未處理，已升級由您簽核"));
                    events.Add(new WorkflowEvent(NotificationType.Escalated, a.EmployeeId, doc, null, step.Name,
                        $"逾時未處理，已升級由 {mgr.Name} 簽核"));
                }

                step.IsEscalated = true; // 不論是否找到主管都只處理一次，避免每次排程重複
                if (added.Count > 0)
                {
                    step.DueAt = Truncate(calendar.AddWorkingHours(now, sla));
                    step.RemindedAt = null;
                    Record(doc, ApprovalActionType.Escalate, step, now,
                        $"逾時超過 {escalateAfter} 個工作小時未簽核，系統自動升級至 {string.Join("、", added.Select(m => m.Name))}，新期限 {step.DueAt:MM/dd HH:mm}。",
                        target: added[0].Id);
                    events.Add(new WorkflowEvent(NotificationType.Escalated, doc.ApplicantId, doc, null, step.Name,
                        $"逾時未簽核，系統已自動升級至 {string.Join("、", added.Select(m => m.Name))}"));
                    escalated++;
                    actions.Add("升級");
                }
                else
                {
                    Record(doc, ApprovalActionType.Escalate, step, now, "逾時未簽核，但找不到可升級的上一層主管，請系統管理員協助轉簽。");
                    actions.Add("無法升級");
                }
            }

            if (actions.Count == 0) continue;
            try
            {
                await documents.UpdateAsync(doc, ct);
            }
            catch (ConcurrencyException)
            {
                conflicts++;
                continue; // 有人正在操作，下次排程再檢查
            }

            await audit.LogAsync($"Workflow.Sla", nameof(Document), doc.Id.ToString(),
                $"{doc.DocumentNo} 時效檢查：{string.Join("、", actions)}", userName: "系統排程");
            foreach (var sink in sinks)
            {
                try { await sink.PublishAsync(events, ct); }
                catch (Exception ex) { logger.LogError(ex, "時效通知發送失敗（單據 {DocumentId}）", doc.Id); }
            }
        }
        return new SlaRunResult(checkedCount, reminded, overdue, escalated, conflicts);
    }

    private static void Record(Document doc, ApprovalActionType action, DocumentStep step, DateTimeOffset at, string comment,
        int? target = null, DocumentStatus? from = null, DocumentStatus? to = null)
    {
        doc.History.Add(new ApprovalRecord
        {
            Seq = doc.History.Count == 0 ? 1 : doc.History.Max(h => h.Seq) + 1,
            At = at,
            Action = action,
            ActorId = null, // 系統動作
            StepNo = step.StepNo,
            StepName = step.Name,
            TargetEmployeeId = target,
            Comment = comment,
            FromStatus = from,
            ToStatus = to,
            RevisionNo = doc.RevisionNo
        });
    }

    private static DateTimeOffset Truncate(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Offset);
}
