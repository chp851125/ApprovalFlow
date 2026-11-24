using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Application.Workflow;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Documents;

public enum DocumentBox { Todo, Done, Mine, Cc, Drafts }

public sealed class DocumentQuery
{
    public DocumentBox Box { get; set; } = DocumentBox.Todo;
    public DocumentType? Type { get; set; }
    public DocumentStatus? Status { get; set; }
    public string? Keyword { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    /// <summary>updated / submitted / submitted_asc / amount / due</summary>
    public string? Sort { get; set; }
}

public sealed record DocumentListItem(
    Document Document,
    string ApplicantName,
    string DepartmentName,
    string? CurrentStepName,
    DateTimeOffset? DueAt,
    bool IsOverdue,
    int? OnBehalfOfId,
    bool CanBatchApprove,
    string Summary);

public sealed record BoxCounts(int Todo, int Drafts, int Cc);

public sealed record BatchResult(int Succeeded, IReadOnlyList<string> Failures);

/// <summary>單據清單查詢（待辦、已辦、我的申請、抄送、草稿）與批次核准</summary>
public sealed class DocumentQueryService(
    IRepository<Document> documents,
    IRepository<Employee> employees,
    IRepository<Department> departments,
    IRepository<Delegation> delegations,
    IRepository<DocumentTypeConfig> typeConfigs,
    IRepository<LeaveType> leaveTypes,
    SettingsService settings,
    WorkflowEngine engine,
    ICurrentUser currentUser,
    IClock clock)
{
    public const int MaxBatch = 50;
    private static readonly ApprovalActionType[] HandledActions =
    [
        ApprovalActionType.Approve, ApprovalActionType.Reject, ApprovalActionType.ReturnToApplicant,
        ApprovalActionType.ReturnToPrevious, ApprovalActionType.Transfer, ApprovalActionType.AddSigner
    ];

    public async Task<IReadOnlyList<DocumentListItem>> QueryAsync(DocumentQuery q)
    {
        var me = currentUser.Id ?? throw new ForbiddenException();
        var now = clock.Now;
        var all = await documents.GetAllAsync();
        var principals = await ActingPrincipalsAsync(me);
        var batchTypes = await BatchApproveTypesAsync();

        IEnumerable<Document> src = q.Box switch
        {
            DocumentBox.Todo => all.Where(d => PendingFor(d, me, principals) is not null),
            DocumentBox.Done => all.Where(d => d.ApplicantId != me && d.Status != DocumentStatus.Draft &&
                                              d.History.Any(h => h.ActorId == me && HandledActions.Contains(h.Action))),
            DocumentBox.Mine => all.Where(d => d.ApplicantId == me && d.Status != DocumentStatus.Draft),
            DocumentBox.Cc => all.Where(d => d.CcEmployeeIds.Contains(me) && d.Status != DocumentStatus.Draft),
            DocumentBox.Drafts => all.Where(d => d.ApplicantId == me && d.Status is DocumentStatus.Draft or DocumentStatus.Returned),
            _ => []
        };

        if (q.Type is { } t) src = src.Where(d => d.DocumentType == t);
        if (q.Status is { } s) src = src.Where(d => d.Status == s);
        if (q.From is { } from) src = src.Where(d => DateOnly.FromDateTime((d.SubmittedAt ?? d.CreatedAt).DateTime) >= from);
        if (q.To is { } to) src = src.Where(d => DateOnly.FromDateTime((d.SubmittedAt ?? d.CreatedAt).DateTime) <= to);

        var emps = (await employees.GetAllAsync()).ToDictionary(e => e.Id);
        var depts = (await departments.GetAllAsync()).ToDictionary(d => d.Id, d => d.Name);
        var leaveNames = (await leaveTypes.GetAllAsync()).ToDictionary(l => l.Id, l => l.Name);

        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            var k = q.Keyword.Trim();
            src = src.Where(d => Contains(d.DocumentNo, k) || Contains(d.Title, k) ||
                                 Contains(emps.GetValueOrDefault(d.ApplicantId)?.Name, k) ||
                                 Contains(emps.GetValueOrDefault(d.ApplicantId)?.Account, k) ||
                                 SearchText(d).Any(x => Contains(x, k)));
        }

        var items = src.Select(d =>
        {
            var step = d.Status.IsActiveFlow() ? d.GetCurrentStep() : null;
            var pending = q.Box == DocumentBox.Todo ? PendingFor(d, me, principals) : null;
            return new DocumentListItem(
                d,
                emps.GetValueOrDefault(d.ApplicantId)?.Name ?? "",
                depts.GetValueOrDefault(d.DepartmentId) ?? "",
                step?.Name,
                step?.DueAt,
                step?.DueAt is { } due && due < now,
                pending is { } p && p != me ? p : null,
                q.Box == DocumentBox.Todo && batchTypes.Contains(d.DocumentType),
                Summary(d, leaveNames));
        });

        var sort = q.Sort ?? (q.Box == DocumentBox.Todo ? "due" : q.Box == DocumentBox.Drafts ? "updated" : "submitted");
        items = sort switch
        {
            "due" => items.OrderByDescending(i => i.Document.IsUrgent).ThenBy(i => i.DueAt ?? DateTimeOffset.MaxValue),
            "submitted_asc" => items.OrderBy(i => i.Document.SubmittedAt ?? i.Document.CreatedAt),
            "amount" => items.OrderByDescending(i => i.Document.Amount ?? 0).ThenByDescending(i => i.Document.Hours ?? 0),
            "updated" => items.OrderByDescending(i => i.Document.UpdatedAt),
            _ => items.OrderByDescending(i => i.Document.SubmittedAt ?? i.Document.CreatedAt)
        };
        return items.ToList();
    }

    public async Task<BoxCounts> CountsAsync()
    {
        var me = currentUser.Id ?? 0;
        var all = await documents.GetAllAsync();
        var principals = await ActingPrincipalsAsync(me);
        return new BoxCounts(
            all.Count(d => PendingFor(d, me, principals) is not null),
            all.Count(d => d.ApplicantId == me && d.Status is DocumentStatus.Draft or DocumentStatus.Returned),
            0);
    }

    /// <summary>批次核准目前可用嗎（系統參數）</summary>
    public async Task<bool> IsBatchEnabledAsync() => await settings.GetBoolAsync(SettingKeys.BatchApproveEnabled, false);

    /// <summary>
    /// 批次核准：只允許系統參數開啟且單別設定為「低風險可批次核准」的單據。
    /// 逐筆交由流程引擎核准（各自檢查權限、狀態與版本），單筆失敗不影響其他筆。
    /// </summary>
    public async Task<BatchResult> BatchApproveAsync(IReadOnlyList<(int Id, int Version)> targets, string? comment)
    {
        if (!await IsBatchEnabledAsync()) throw new BusinessException("系統未開啟批次核准功能。");
        if (targets.Count == 0) throw new ValidationException("請勾選要核准的單據。");
        if (targets.Count > MaxBatch) throw new ValidationException($"一次最多批次核准 {MaxBatch} 張。");
        var allowedTypes = await BatchApproveTypesAsync();

        var ok = 0;
        var failures = new List<string>();
        foreach (var (id, version) in targets.DistinctBy(t => t.Id))
        {
            var doc = await documents.GetByIdAsync(id);
            var label = doc is null ? $"#{id}" : (string.IsNullOrEmpty(doc.DocumentNo) ? doc.Title : doc.DocumentNo);
            try
            {
                if (doc is null) throw new NotFoundException("單據", id);
                if (!allowedTypes.Contains(doc.DocumentType))
                    throw new BusinessException($"{doc.DocumentType.ToDisplay()}不開放批次核准，請逐張審核。");
                await engine.ApproveAsync(id, version, string.IsNullOrWhiteSpace(comment) ? "批次核准" : comment.Trim());
                ok++;
            }
            catch (BusinessException ex)
            {
                failures.Add($"{label}：{ex.Message}");
            }
        }
        return new BatchResult(ok, failures);
    }

    // ---------------------------------------------------------------------

    private async Task<HashSet<DocumentType>> BatchApproveTypesAsync() =>
        (await IsBatchEnabledAsync())
            ? (await typeConfigs.QueryAsync(c => c.AllowBatchApprove && c.IsEnabled)).Select(c => c.DocumentType).ToHashSet()
            : [];

    /// <summary>今天有效的代理：被代理人 → 適用單別（空集合 = 全部）</summary>
    private async Task<List<Delegation>> ActingPrincipalsAsync(int me)
    {
        var today = clock.Today;
        return (await delegations.QueryAsync(d => d.DelegateId == me)).Where(d => d.IsEffectiveOn(today)).ToList();
    }

    /// <summary>單據目前關卡是否待我（或我代理的人）簽核；回傳待簽者 Id。規則與流程引擎一致。</summary>
    private static int? PendingFor(Document d, int me, List<Delegation> acting)
    {
        if (!d.Status.IsActiveFlow() || d.GetCurrentStep() is not { Status: StepStatus.Active } step) return null;
        var pending = step.Assignees.Where(a => a.Decision == AssigneeDecision.Pending).ToList();
        if (pending.Any(a => a.EmployeeId == me)) return me;
        if (d.ApplicantId == me) return null;
        return pending.FirstOrDefault(a => acting.Any(x => x.PrincipalId == a.EmployeeId &&
            (x.DocumentTypes.Count == 0 || x.DocumentTypes.Contains(d.DocumentType))))?.EmployeeId;
    }

    private static IEnumerable<string?> SearchText(Document d)
    {
        var c = d.Content;
        yield return c.Remark;
        if (c.Leave is { } l) yield return l.Reason;
        if (c.Expense is { } e)
        {
            yield return e.Purpose;
            yield return e.ProjectCode;
            foreach (var i in e.Items) { yield return i.Description; yield return i.ReceiptNo; }
        }
        if (c.Purchase is { } p)
        {
            yield return p.Purpose;
            foreach (var i in p.Items) { yield return i.Name; yield return i.Specification; }
        }
        if (c.Overtime is { } o) yield return o.Reason;
    }

    private static string Summary(Document d, IReadOnlyDictionary<int, string> leaveNames)
    {
        var c = d.Content;
        return d.DocumentType switch
        {
            DocumentType.Leave when c.Leave is { } l =>
                $"{leaveNames.GetValueOrDefault(l.LeaveTypeId)}・{l.StartAt:MM/dd HH:mm}～{l.EndAt:MM/dd HH:mm}・{l.Hours:0.#} 小時",
            DocumentType.Expense when c.Expense is { } e => $"{e.Items.Count} 筆明細・NT$ {e.TotalAmount:N0}",
            DocumentType.Purchase when c.Purchase is { } p => $"{p.Items.Count} 項・NT$ {p.TotalAmount:N0}",
            DocumentType.Overtime when c.Overtime is { } o => $"{o.Date:MM/dd} {o.StartTime:HH\\:mm}–{o.EndTime:HH\\:mm}・{o.Hours:0.#} 小時・{o.Compensation.ToDisplay()}",
            _ => ""
        };
    }

    private static bool Contains(string? s, string k) => s?.Contains(k, StringComparison.OrdinalIgnoreCase) == true;
}
