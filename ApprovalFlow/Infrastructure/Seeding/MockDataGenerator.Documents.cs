using System.Text.Json;
using ApprovalFlow.Application.Workflow;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;
using ApprovalFlow.Infrastructure.Persistence;

namespace ApprovalFlow.Infrastructure.Seeding;

public sealed partial class MockDataGenerator
{
    private sealed record DocSpec(DocumentType Type, DocumentStatus Final, string? Special = null, string? Applicant = null, DateOnly? Date = null);

    private sealed record NoteSeed(Document Doc, int RecipientId, NotificationType Type, DateTimeOffset At, string? Extra = null);

    /// <summary>模擬時間超過「現在」時拋出，用來停止進行中單據或重新挑選日期</summary>
    private sealed class TooLateException : Exception;

    /// <summary>進行中單據的目前關卡已逾期：改用較近的日期重新產生</summary>
    private sealed class NotYetException : Exception;

    private readonly List<NoteSeed> _notes = [];

    private static readonly string[] ApproveComments = ["同意", "如擬", "可", "核准，請依規定辦理", "同意，請注意預算控管", "同意，辛苦了", "OK"];

    // ------------------------------------------------------------------ 主流程

    private void GenerateDocuments()
    {
        var specs = new List<DocSpec>
        {
            new(DocumentType.Leave, DocumentStatus.Approved, "delegation", "acc02", new DateOnly(2026, 3, 3)),
            new(DocumentType.Expense, DocumentStatus.Approved, "high-amount", "sales01", new DateOnly(2026, 7, 15)),
            new(DocumentType.Overtime, DocumentStatus.InProgress, "progress-2", "sales03"),
            new(DocumentType.Purchase, DocumentStatus.Approved, "addsigner", "sales02", new DateOnly(2026, 9, 8)),
            new(DocumentType.Expense, DocumentStatus.Approved, "transfer", "hr01", new DateOnly(2026, 8, 20)),
            new(DocumentType.Expense, DocumentStatus.Approved, "resubmit", "sales02", new DateOnly(2026, 10, 14)),
            new(DocumentType.Leave, DocumentStatus.Approved, "resubmit-sick", "sales06", new DateOnly(2026, 4, 21)),
            new(DocumentType.Purchase, DocumentStatus.Approved, "return-prev", "ga01", new DateOnly(2026, 5, 12)),
            new(DocumentType.Expense, DocumentStatus.Overdue, "escalated", "acc03"),
            new(DocumentType.Purchase, DocumentStatus.InProgress, "high-amount", "sales.lead1"),
        };

        var targets = new Dictionary<DocumentStatus, int>
        {
            [DocumentStatus.Draft] = 5,
            [DocumentStatus.InProgress] = 10,
            [DocumentStatus.Overdue] = 4,
            [DocumentStatus.Approved] = 28,
            [DocumentStatus.Rejected] = 5,
            [DocumentStatus.Returned] = 4,
            [DocumentStatus.Withdrawn] = 4,
        };
        foreach (var s in specs) targets[s.Final]--;
        foreach (var (status, count) in targets)
            for (var i = 0; i < count; i++)
                specs.Add(new DocSpec(Weighted(
                    (DocumentType.Leave, 35), (DocumentType.Expense, 30),
                    (DocumentType.Purchase, 15), (DocumentType.Overtime, 20)), status));

        foreach (var spec in specs)
        {
            var date = spec.Date ?? PickDate(spec.Final);
            Document? doc = null;
            for (var attempt = 0; attempt < 8 && doc is null; attempt++)
            {
                var noteMark = _notes.Count;
                try
                {
                    doc = BuildDocument(spec, date);
                }
                catch (TooLateException)
                {
                    _notes.RemoveRange(noteMark, _notes.Count - noteMark);
                    date = _cal.PreviousWorkday(date.AddDays(-7));
                }
                catch (NotYetException)
                {
                    _notes.RemoveRange(noteMark, _notes.Count - noteMark);
                    date = WorkdaysBack(1);
                }
            }
            if (doc is null) throw new InvalidOperationException($"無法產生單據：{spec}");
            _documents.Add(doc);
        }

        // 依建立時間指派 Id
        var id = 1;
        foreach (var d in _documents.OrderBy(d => d.CreatedAt).ToList())
        {
            d.Id = id++;
            d.Version = 1;
        }
        _documents.Sort((a, b) => a.Id.CompareTo(b.Id));

        AssignDocumentNumbers();
    }

    /// <summary>已結案單據的時間分層（讓 12 個月都有資料）</summary>
    private Queue<int>? _slots;
    private const int SlotCount = 30;

    private DateOnly PickCompletedDate()
    {
        _slots ??= new Queue<int>(Enumerable.Range(0, SlotCount).OrderBy(_ => _rng.Next()));
        if (_slots.Count == 0) return _cal.PreviousWorkday(_anchor.AddDays(-R(20, 355)));
        var slot = _slots.Dequeue();
        var offset = 20 + (int)((slot + _rng.NextDouble()) * (355 - 20) / SlotCount);
        return _cal.PreviousWorkday(_anchor.AddDays(-offset));
    }

    private DateOnly PickDate(DocumentStatus s) => s switch
    {
        DocumentStatus.Approved or DocumentStatus.Rejected or DocumentStatus.Withdrawn => PickCompletedDate(),
        DocumentStatus.Returned => _cal.PreviousWorkday(_anchor.AddDays(-R(6, 40))),
        DocumentStatus.Draft => _cal.PreviousWorkday(_anchor.AddDays(-R(1, 45))),
        DocumentStatus.InProgress => WorkdaysBack(R(1, 2)),
        DocumentStatus.Overdue => WorkdaysBack(R(5, 9)),
        _ => _anchor
    };

    private DateOnly WorkdaysBack(int n)
    {
        var d = _anchor;
        for (var i = 0; i < n; i++) d = _cal.PreviousWorkday(d.AddDays(-1));
        return d;
    }

    private Employee PickApplicant(DateOnly date) =>
        Weighted(_employees
            .Where(e => e.Account != "gm" && e.HireDate.AddDays(30) < date &&
                        (e.IsActive || e.ResignedDate > date.AddDays(10)))
            .Select(e => (e, e.DepartmentId == _deptByCode["SAL"].Id ? 3 : 2))
            .ToArray());

    private Document BuildDocument(DocSpec spec, DateOnly date)
    {
        var app = spec.Applicant is null ? PickApplicant(date) : E(spec.Applicant);

        DateTimeOffset submitAt = spec.Final switch
        {
            DocumentStatus.InProgress when date < _cal.PreviousWorkday(_anchor.AddDays(-1)) => At(date, R(13, 16), R(0, 59)),
            _ => At(date, R(9, 16), R(0, 59))
        };
        var createdAt = submitAt.AddMinutes(-R(5, 240));
        if (createdAt.Hour < 8) createdAt = At(date, 8, R(30, 59));

        var doc = new Document
        {
            DocumentType = spec.Type,
            ApplicantId = app.Id,
            DepartmentId = app.DepartmentId,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            IsUrgent = Chance(0.1)
        };
        FillContent(doc, app, date, spec);

        if (spec.Final == DocumentStatus.Draft)
        {
            doc.UpdatedAt = createdAt.AddMinutes(R(1, 50));
            return doc;
        }

        RunFlow(doc, app, spec, submitAt);
        doc.UpdatedAt = doc.History.Count > 0 ? doc.History.Max(h => h.At) : createdAt;
        if (doc.UpdatedAt > _now) throw new TooLateException();
        return doc;
    }

    // ------------------------------------------------------------------ 流程模擬

    private void RunFlow(Document doc, Employee app, DocSpec spec, DateTimeOffset submitAt)
    {
        var t = Submit(doc, app, submitAt, resubmit: false);
        var activeCount = doc.Steps.Count(s => s.Status != StepStatus.Skipped);

        switch (spec.Special)
        {
            case "addsigner":
                t = ApproveCurrent(doc, t);
                t = AddSigner(doc, t, "admin", "請資訊組確認規格與公司現有設備相容，並評估是否可沿用舊機周邊。");
                t = ApproveCurrent(doc, t, "同意採購，惟請待資訊組確認規格後再下單。");
                t = ApproveCurrent(doc, t, "規格確認無誤，與現有擴充座相容。");
                RunApprovals(doc, t);
                return;

            case "transfer":
                t = Transfer(doc, t, "adm.head", "本週外出參加勞動法規研習，轉請處長核示。");
                RunApprovals(doc, t);
                return;

            case "resubmit":
            {
                while (doc.GetCurrentStep()!.Name != "財務審核") t = ApproveCurrent(doc, t);
                t = ReturnToApplicant(doc, t, "第 2 筆計程車費用與收據金額不符，請更正後重新送出。");
                t = AddWH(t, RD(1.5, 8));
                var items = doc.Content.Expense!.Items;
                if (items.Count < 2) items.Add(NewExpenseItem(app, DateOnly.FromDateTime(submitAt.DateTime), "TRAFFIC"));
                items[1].Amount -= 120;
                items[1].Description += "（更正收據金額）";
                RecalcExpense(doc);
                doc.Content.Remark = "已依財務意見更正第 2 筆金額並重新上傳收據。";
                t = Submit(doc, app, t, resubmit: true);
                RunApprovals(doc, t);
                return;
            }

            case "resubmit-sick":
                t = ReturnToApplicant(doc, t, "病假連續三日以上，請檢附就醫證明（診斷書或醫療收據）後再送出。");
                t = AddWH(t, RD(3, 12));
                doc.Content.Remark = "已補附醫院門診收據及診斷證明書。";
                t = Submit(doc, app, t, resubmit: true);
                RunApprovals(doc, t);
                return;

            case "return-prev":
                while (doc.GetCurrentStep()!.Name != "財務會簽") t = ApproveCurrent(doc, t);
                t = ReturnToPrevious(doc, t, "fin.mgr", "請補充三家比價資料，並說明選擇此供應商的理由。");
                t = ApproveCurrent(doc, t, "已補比價單，三家中此供應商報價最低且含三年到府保固，同意。");
                RunApprovals(doc, t);
                return;
        }

        switch (spec.Final)
        {
            case DocumentStatus.Approved:
                RunApprovals(doc, t);
                break;

            case DocumentStatus.Rejected:
            {
                var k = R(1, activeCount);
                for (var i = 1; i < k; i++) t = ApproveCurrent(doc, t);
                Reject(doc, t, RejectComment(doc.DocumentType));
                break;
            }

            case DocumentStatus.Returned:
            {
                var k = R(1, activeCount);
                for (var i = 1; i < k; i++) t = ApproveCurrent(doc, t);
                ReturnToApplicant(doc, t, ReturnComment(doc.DocumentType));
                break;
            }

            case DocumentStatus.Withdrawn:
            {
                var k = R(1, activeCount);
                for (var i = 1; i < k; i++) t = ApproveCurrent(doc, t);
                Withdraw(doc, app, t);
                break;
            }

            case DocumentStatus.InProgress:
            {
                var k = spec.Special == "progress-2" ? Math.Min(2, activeCount) : R(1, activeCount);
                try
                {
                    for (var i = 1; i < k; i++) t = ApproveCurrent(doc, t, quick: true);
                }
                catch (TooLateException)
                {
                    // 停在目前關卡
                }
                // 進行中單據的目前關卡不可已逾期，否則改挑較近的日期重產
                if (doc.GetCurrentStep()?.DueAt is { } due && due <= _now) throw new NotYetException();
                break;
            }

            case DocumentStatus.Overdue:
            {
                var k = spec.Special == "escalated" ? 1 : R(1, Math.Min(2, activeCount));
                for (var i = 1; i < k; i++) t = ApproveCurrent(doc, t, quick: true);
                MarkOverdue(doc, escalate: spec.Special == "escalated");
                break;
            }
        }
    }

    private WorkflowTemplate TemplateFor(DocumentType type, DateOnly date) =>
        _templates.Where(t => t.DocumentType == type && t.EffectiveFrom <= date)
            .OrderByDescending(t => t.EffectiveFrom).First();

    private DateTimeOffset Submit(Document doc, Employee app, DateTimeOffset t, bool resubmit)
    {
        var from = doc.Status;
        var tpl = doc.TemplateId is int tid ? _templates.First(x => x.Id == tid) : TemplateFor(doc.DocumentType, DateOnly.FromDateTime(t.DateTime));
        doc.TemplateId = tpl.Id;
        doc.TemplateVersion = tpl.TemplateVersion;
        doc.Steps = StepBuilder.Build(tpl, app, doc.Amount, doc.Hours, _org, DefaultSla);
        doc.RevisionNo++;
        doc.Revisions.Add(new DocumentRevision
        {
            RevisionNo = doc.RevisionNo, SubmittedAt = t, Title = doc.Title,
            Amount = doc.Amount, Hours = doc.Hours, Content = CloneContent(doc.Content)
        });
        doc.SubmittedAt ??= t;
        doc.Status = DocumentStatus.InProgress;
        Record(doc, resubmit ? ApprovalActionType.Resubmit : ApprovalActionType.Submit, t, app.Id, null,
            comment: resubmit ? doc.Content.Remark : null, from: from, to: DocumentStatus.InProgress);
        ActivateNext(doc, 0, t);
        return t;
    }

    private void ActivateNext(Document doc, int afterStepNo, DateTimeOffset t)
    {
        var next = doc.Steps.Where(s => s.StepNo > afterStepNo && s.Status == StepStatus.Pending)
            .OrderBy(s => s.StepNo).FirstOrDefault();
        if (next is null)
        {
            doc.Status = DocumentStatus.Approved;
            doc.CompletedAt = t;
            doc.CurrentStepNo = null;
            if (doc.History.Count > 0) doc.History[^1].ToStatus = DocumentStatus.Approved;
            if (doc.History.Count > 0) doc.History[^1].FromStatus ??= DocumentStatus.InProgress;
            _notes.Add(new NoteSeed(doc, doc.ApplicantId, NotificationType.Approved, t));
            foreach (var cc in doc.CcEmployeeIds) _notes.Add(new NoteSeed(doc, cc, NotificationType.Cc, t));
            return;
        }
        Activate(doc, next, t);
    }

    private void Activate(Document doc, DocumentStep step, DateTimeOffset t)
    {
        step.Status = StepStatus.Active;
        step.ActivatedAt = t;
        step.DueAt = AddWH(t, step.SlaHours ?? DefaultSla);
        doc.CurrentStepNo = step.StepNo;
        foreach (var a in step.Assignees.Where(a => a.Decision == AssigneeDecision.Pending))
            _notes.Add(new NoteSeed(doc, a.EmployeeId, NotificationType.TaskAssigned, t, step.Name));
    }

    /// <summary>加工作小時並取整到分鐘（讓 JSON 時間易讀）</summary>
    private DateTimeOffset AddWH(DateTimeOffset t, double hours)
    {
        var r = _cal.AddWorkingHours(t, hours);
        return new DateTimeOffset(r.Year, r.Month, r.Day, r.Hour, r.Minute, 0, r.Offset);
    }

    private double Delay(bool quick = false) => quick
        ? RD(0.2, 1.5)
        : Weighted((0, 70), (1, 25), (2, 5)) switch
        {
            0 => RD(0.2, 4),
            1 => RD(4, 12),
            _ => RD(12, 20)
        };

    private DateTimeOffset NextTime(DateTimeOffset t, bool quick = false)
    {
        var at = AddWH(t, Delay(quick));
        if (at >= _now) throw new TooLateException();
        return at;
    }

    private int ActorFor(int principalId, DateTimeOffset at, DocumentType type)
    {
        var date = DateOnly.FromDateTime(at.DateTime);
        var d = _delegations.FirstOrDefault(x => x.PrincipalId == principalId && x.IsEffectiveOn(date, type));
        return d?.DelegateId ?? principalId;
    }

    private (int Actor, int? OnBehalf) Decide(Document doc, StepAssignee a, DateTimeOffset at, AssigneeDecision decision, string? comment)
    {
        var actor = ActorFor(a.EmployeeId, at, doc.DocumentType);
        // 代理人本身也是同關卡簽核人時，不代理（由本人簽核，避免一人簽兩份）
        var step = doc.GetCurrentStep();
        if (actor != a.EmployeeId && step != null && step.Assignees.Any(x => x.EmployeeId == actor)) actor = a.EmployeeId;
        a.Decision = decision;
        a.ActedById = actor;
        a.DecidedAt = at;
        a.Comment = comment;
        return (actor, actor != a.EmployeeId ? a.EmployeeId : null);
    }

    private string? RandomApproveComment() => Chance(0.55) ? null : Pick(ApproveComments);

    private DateTimeOffset ApproveCurrent(Document doc, DateTimeOffset t, string? comment = null, bool quick = false)
    {
        var step = doc.GetCurrentStep() ?? throw new InvalidOperationException("無目前關卡");
        var pending = step.Assignees.Where(a => a.Decision == AssigneeDecision.Pending).ToList();

        if (step.Mode == ApprovalMode.All)
        {
            foreach (var a in pending.OrderBy(_ => _rng.Next()))
            {
                var at = NextTime(t, quick);
                var (actor, onBehalf) = Decide(doc, a, at, AssigneeDecision.Approved, comment ?? RandomApproveComment());
                Record(doc, ApprovalActionType.Approve, at, actor, step, a.Comment, onBehalf);
                t = at;
            }
        }
        else
        {
            var a = Pick(pending);
            var at = NextTime(t, quick);
            var (actor, onBehalf) = Decide(doc, a, at, AssigneeDecision.Approved, comment ?? RandomApproveComment());
            Record(doc, ApprovalActionType.Approve, at, actor, step, a.Comment, onBehalf);
            foreach (var other in pending.Where(x => x != a)) other.Decision = AssigneeDecision.NotRequired;
            t = at;
        }

        step.Status = StepStatus.Approved;
        step.CompletedAt = t;
        ActivateNext(doc, step.StepNo, t);
        return t;
    }

    private DateTimeOffset RunApprovals(Document doc, DateTimeOffset t)
    {
        while (doc.Status == DocumentStatus.InProgress) t = ApproveCurrent(doc, t);
        return t;
    }

    private void CloseRemaining(Document doc)
    {
        foreach (var s in doc.Steps)
        {
            if (s.Status is StepStatus.Pending or StepStatus.Active) s.Status = StepStatus.Cancelled;
            foreach (var a in s.Assignees.Where(a => a.Decision == AssigneeDecision.Pending))
                a.Decision = AssigneeDecision.Cancelled;
        }
        doc.CurrentStepNo = null;
    }

    private DateTimeOffset Reject(Document doc, DateTimeOffset t, string comment)
    {
        var step = doc.GetCurrentStep()!;
        var a = Pick(step.Assignees.Where(x => x.Decision == AssigneeDecision.Pending).ToList());
        var at = NextTime(t);
        var (actor, onBehalf) = Decide(doc, a, at, AssigneeDecision.Rejected, comment);
        step.Status = StepStatus.Rejected;
        step.CompletedAt = at;
        Record(doc, ApprovalActionType.Reject, at, actor, step, comment, onBehalf, from: DocumentStatus.InProgress, to: DocumentStatus.Rejected);
        CloseRemaining(doc);
        doc.Status = DocumentStatus.Rejected;
        doc.CompletedAt = at;
        _notes.Add(new NoteSeed(doc, doc.ApplicantId, NotificationType.Rejected, at, comment));
        return at;
    }

    private DateTimeOffset ReturnToApplicant(Document doc, DateTimeOffset t, string comment)
    {
        var step = doc.GetCurrentStep()!;
        var a = Pick(step.Assignees.Where(x => x.Decision == AssigneeDecision.Pending).ToList());
        var at = NextTime(t);
        var (actor, onBehalf) = Decide(doc, a, at, AssigneeDecision.Returned, comment);
        step.Status = StepStatus.Returned;
        step.CompletedAt = at;
        Record(doc, ApprovalActionType.ReturnToApplicant, at, actor, step, comment, onBehalf, from: DocumentStatus.InProgress, to: DocumentStatus.Returned);
        CloseRemaining(doc);
        doc.Status = DocumentStatus.Returned;
        _notes.Add(new NoteSeed(doc, doc.ApplicantId, NotificationType.Returned, at, comment));
        return at;
    }

    private DateTimeOffset ReturnToPrevious(Document doc, DateTimeOffset t, string account, string comment)
    {
        var step = doc.GetCurrentStep()!;
        var a = step.Assignees.First(x => x.EmployeeId == E(account).Id && x.Decision == AssigneeDecision.Pending);
        var at = NextTime(t);
        var (actor, onBehalf) = Decide(doc, a, at, AssigneeDecision.Returned, comment);
        Record(doc, ApprovalActionType.ReturnToPrevious, at, actor, step, comment, onBehalf);

        // 目前關卡重置為未開始；上一個已核准關卡重新啟動
        step.Status = StepStatus.Pending;
        step.ActivatedAt = null;
        step.DueAt = null;
        foreach (var x in step.Assignees) ResetAssignee(x);

        var prev = doc.Steps.Where(s => s.StepNo < step.StepNo && s.Status == StepStatus.Approved).OrderByDescending(s => s.StepNo).First();
        foreach (var x in prev.Assignees) ResetAssignee(x);
        prev.CompletedAt = null;
        Activate(doc, prev, at);
        return at;
    }

    private static void ResetAssignee(StepAssignee a)
    {
        a.Decision = AssigneeDecision.Pending;
        a.ActedById = null;
        a.DecidedAt = null;
        a.Comment = null;
    }

    private void Withdraw(Document doc, Employee app, DateTimeOffset t)
    {
        var at = AddWH(t, RD(0.5, 6));
        if (at >= _now) throw new TooLateException();
        var step = doc.GetCurrentStep()!;
        var comment = Pick(new[] { "日期填寫錯誤，撤回後重新申請。", "行程取消，撤回申請。", "金額有誤，撤回修正。", "客戶會議改期，暫不需要。" });
        foreach (var a in step.Assignees.Where(a => a.Decision == AssigneeDecision.Pending))
            _notes.Add(new NoteSeed(doc, a.EmployeeId, NotificationType.Withdrawn, at));
        Record(doc, ApprovalActionType.Withdraw, at, app.Id, step, comment, from: DocumentStatus.InProgress, to: DocumentStatus.Withdrawn);
        CloseRemaining(doc);
        doc.Status = DocumentStatus.Withdrawn;
        doc.CompletedAt = at;
    }

    private DateTimeOffset AddSigner(Document doc, DateTimeOffset t, string account, string comment)
    {
        var step = doc.GetCurrentStep()!;
        var a = step.Assignees.First(x => x.Decision == AssigneeDecision.Pending);
        var at = NextTime(t, quick: true);
        var actor = ActorFor(a.EmployeeId, at, doc.DocumentType);
        var target = E(account);
        var added = new DocumentStep
        {
            Name = $"加簽：{target.Name}",
            Mode = ApprovalMode.Single,
            SlaHours = DefaultSla,
            IsAddedStep = true,
            Assignees = [new StepAssignee { EmployeeId = target.Id, Source = AssigneeSource.AddedSigner, AssignedById = actor }]
        };
        doc.Steps.Insert(doc.Steps.IndexOf(step) + 1, added);
        StepBuilder.Renumber(doc.Steps);
        Record(doc, ApprovalActionType.AddSigner, at, actor, step, comment, actor != a.EmployeeId ? a.EmployeeId : null, target.Id);
        return at;
    }

    private DateTimeOffset Transfer(Document doc, DateTimeOffset t, string account, string comment)
    {
        var step = doc.GetCurrentStep()!;
        var a = step.Assignees.First(x => x.Decision == AssigneeDecision.Pending);
        var at = NextTime(t, quick: true);
        var target = E(account);
        var (actor, onBehalf) = Decide(doc, a, at, AssigneeDecision.Transferred, comment);
        step.Assignees.Add(new StepAssignee { EmployeeId = target.Id, Source = AssigneeSource.Transferred, AssignedById = actor });
        Record(doc, ApprovalActionType.Transfer, at, actor, step, comment, onBehalf, target.Id);
        _notes.Add(new NoteSeed(doc, target.Id, NotificationType.TaskAssigned, at, step.Name));
        return at;
    }

    private void MarkOverdue(Document doc, bool escalate)
    {
        var step = doc.GetCurrentStep()!;
        var sla = step.SlaHours ?? DefaultSla;
        var remindAt = AddWH(step.ActivatedAt!.Value, Math.Max(1, sla - 4));
        if (remindAt < _now)
        {
            step.RemindedAt = remindAt;
            Record(doc, ApprovalActionType.Remind, remindAt, null, step, "距關卡時效剩 4 個工作小時，已提醒簽核人。");
            foreach (var a in step.Assignees.Where(a => a.Decision == AssigneeDecision.Pending))
                _notes.Add(new NoteSeed(doc, a.EmployeeId, NotificationType.Reminder, remindAt, "即將超過簽核時效"));
        }
        if (step.DueAt is { } due && due < _now)
        {
            Record(doc, ApprovalActionType.Remind, due, null, step,
                $"已超過關卡時效（{sla} 個工作小時），狀態改為逾時。", from: DocumentStatus.InProgress, to: DocumentStatus.Overdue);
            doc.Status = DocumentStatus.Overdue;
            foreach (var a in step.Assignees.Where(a => a.Decision == AssigneeDecision.Pending))
                _notes.Add(new NoteSeed(doc, a.EmployeeId, NotificationType.Reminder, due, "已超過簽核時效"));

            if (escalate)
            {
                var at = AddWH(due, 8);
                if (at < _now)
                {
                    var a = step.Assignees.First(x => x.Decision == AssigneeDecision.Pending);
                    var mgr = _org.ActiveManagerOf(a.EmployeeId)!;
                    a.Decision = AssigneeDecision.Escalated;
                    a.DecidedAt = at;
                    step.Assignees.Add(new StepAssignee { EmployeeId = mgr.Id, Source = AssigneeSource.Escalated });
                    step.IsEscalated = true;
                    Record(doc, ApprovalActionType.Escalate, at, null, step,
                        "逾時超過 8 個工作小時未簽核，系統自動升級至上一層主管。", target: mgr.Id);
                    _notes.Add(new NoteSeed(doc, mgr.Id, NotificationType.Escalated, at, "逾時單據已升級由您簽核"));
                    _notes.Add(new NoteSeed(doc, a.EmployeeId, NotificationType.Escalated, at, "您的逾時待辦已升級至上一層主管"));
                }
            }
        }
    }

    private static void Record(Document doc, ApprovalActionType action, DateTimeOffset at, int? actorId, DocumentStep? step,
        string? comment = null, int? onBehalf = null, int? target = null, DocumentStatus? from = null, DocumentStatus? to = null)
    {
        doc.History.Add(new ApprovalRecord
        {
            Seq = doc.History.Count + 1,
            At = at,
            Action = action,
            ActorId = actorId,
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

    private static DocumentContent CloneContent(DocumentContent c) =>
        JsonSerializer.Deserialize<DocumentContent>(JsonSerializer.SerializeToUtf8Bytes(c, JsonDefaults.Options), JsonDefaults.Options)!;

    private string RejectComment(DocumentType type) => Pick(type switch
    {
        DocumentType.Leave => new[] { "月底結帳期間人力不足，請改期申請。", "與部門重要專案時程衝突，請調整日期。" },
        DocumentType.Expense => new[] { "交際費未事先申請核准，依規定不予核銷。", "此筆屬個人消費，不符報銷規定。", "已超過報銷期限（60 日），依規定不予受理。" },
        DocumentType.Purchase => new[] { "本季預算已用罄，請於下季重新提出。", "請改向公司合約供應商採購。", "現有設備仍堪用，暫不同意汰換。" },
        _ => new[] { "此工作可於正常上班時間完成，不同意加班。", "本月加班時數已接近上限，請調整人力安排。" }
    });

    private string ReturnComment(DocumentType type) => Pick(type switch
    {
        DocumentType.Leave => new[] { "請填寫職務代理人並確認交接事項。", "請假事由請具體說明。" },
        DocumentType.Expense => new[] { "請補附發票影本。", "請註明交際對象與公司名稱。", "請說明出差事由與行程。" },
        DocumentType.Purchase => new[] { "請補充規格說明與報價單。", "請確認預算科目是否正確。" },
        _ => new[] { "請說明加班具體工作內容。", "加班時段與打卡紀錄不符，請確認。" }
    });

    // ------------------------------------------------------------------ 單號

    private void AssignDocumentNumbers()
    {
        var counters = new Dictionary<(string Prefix, string Period), int>();
        foreach (var d in _documents.Where(d => d.SubmittedAt != null).OrderBy(d => d.SubmittedAt))
        {
            var cfg = _typeConfigs.First(c => c.DocumentType == d.DocumentType);
            var key = (cfg.Prefix, d.SubmittedAt!.Value.ToString("yyyyMM"));
            counters[key] = counters.GetValueOrDefault(key) + 1;
            d.DocumentNo = Application.Services.DocumentNumberService.Format(cfg, d.SubmittedAt.Value, counters[key]);
        }
        var id = 1;
        foreach (var ((prefix, period), last) in counters.OrderBy(k => k.Key.Period).ThenBy(k => k.Key.Prefix))
            _sequences.Add(new DocumentSequence { Id = id++, Version = 1, Prefix = prefix, Period = period, LastNumber = last });
    }

    // ------------------------------------------------------------------ 通知

    private void GenerateNotifications()
    {
        var id = 1;
        foreach (var n in _notes.Where(n => n.At <= _now).OrderByDescending(n => n.At).Take(60).OrderBy(n => n.At))
        {
            var doc = n.Doc;
            var app = _employees.First(e => e.Id == doc.ApplicantId);
            var typeName = doc.DocumentType.ToDisplay();
            var (title, msg) = n.Type switch
            {
                NotificationType.TaskAssigned => ($"待簽核：{doc.DocumentNo}", $"{app.Name} 的{typeName}「{doc.Title}」已送達「{n.Extra}」，請您簽核。"),
                NotificationType.Approved => ($"已核准：{doc.DocumentNo}", $"您的{typeName}「{doc.Title}」已完成簽核並核准。"),
                NotificationType.Rejected => ($"已駁回：{doc.DocumentNo}", $"您的{typeName}「{doc.Title}」已被駁回。意見：{n.Extra}"),
                NotificationType.Returned => ($"已退回：{doc.DocumentNo}", $"您的{typeName}「{doc.Title}」已退回修改。意見：{n.Extra}"),
                NotificationType.Withdrawn => ($"已撤回：{doc.DocumentNo}", $"{app.Name} 已撤回{typeName}「{doc.Title}」，您無需再簽核。"),
                NotificationType.Cc => ($"抄送：{doc.DocumentNo}", $"{app.Name} 的{typeName}「{doc.Title}」已核准，抄送給您知悉。"),
                NotificationType.Reminder => ($"簽核提醒：{doc.DocumentNo}", $"{typeName}「{doc.Title}」{n.Extra}，請儘速處理。"),
                NotificationType.Escalated => ($"逾時升級：{doc.DocumentNo}", $"{typeName}「{doc.Title}」：{n.Extra}。"),
                _ => (doc.DocumentNo, doc.Title)
            };
            var age = _now - n.At;
            var isRead = age > TimeSpan.FromDays(3) || (age > TimeSpan.FromHours(20) && Chance(0.5));
            DateTimeOffset? readAt = null;
            if (isRead)
            {
                var r = n.At.AddMinutes(R(5, 600));
                readAt = r < _now ? r : _now.AddMinutes(-5);
            }
            _notifications.Add(new Notification
            {
                Id = id++, Version = 1, RecipientId = n.RecipientId, Type = n.Type, Title = title, Message = msg,
                DocumentId = doc.Id, Link = $"/Documents/Details/{doc.Id}", CreatedAt = n.At,
                IsRead = isRead, ReadAt = readAt
            });
        }
    }

    // ------------------------------------------------------------------ 稽核

    private void GenerateAuditLogs()
    {
        var logs = new List<AuditLog>();
        string Ip() => Chance(0.85) ? $"10.10.20.{R(11, 250)}" : $"172.16.8.{R(2, 120)}";
        AuditLog Log(DateTimeOffset at, Employee? who, string action, string entity, string? entityId, string desc, List<FieldChange>? changes = null) =>
            new() { At = at, UserId = who?.Id, UserName = who?.Name ?? "系統", IpAddress = who is null ? null : Ip(), Action = action, EntityType = entity, EntityId = entityId, Description = desc, Changes = changes ?? [] };

        var admin = E("admin");
        var hrLead = E("hr.lead");

        // 系統管理類
        logs.Add(Log(At(new DateOnly(2026, 1, 5), 10, 12), admin, "Update", nameof(SystemSetting), "1", "更新系統參數：公司名稱",
            [new() { Field = "Value", OldValue = "微積分科技有限公司", NewValue = "微積分科技股份有限公司" }]));
        logs.Add(Log(At(new DateOnly(2026, 1, 6), 14, 40), hrLead, "Update", nameof(LeaveType), "3", "更新假別：病假說明",
            [new() { Field = "Description", OldValue = "未住院者一年內合計不得超過 30 日", NewValue = "未住院者一年內合計不得超過 30 日，半薪；連續 3 日以上須檢附證明" }]));
        logs.Add(Log(At(new DateOnly(2026, 1, 8), 9, 35), admin, "Create", nameof(Holiday), "1", "匯入 2026 年政府行政機關辦公日曆（16 筆）"));
        logs.Add(Log(At(new DateOnly(2026, 2, 2), 11, 5), admin, "Update", nameof(Employee), E("acc03").Id.ToString(), $"指派角色：{E("acc03").Name}",
            [new() { Field = "Roles", OldValue = "Applicant", NewValue = "Applicant,Finance" }]));
        logs.Add(Log(At(new DateOnly(2026, 3, 31), 17, 45), admin, "Deactivate", nameof(Employee), E("sales05").Id.ToString(), $"停用帳號：{E("sales05").Name}（離職）",
            [new() { Field = "IsActive", OldValue = "True", NewValue = "False" }]));
        logs.Add(Log(At(new DateOnly(2026, 5, 25), 14, 20), admin, "Publish", nameof(WorkflowTemplate), "3", "發布流程範本「費用報銷流程」v2，2026-06-01 生效",
            [new() { Field = "TemplateVersion", OldValue = "1", NewValue = "2" }]));
        logs.Add(Log(At(new DateOnly(2026, 5, 29), 17, 30), admin, "Deactivate", nameof(Employee), E("hr02").Id.ToString(), $"停用帳號：{E("hr02").Name}（離職）",
            [new() { Field = "IsActive", OldValue = "True", NewValue = "False" }]));
        logs.Add(Log(At(new DateOnly(2026, 7, 1), 10, 0), admin, "Update", nameof(SystemSetting), "13", "更新系統參數：開放批次核准",
            [new() { Field = "Value", OldValue = "false", NewValue = "true" }]));
        logs.Add(Log(At(new DateOnly(2026, 8, 3), 15, 22), admin, "ResetPassword", nameof(Employee), E("sales04").Id.ToString(), $"重設密碼：{E("sales04").Name}"));
        logs.Add(Log(At(new DateOnly(2026, 9, 14), 9, 50), hrLead, "Update", nameof(ExpenseCategory), "3", "更新費用類別：誤餐費單筆上限",
            [new() { Field = "MaxAmountPerItem", OldValue = "300", NewValue = "500" }]));

        foreach (var d in _delegations)
        {
            var p = _employees.First(e => e.Id == d.PrincipalId);
            var dl = _employees.First(e => e.Id == d.DelegateId);
            logs.Add(Log(d.CreatedAt, p, "Create", nameof(Delegation), d.Id.ToString(),
                $"設定代理人：{p.Name} → {dl.Name}（{d.StartDate:yyyy/MM/dd}～{d.EndDate:yyyy/MM/dd}）"));
        }

        // 登入紀錄（近 30 天）
        var loginUsers = _employees.Where(e => e.IsActive).ToList();
        for (var i = 0; i < 18; i++)
        {
            var who = Pick(loginUsers);
            var day = _cal.PreviousWorkday(_anchor.AddDays(-R(0, 30)));
            var at = At(day, R(8, 9), R(0, 59));
            if (at >= _now) at = _now.AddMinutes(-R(10, 60));
            logs.Add(Log(at, who, "Login", nameof(Employee), who.Id.ToString(), "登入成功"));
        }
        foreach (var who in new[] { E("sales04"), E("ga01") })
        {
            var at = At(_cal.PreviousWorkday(_anchor.AddDays(-R(2, 20))), 8, R(40, 59));
            logs.Add(Log(at, who, "LoginFailed", nameof(Employee), who.Id.ToString(), "登入失敗：密碼錯誤"));
        }

        // 單據動作（取最近的）
        var docLogs = new List<AuditLog>();
        foreach (var d in _documents)
        {
            foreach (var h in d.History.Where(h => h.ActorId != null))
            {
                var who = _employees.First(e => e.Id == h.ActorId);
                var desc = $"{h.Action.ToDisplay()}：{d.DocumentNo} {d.Title}" +
                           (h.OnBehalfOfId is int ob ? $"（代理 {_employees.First(e => e.Id == ob).Name}）" : "");
                var changes = h.ToStatus is { } to
                    ? new List<FieldChange> { new() { Field = "Status", OldValue = h.FromStatus?.ToString(), NewValue = to.ToString() } }
                    : [];
                docLogs.Add(Log(h.At, who, h.Action.ToString(), nameof(Document), d.Id.ToString(), desc, changes));
            }
        }
        var remaining = 80 - logs.Count;
        logs.AddRange(docLogs.OrderByDescending(l => l.At).Take(remaining));

        var id = 1;
        foreach (var l in logs.OrderBy(l => l.At))
        {
            l.Id = id++;
            l.Version = 1;
            _audits.Add(l);
        }
    }
}
