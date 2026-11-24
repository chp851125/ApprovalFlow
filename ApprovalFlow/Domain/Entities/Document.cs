using ApprovalFlow.Domain.Common;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Domain.Entities;

/// <summary>簽核單據（四種單別共用，表單內容放在 Content）</summary>
public class Document : EntityBase
{
    /// <summary>單號；草稿尚未送出時為空字串</summary>
    public string DocumentNo { get; set; } = "";
    public DocumentType DocumentType { get; set; }
    public string Title { get; set; } = "";
    public int ApplicantId { get; set; }
    public int DepartmentId { get; set; }
    public DocumentStatus Status { get; set; } = DocumentStatus.Draft;
    public bool IsUrgent { get; set; }

    /// <summary>表單內容（目前版本）</summary>
    public DocumentContent Content { get; set; } = new();

    /// <summary>條件分支與統計用：金額</summary>
    public decimal? Amount { get; set; }
    /// <summary>條件分支與統計用：時數（請假 / 加班）</summary>
    public decimal? Hours { get; set; }

    // ---- 流程 ----
    public int? TemplateId { get; set; }
    public int? TemplateVersion { get; set; }
    /// <summary>流程實例的關卡（送出時依範本展開）</summary>
    public List<DocumentStep> Steps { get; set; } = [];
    public int? CurrentStepNo { get; set; }
    /// <summary>簽核時間軸</summary>
    public List<ApprovalRecord> History { get; set; } = [];
    /// <summary>每次送出時的內容快照（退回修改後再送出會新增一版）</summary>
    public List<DocumentRevision> Revisions { get; set; } = [];
    public int RevisionNo { get; set; }

    public List<int> CcEmployeeIds { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? SubmittedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public DocumentStep? GetCurrentStep() =>
        CurrentStepNo is null ? null : Steps.FirstOrDefault(s => s.StepNo == CurrentStepNo);
}

/// <summary>表單內容；依單別只會有一個子物件有值</summary>
public class DocumentContent
{
    public LeaveForm? Leave { get; set; }
    public ExpenseForm? Expense { get; set; }
    public PurchaseForm? Purchase { get; set; }
    public OvertimeForm? Overtime { get; set; }
    /// <summary>補充說明</summary>
    public string? Remark { get; set; }
}

public class LeaveForm
{
    public int LeaveTypeId { get; set; }
    public DateTimeOffset StartAt { get; set; }
    public DateTimeOffset EndAt { get; set; }
    public decimal Hours { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>職務代理人</summary>
    public int? DeputyId { get; set; }
}

public class ExpenseForm
{
    public List<ExpenseItem> Items { get; set; } = [];
    public decimal TotalAmount { get; set; }
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.BankTransfer;
    public string? ProjectCode { get; set; }
    public string Purpose { get; set; } = "";
}

public class ExpenseItem
{
    public DateOnly Date { get; set; }
    public int CategoryId { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    /// <summary>發票 / 收據號碼</summary>
    public string? ReceiptNo { get; set; }
}

public class PurchaseForm
{
    public int? SupplierId { get; set; }
    public int BudgetSubjectId { get; set; }
    public List<PurchaseItem> Items { get; set; } = [];
    public decimal TotalAmount { get; set; }
    public DateOnly? RequiredDate { get; set; }
    public string Purpose { get; set; } = "";
    public string? DeliveryLocation { get; set; }
}

public class PurchaseItem
{
    public string Name { get; set; } = "";
    public string? Specification { get; set; }
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = "個";
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }
}

public class OvertimeForm
{
    public DateOnly Date { get; set; }
    public TimeOnly StartTime { get; set; }
    public TimeOnly EndTime { get; set; }
    public decimal Hours { get; set; }
    public OvertimeCompensation Compensation { get; set; }
    public string Reason { get; set; } = "";
    public bool IsHoliday { get; set; }
}

/// <summary>流程實例關卡</summary>
public class DocumentStep
{
    public int StepNo { get; set; }
    /// <summary>來源範本關卡編號；加簽產生的關卡為 null</summary>
    public int? TemplateStepNo { get; set; }
    public string Name { get; set; } = "";
    public ApprovalMode Mode { get; set; }
    public StepStatus Status { get; set; } = StepStatus.Pending;
    public string? SkipReason { get; set; }
    public int? SlaHours { get; set; }
    public List<StepAssignee> Assignees { get; set; } = [];
    public DateTimeOffset? ActivatedAt { get; set; }
    public DateTimeOffset? DueAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public DateTimeOffset? RemindedAt { get; set; }
    public bool IsEscalated { get; set; }
    public bool IsAddedStep { get; set; }
}

/// <summary>關卡簽核人</summary>
public class StepAssignee
{
    /// <summary>應簽核的人</summary>
    public int EmployeeId { get; set; }
    public AssigneeDecision Decision { get; set; } = AssigneeDecision.Pending;
    /// <summary>實際執行者（代理時與 EmployeeId 不同）</summary>
    public int? ActedById { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? Comment { get; set; }
    public AssigneeSource Source { get; set; } = AssigneeSource.Workflow;
    /// <summary>加簽 / 轉簽 / 升級的發起人</summary>
    public int? AssignedById { get; set; }

    public bool IsDelegated() => ActedById.HasValue && ActedById.Value != EmployeeId;
}

/// <summary>簽核時間軸紀錄</summary>
public class ApprovalRecord
{
    public int Seq { get; set; }
    public DateTimeOffset At { get; set; }
    public ApprovalActionType Action { get; set; }
    /// <summary>實際執行者；系統動作時為 null</summary>
    public int? ActorId { get; set; }
    /// <summary>代理簽核時的被代理人</summary>
    public int? OnBehalfOfId { get; set; }
    public int? StepNo { get; set; }
    public string? StepName { get; set; }
    /// <summary>加簽 / 轉簽 / 升級的對象</summary>
    public int? TargetEmployeeId { get; set; }
    public string? Comment { get; set; }
    public DocumentStatus? FromStatus { get; set; }
    public DocumentStatus? ToStatus { get; set; }
    public int RevisionNo { get; set; }
}

/// <summary>送出時的內容快照</summary>
public class DocumentRevision
{
    public int RevisionNo { get; set; }
    public DateTimeOffset SubmittedAt { get; set; }
    public string Title { get; set; } = "";
    public decimal? Amount { get; set; }
    public decimal? Hours { get; set; }
    public DocumentContent Content { get; set; } = new();
}
