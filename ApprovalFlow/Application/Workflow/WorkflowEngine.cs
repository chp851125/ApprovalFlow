using System.Text.Json;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Common;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace ApprovalFlow.Application.Workflow;

/// <summary>
/// 簽核流程引擎（獨立服務，不依賴 Web 層）。
/// <list type="bullet">
/// <item>送出 / 重新送出：依範本展開關卡（條件分支、簽核人解析、同人略過），並保存內容版本。</item>
/// <item>核准（單簽 / 會簽 / 或簽）、駁回、退回上一關、退回申請人、撤回、加簽、轉簽、意見。</item>
/// <item>每個動作都先檢查狀態機與身分（含代理），並以 Version 做樂觀並行控制。</item>
/// <item>單據記錄使用的範本 Id；範本改版不影響已送出的單據，重新送出也沿用原範本版本。</item>
/// </list>
/// </summary>
public sealed class WorkflowEngine(
    IRepository<Document> documents,
    IRepository<WorkflowTemplate> templates,
    IRepository<Employee> employees,
    IRepository<Department> departments,
    IRepository<Delegation> delegations,
    IRepository<Holiday> holidays,
    DocumentNumberService numbers,
    SettingsService settings,
    AuditService audit,
    IEnumerable<IWorkflowEventSink> sinks,
    ICurrentUser currentUser,
    IClock clock,
    ILogger<WorkflowEngine> logger)
{
    /// <summary>單據最多可加簽的關卡數</summary>
    public const int MaxAddedSteps = 5;

    private sealed class Ctx(Document doc, int userId, DateTimeOffset now, WorkCalendar calendar, int defaultSla)
    {
        public Document Doc { get; } = doc;
        public int UserId { get; } = userId;
        public DateTimeOffset Now { get; } = now;
        public WorkCalendar Calendar { get; } = calendar;
        public int DefaultSla { get; } = defaultSla;
        public List<WorkflowEvent> Events { get; } = [];
        public List<(int PrincipalId, int DelegateId)> ActiveDelegations { get; set; } = [];
    }

    // =====================================================================
    // 公開動作
    // =====================================================================

    /// <summary>送出（草稿）或重新送出（已退回 / 已撤回）</summary>
    public Task<Document> SubmitAsync(int documentId, int version, string? comment = null) =>
        ExecuteAsync(documentId, version, null, async ctx =>
        {
            var doc = ctx.Doc;
            var action = WorkflowStateMachine.SubmitActionFor(doc.Status);
            WorkflowStateMachine.Ensure(doc.Status, action);
            if (doc.ApplicantId != ctx.UserId) throw new ForbiddenException("只有申請人可以送出此單據。");
            if (string.IsNullOrWhiteSpace(doc.Title)) throw new ValidationException("請填寫主旨後再送出。");

            var applicant = await employees.GetByIdAsync(doc.ApplicantId) ?? throw new NotFoundException("員工", doc.ApplicantId);
            if (!applicant.IsActive) throw new BusinessException("申請人帳號已停用，無法送出。");

            var template = await ResolveTemplateAsync(doc);
            var org = new OrgSnapshot(await employees.GetAllAsync(), await departments.GetAllAsync());
            var from = doc.Status;

            doc.TemplateId = template.Id;
            doc.TemplateVersion = template.TemplateVersion;
            doc.Steps = StepBuilder.Build(template, applicant, doc.Amount, doc.Hours, org, ctx.DefaultSla);
            if (string.IsNullOrEmpty(doc.DocumentNo))
                doc.DocumentNo = await numbers.NextAsync(doc.DocumentType, ctx.Now);

            doc.RevisionNo++;
            doc.Revisions.Add(new DocumentRevision
            {
                RevisionNo = doc.RevisionNo, SubmittedAt = ctx.Now, Title = doc.Title,
                Amount = doc.Amount, Hours = doc.Hours, Content = Clone(doc.Content)
            });
            doc.SubmittedAt ??= ctx.Now;
            doc.CompletedAt = null;
            doc.CurrentStepNo = null;
            doc.Status = DocumentStatus.InProgress;
            Record(ctx, action, null, Clean(comment), from: from, to: DocumentStatus.InProgress);
            ActivateNext(ctx, 0);
            return action;
        });

    /// <summary>核准目前關卡（會簽需全部同意才進下一關；或簽 / 單簽一人同意即可）</summary>
    public Task<Document> ApproveAsync(int documentId, int version, string? comment, int? asPrincipalId = null) =>
        ExecuteAsync(documentId, version, ApprovalActionType.Approve, async ctx =>
        {
            var (step, assignee, onBehalf) = await RequireActingAsync(ctx, asPrincipalId);
            Decide(ctx, assignee, AssigneeDecision.Approved, Clean(comment));
            Record(ctx, ApprovalActionType.Approve, step, Clean(comment), onBehalf);

            if (step.Mode == ApprovalMode.All && step.Assignees.Any(a => a.Decision == AssigneeDecision.Pending))
                return ApprovalActionType.Approve; // 會簽：等待其他人

            foreach (var other in step.Assignees.Where(a => a.Decision == AssigneeDecision.Pending))
                other.Decision = AssigneeDecision.NotRequired;
            step.Status = StepStatus.Approved;
            step.CompletedAt = ctx.Now;
            ActivateNext(ctx, step.StepNo);
            return ApprovalActionType.Approve;
        });

    public Task<Document> RejectAsync(int documentId, int version, string? comment, int? asPrincipalId = null) =>
        ExecuteAsync(documentId, version, ApprovalActionType.Reject, async ctx =>
        {
            var text = RequireComment(comment, "駁回");
            var (step, assignee, onBehalf) = await RequireActingAsync(ctx, asPrincipalId);
            Decide(ctx, assignee, AssigneeDecision.Rejected, text);
            step.Status = StepStatus.Rejected;
            step.CompletedAt = ctx.Now;
            var from = ctx.Doc.Status;
            CloseRemaining(ctx.Doc);
            ctx.Doc.Status = DocumentStatus.Rejected;
            ctx.Doc.CompletedAt = ctx.Now;
            Record(ctx, ApprovalActionType.Reject, step, text, onBehalf, from: from, to: DocumentStatus.Rejected);
            Notify(ctx, NotificationType.Rejected, ctx.Doc.ApplicantId, step.Name, text);
            return ApprovalActionType.Reject;
        });

    public Task<Document> ReturnToApplicantAsync(int documentId, int version, string? comment, int? asPrincipalId = null) =>
        ExecuteAsync(documentId, version, ApprovalActionType.ReturnToApplicant, async ctx =>
        {
            var text = RequireComment(comment, "退回");
            var (step, assignee, onBehalf) = await RequireActingAsync(ctx, asPrincipalId);
            Decide(ctx, assignee, AssigneeDecision.Returned, text);
            step.Status = StepStatus.Returned;
            step.CompletedAt = ctx.Now;
            var from = ctx.Doc.Status;
            CloseRemaining(ctx.Doc);
            ctx.Doc.Status = DocumentStatus.Returned;
            Record(ctx, ApprovalActionType.ReturnToApplicant, step, text, onBehalf, from: from, to: DocumentStatus.Returned);
            Notify(ctx, NotificationType.Returned, ctx.Doc.ApplicantId, step.Name, text);
            return ApprovalActionType.ReturnToApplicant;
        });

    /// <summary>退回上一個已核准的關卡重新簽核；目前關卡重置為未開始</summary>
    public Task<Document> ReturnToPreviousAsync(int documentId, int version, string? comment, int? asPrincipalId = null) =>
        ExecuteAsync(documentId, version, ApprovalActionType.ReturnToPrevious, async ctx =>
        {
            var text = RequireComment(comment, "退回");
            var (step, assignee, onBehalf) = await RequireActingAsync(ctx, asPrincipalId);
            var prev = PreviousApprovedStep(ctx.Doc, step)
                       ?? throw new BusinessException("目前已是第一個簽核關卡，沒有上一關可退回；如需修改請改用「退回申請人」。");

            Decide(ctx, assignee, AssigneeDecision.Returned, text);
            Record(ctx, ApprovalActionType.ReturnToPrevious, step, text, onBehalf);

            step.Status = StepStatus.Pending;
            step.ActivatedAt = step.DueAt = step.CompletedAt = step.RemindedAt = null;
            foreach (var a in step.Assignees) ResetAssignee(a);

            foreach (var a in prev.Assignees.Where(a => a.Decision is AssigneeDecision.Approved or AssigneeDecision.NotRequired))
                ResetAssignee(a);
            prev.CompletedAt = null;
            Activate(ctx, prev);
            Notify(ctx, NotificationType.Returned, ctx.Doc.ApplicantId, step.Name, $"已退回「{prev.Name}」重新簽核：{text}");
            return ApprovalActionType.ReturnToPrevious;
        });

    /// <summary>申請人撤回（關卡設定允許時）</summary>
    public Task<Document> WithdrawAsync(int documentId, int version, string? comment) =>
        ExecuteAsync(documentId, version, ApprovalActionType.Withdraw, async ctx =>
        {
            var doc = ctx.Doc;
            if (doc.ApplicantId != ctx.UserId) throw new ForbiddenException("只有申請人可以撤回此單據。");
            var step = doc.GetCurrentStep() ?? throw new BusinessException("此單據沒有進行中的關卡。");
            var blocked = await WithdrawBlockedReasonAsync(doc, step);
            if (blocked is not null) throw new BusinessException(blocked);

            var pending = step.Assignees.Where(a => a.Decision == AssigneeDecision.Pending).Select(a => a.EmployeeId).ToList();
            var from = doc.Status;
            CloseRemaining(doc);
            doc.Status = DocumentStatus.Withdrawn;
            doc.CompletedAt = ctx.Now;
            Record(ctx, ApprovalActionType.Withdraw, step, Clean(comment), from: from, to: DocumentStatus.Withdrawn);
            foreach (var id in pending) Notify(ctx, NotificationType.Withdrawn, id, step.Name, Clean(comment));
            return ApprovalActionType.Withdraw;
        });

    /// <summary>加簽：在目前關卡之後插入一個由指定人員簽核的關卡（目前簽核人仍需完成本關）</summary>
    public Task<Document> AddSignerAsync(int documentId, int version, int targetId, string? comment, int? asPrincipalId = null) =>
        ExecuteAsync(documentId, version, ApprovalActionType.AddSigner, async ctx =>
        {
            var text = RequireComment(comment, "加簽");
            var (step, assignee, onBehalf) = await RequireActingAsync(ctx, asPrincipalId);
            var target = await ValidateTargetAsync(ctx, step, targetId);
            if (ctx.Doc.Steps.Count(s => s.IsAddedStep) >= MaxAddedSteps)
                throw new BusinessException($"每張單據最多加簽 {MaxAddedSteps} 次。");
            var next = ctx.Doc.Steps.Where(s => s.StepNo > step.StepNo && s.Status == StepStatus.Pending).OrderBy(s => s.StepNo).FirstOrDefault();
            if (next is { IsAddedStep: true } && next.Assignees.Any(a => a.EmployeeId == target.Id))
                throw new BusinessException($"已將「{target.Name}」加簽在下一關。");

            var added = new DocumentStep
            {
                Name = $"加簽：{target.Name}",
                Mode = ApprovalMode.Single,
                SlaHours = ctx.DefaultSla,
                IsAddedStep = true,
                Assignees = [new StepAssignee { EmployeeId = target.Id, Source = AssigneeSource.AddedSigner, AssignedById = ctx.UserId }]
            };
            ctx.Doc.Steps.Insert(ctx.Doc.Steps.IndexOf(step) + 1, added);
            StepBuilder.Renumber(ctx.Doc.Steps);
            Record(ctx, ApprovalActionType.AddSigner, step, text, onBehalf, target: target.Id);
            return ApprovalActionType.AddSigner;
        });

    /// <summary>轉簽：把自己在本關的簽核責任交給另一人</summary>
    public Task<Document> TransferAsync(int documentId, int version, int targetId, string? comment, int? asPrincipalId = null) =>
        ExecuteAsync(documentId, version, ApprovalActionType.Transfer, async ctx =>
        {
            var text = RequireComment(comment, "轉簽");
            var (step, assignee, onBehalf) = await RequireActingAsync(ctx, asPrincipalId);
            var target = await ValidateTargetAsync(ctx, step, targetId);
            Decide(ctx, assignee, AssigneeDecision.Transferred, text);
            step.Assignees.Add(new StepAssignee { EmployeeId = target.Id, Source = AssigneeSource.Transferred, AssignedById = ctx.UserId });
            Record(ctx, ApprovalActionType.Transfer, step, text, onBehalf, target: target.Id);
            NotifyAssigned(ctx, step, [target.Id]);
            return ApprovalActionType.Transfer;
        });

    /// <summary>留言（不影響流程）</summary>
    public Task<Document> CommentAsync(int documentId, int version, string? comment) =>
        ExecuteAsync(documentId, version, ApprovalActionType.Comment, async ctx =>
        {
            var text = RequireComment(comment, "留言", 500);
            if (!(await GetPermissionsAsync(ctx.Doc, ctx.UserId)).CanComment) throw new ForbiddenException();
            Record(ctx, ApprovalActionType.Comment, ctx.Doc.GetCurrentStep(), text);
            if (ctx.Doc.ApplicantId != ctx.UserId)
                Notify(ctx, NotificationType.System, ctx.Doc.ApplicantId, null, text);
            return ApprovalActionType.Comment;
        });

    // =====================================================================
    // 權限查詢（給 UI 決定要顯示哪些按鈕；實際動作仍會再檢查一次）
    // =====================================================================

    public async Task<WorkflowPermissions> GetPermissionsAsync(Document doc, int userId)
    {
        var canView = await CanViewAsync(doc, userId);
        if (!canView) return WorkflowPermissions.None;

        var isApplicant = doc.ApplicantId == userId;
        var step = doc.GetCurrentStep();
        var acting = step is null ? null : await ResolveActingAsync(doc, step, userId, null);
        var canAct = acting is not null && doc.Status.IsActiveFlow();
        string? withdrawBlocked = null;
        if (isApplicant && doc.Status.IsActiveFlow() && step is not null)
            withdrawBlocked = await WithdrawBlockedReasonAsync(doc, step);

        return new WorkflowPermissions
        {
            CanView = true,
            CanSubmit = isApplicant && WorkflowStateMachine.CanPerform(doc.Status, WorkflowStateMachine.SubmitActionFor(doc.Status)),
            CanApprove = canAct,
            CanReject = canAct,
            CanReturnToApplicant = canAct,
            CanReturnToPrevious = canAct && step is not null && PreviousApprovedStep(doc, step) is not null,
            CanAddSigner = canAct && doc.Steps.Count(s => s.IsAddedStep) < MaxAddedSteps,
            CanTransfer = canAct,
            CanWithdraw = isApplicant && doc.Status.IsActiveFlow() && step is not null && withdrawBlocked is null,
            WithdrawBlockedReason = withdrawBlocked,
            CanComment = doc.Status != DocumentStatus.Draft,
            OnBehalfOfId = acting?.OnBehalfOfId
        };
    }

    /// <summary>
    /// 可檢視者：申請人、曾經或目前的簽核人與其代理人、抄送對象、申請人的上級主管、
    /// 系統管理員；人資可看請假 / 加班，財務可看費用 / 採購。
    /// </summary>
    public async Task<bool> CanViewAsync(Document doc, int userId)
    {
        if (doc.ApplicantId == userId || currentUser.IsInRole(Roles.Admin)) return true;
        if (doc.Status == DocumentStatus.Draft) return false;
        if (doc.CcEmployeeIds.Contains(userId)) return true;
        if (doc.Steps.Any(s => s.Assignees.Any(a => a.EmployeeId == userId || a.ActedById == userId))) return true;
        if (doc.History.Any(h => h.ActorId == userId || h.TargetEmployeeId == userId)) return true;
        if (currentUser.IsInRole(Roles.HR) && doc.DocumentType is DocumentType.Leave or DocumentType.Overtime) return true;
        if (currentUser.IsInRole(Roles.Finance) && doc.DocumentType is DocumentType.Expense or DocumentType.Purchase) return true;

        var step = doc.GetCurrentStep();
        if (step is not null && doc.Status.IsActiveFlow() && await ResolveActingAsync(doc, step, userId, null) is not null) return true;

        // 申請人的上級主管鏈
        var all = (await employees.GetAllAsync()).ToDictionary(e => e.Id);
        var cursor = all.GetValueOrDefault(doc.ApplicantId);
        for (var guard = 0; cursor?.ManagerId is int m && guard < 20; guard++)
        {
            if (m == userId) return true;
            cursor = all.GetValueOrDefault(m);
        }
        return false;
    }

    /// <summary>目前單別啟用中的範本（新送出的單據使用）</summary>
    public async Task<WorkflowTemplate?> GetActiveTemplateAsync(DocumentType type) =>
        (await templates.QueryAsync(t => t.DocumentType == type && t.IsActive))
        .OrderByDescending(t => t.TemplateVersion).FirstOrDefault();

    // =====================================================================
    // 執行框架
    // =====================================================================

    /// <param name="expected">預期動作，用於事前狀態檢查；null 表示由 body 自行檢查（送出）</param>
    private async Task<Document> ExecuteAsync(int documentId, int version, ApprovalActionType? expected,
        Func<Ctx, Task<ApprovalActionType>> body)
    {
        var userId = currentUser.Id ?? throw new ForbiddenException();
        var doc = await documents.GetByIdAsync(documentId) ?? throw new NotFoundException("單據", documentId);
        if (doc.Version != version) throw new ConcurrencyException(nameof(Document), documentId);
        if (expected is { } a) WorkflowStateMachine.Ensure(doc.Status, a);

        var ctx = new Ctx(doc, userId, clock.Now,
            new WorkCalendar(await holidays.GetAllAsync()),
            await settings.GetIntAsync(SettingKeys.SlaDefaultHours, 16));
        var today = clock.Today;
        ctx.ActiveDelegations = (await delegations.GetAllAsync())
            .Where(d => d.IsEffectiveOn(today, doc.DocumentType))
            .Select(d => (d.PrincipalId, d.DelegateId)).ToList();

        var action = await body(ctx);
        doc.UpdatedAt = ctx.Now;
        await documents.UpdateAsync(doc); // Version 不符時丟出 ConcurrencyException，變更不會寫入

        await audit.LogAsync($"Workflow.{action}", nameof(Document), doc.Id.ToString(),
            $"{doc.DocumentNo}「{doc.Title}」{action.ToDisplay()}，狀態：{doc.Status.ToDisplay()}");

        if (ctx.Events.Count > 0)
        {
            foreach (var sink in sinks)
            {
                try { await sink.PublishAsync(ctx.Events); }
                catch (Exception ex) { logger.LogError(ex, "流程事件發送失敗（單據 {DocumentId}）", doc.Id); }
            }
        }
        return doc;
    }

    private async Task<WorkflowTemplate> ResolveTemplateAsync(Document doc)
    {
        if (doc.TemplateId is int tid && await templates.GetByIdAsync(tid) is { } used) return used;
        return await GetActiveTemplateAsync(doc.DocumentType)
               ?? throw new BusinessException($"「{doc.DocumentType.ToDisplay()}」尚未設定啟用中的簽核流程，請洽系統管理員。");
    }

    // =====================================================================
    // 身分與代理
    // =====================================================================

    private sealed record Acting(StepAssignee Assignee, int? OnBehalfOfId);

    /// <summary>
    /// 決定使用者以哪位簽核人的身分處理目前關卡：本人優先；否則找今天有效、適用此單別的代理設定。
    /// 申請人不可透過代理簽核自己的單據。
    /// </summary>
    private async Task<Acting?> ResolveActingAsync(Document doc, DocumentStep step, int userId, int? asPrincipalId,
        IReadOnlyList<(int PrincipalId, int DelegateId)>? activeDelegations = null)
    {
        if (!doc.Status.IsActiveFlow() || step.Status != StepStatus.Active) return null;
        var pending = step.Assignees.Where(a => a.Decision == AssigneeDecision.Pending).ToList();

        if (asPrincipalId is null || asPrincipalId == userId)
        {
            var own = pending.FirstOrDefault(a => a.EmployeeId == userId);
            if (own is not null) return new Acting(own, null);
        }
        if (doc.ApplicantId == userId) return null;

        var principals = activeDelegations is not null
            ? activeDelegations.Where(d => d.DelegateId == userId).Select(d => d.PrincipalId).ToHashSet()
            : (await delegations.QueryAsync(d => d.DelegateId == userId))
                .Where(d => d.IsEffectiveOn(clock.Today, doc.DocumentType)).Select(d => d.PrincipalId).ToHashSet();
        var match = pending.FirstOrDefault(a => principals.Contains(a.EmployeeId) && (asPrincipalId is null || a.EmployeeId == asPrincipalId));
        return match is null ? null : new Acting(match, match.EmployeeId);
    }

    private async Task<(DocumentStep Step, StepAssignee Assignee, int? OnBehalfOf)> RequireActingAsync(Ctx ctx, int? asPrincipalId)
    {
        var step = ctx.Doc.GetCurrentStep() ?? throw new BusinessException("此單據沒有進行中的關卡。");
        var acting = await ResolveActingAsync(ctx.Doc, step, ctx.UserId, asPrincipalId, ctx.ActiveDelegations)
                     ?? throw new ForbiddenException("您不是此關卡的簽核人（或代理人），無法執行此操作。");
        return (step, acting.Assignee, acting.OnBehalfOfId);
    }

    private async Task<Employee> ValidateTargetAsync(Ctx ctx, DocumentStep step, int targetId)
    {
        var target = await employees.GetByIdAsync(targetId) ?? throw new ValidationException("請選擇人員。", "TargetId");
        if (!target.IsActive) throw new ValidationException($"「{target.Name}」已停用。", "TargetId");
        if (target.Id == ctx.Doc.ApplicantId) throw new ValidationException("不可指定申請人本人。", "TargetId");
        if (target.Id == ctx.UserId) throw new ValidationException("不可指定自己。", "TargetId");
        if (step.Assignees.Any(a => a.EmployeeId == target.Id && a.Decision == AssigneeDecision.Pending))
            throw new ValidationException($"「{target.Name}」已是本關卡的簽核人。", "TargetId");
        return target;
    }

    private async Task<string?> WithdrawBlockedReasonAsync(Document doc, DocumentStep step)
    {
        if (step.IsAddedStep || step.TemplateStepNo is null || doc.TemplateId is null) return null;
        var tpl = await templates.GetByIdAsync(doc.TemplateId.Value);
        var def = tpl?.Steps.FirstOrDefault(s => s.StepNo == step.TemplateStepNo);
        return def is { AllowWithdraw: false } ? $"流程已進入「{step.Name}」，此關卡不允許申請人撤回。" : null;
    }

    // =====================================================================
    // 關卡運算
    // =====================================================================

    private void ActivateNext(Ctx ctx, int afterStepNo)
    {
        var doc = ctx.Doc;
        var next = doc.Steps.Where(s => s.StepNo > afterStepNo && s.Status == StepStatus.Pending)
            .OrderBy(s => s.StepNo).FirstOrDefault();
        if (next is null)
        {
            var from = doc.Status;
            doc.Status = DocumentStatus.Approved;
            doc.CompletedAt = ctx.Now;
            doc.CurrentStepNo = null;
            var last = doc.History[^1];
            last.FromStatus ??= from;
            last.ToStatus = DocumentStatus.Approved;
            Notify(ctx, NotificationType.Approved, doc.ApplicantId, null, null);
            foreach (var cc in doc.CcEmployeeIds.Distinct()) Notify(ctx, NotificationType.Cc, cc, null, null);
            return;
        }
        Activate(ctx, next);
    }

    private void Activate(Ctx ctx, DocumentStep step)
    {
        var doc = ctx.Doc;
        step.Status = StepStatus.Active;
        step.ActivatedAt = ctx.Now;
        step.DueAt = Truncate(ctx.Calendar.AddWorkingHours(ctx.Now, step.SlaHours ?? ctx.DefaultSla));
        step.RemindedAt = null;
        step.CompletedAt = null;
        doc.CurrentStepNo = step.StepNo;
        if (doc.Status == DocumentStatus.Overdue)
        {
            doc.Status = DocumentStatus.InProgress; // 逾時關卡已處理，新關卡重新計時
            var last = doc.History[^1];
            last.FromStatus ??= DocumentStatus.Overdue;
            last.ToStatus = DocumentStatus.InProgress;
        }
        NotifyAssigned(ctx, step, step.Assignees.Where(a => a.Decision == AssigneeDecision.Pending).Select(a => a.EmployeeId));
    }

    private static DocumentStep? PreviousApprovedStep(Document doc, DocumentStep current) =>
        doc.Steps.Where(s => s.StepNo < current.StepNo && s.Status == StepStatus.Approved)
            .OrderByDescending(s => s.StepNo).FirstOrDefault();

    private static void Decide(Ctx ctx, StepAssignee a, AssigneeDecision decision, string? comment)
    {
        a.Decision = decision;
        a.ActedById = ctx.UserId;
        a.DecidedAt = ctx.Now;
        a.Comment = comment;
    }

    private static void ResetAssignee(StepAssignee a)
    {
        a.Decision = AssigneeDecision.Pending;
        a.ActedById = null;
        a.DecidedAt = null;
        a.Comment = null;
    }

    private static void CloseRemaining(Document doc)
    {
        foreach (var s in doc.Steps)
        {
            if (s.Status is StepStatus.Pending or StepStatus.Active) s.Status = StepStatus.Cancelled;
            foreach (var a in s.Assignees.Where(a => a.Decision == AssigneeDecision.Pending))
                a.Decision = AssigneeDecision.Cancelled;
        }
        doc.CurrentStepNo = null;
    }

    private static void Record(Ctx ctx, ApprovalActionType action, DocumentStep? step, string? comment,
        int? onBehalf = null, int? target = null, DocumentStatus? from = null, DocumentStatus? to = null)
    {
        var doc = ctx.Doc;
        doc.History.Add(new ApprovalRecord
        {
            Seq = doc.History.Count == 0 ? 1 : doc.History.Max(h => h.Seq) + 1,
            At = ctx.Now,
            Action = action,
            ActorId = ctx.UserId,
            OnBehalfOfId = onBehalf,
            StepNo = step?.StepNo,
            StepName = step?.Name,
            TargetEmployeeId = target,
            Comment = comment,
            FromStatus = from,
            ToStatus = to,
            RevisionNo = doc.RevisionNo
        });
    }

    // =====================================================================
    // 事件
    // =====================================================================

    private static void Notify(Ctx ctx, NotificationType type, int recipientId, string? stepName, string? comment)
    {
        if (recipientId == ctx.UserId) return; // 不通知自己
        ctx.Events.Add(new WorkflowEvent(type, recipientId, ctx.Doc, ctx.UserId, stepName, comment));
    }

    /// <summary>通知新的簽核人；若簽核人今天有代理人，代理人也會收到</summary>
    private static void NotifyAssigned(Ctx ctx, DocumentStep step, IEnumerable<int> assigneeIds)
    {
        foreach (var id in assigneeIds.Distinct())
        {
            Notify(ctx, NotificationType.TaskAssigned, id, step.Name, null);
            foreach (var (_, delegateId) in ctx.ActiveDelegations.Where(d => d.PrincipalId == id))
            {
                if (delegateId == ctx.UserId || delegateId == ctx.Doc.ApplicantId) continue;
                ctx.Events.Add(new WorkflowEvent(NotificationType.TaskAssigned, delegateId, ctx.Doc, ctx.UserId, step.Name, null, id));
            }
        }
    }

    // =====================================================================
    // 工具
    // =====================================================================

    private static string RequireComment(string? comment, string actionName, int max = 300)
    {
        var text = Clean(comment);
        if (text is null) throw new ValidationException($"{actionName}時請填寫意見。", "Comment");
        if (text.Length > max) throw new ValidationException($"意見最多 {max} 個字。", "Comment");
        return text;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static DateTimeOffset Truncate(DateTimeOffset t) => new(t.Year, t.Month, t.Day, t.Hour, t.Minute, 0, t.Offset);

    private static DocumentContent Clone(DocumentContent c) =>
        JsonSerializer.Deserialize<DocumentContent>(JsonSerializer.SerializeToUtf8Bytes(c))!;
}
