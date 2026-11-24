namespace ApprovalFlow.Domain.Enums;

/// <summary>單別</summary>
public enum DocumentType
{
    Leave,
    Expense,
    Purchase,
    Overtime
}

/// <summary>單據狀態</summary>
public enum DocumentStatus
{
    Draft,
    InProgress,
    Approved,
    Rejected,
    Returned,
    Withdrawn,
    /// <summary>簽核中但目前關卡已超過 SLA（仍屬進行中）</summary>
    Overdue
}

/// <summary>簽核人來源</summary>
public enum ApproverSource
{
    DirectManager,
    DepartmentManager,
    Role,
    SpecificUser,
    ManagerLevelN
}

/// <summary>關卡簽核方式</summary>
public enum ApprovalMode
{
    /// <summary>單簽：一人簽核即完成</summary>
    Single,
    /// <summary>會簽：所有簽核人皆須同意</summary>
    All,
    /// <summary>或簽：任一人同意即完成</summary>
    Any
}

public enum StepStatus
{
    Pending,
    Active,
    Approved,
    Rejected,
    Returned,
    Skipped,
    Cancelled
}

public enum AssigneeDecision
{
    Pending,
    Approved,
    Rejected,
    Returned,
    Transferred,
    Escalated,
    /// <summary>或簽關卡已由他人完成，不需再簽</summary>
    NotRequired,
    Cancelled
}

public enum AssigneeSource
{
    Workflow,
    AddedSigner,
    Transferred,
    Escalated
}

public enum ApprovalActionType
{
    Submit,
    Resubmit,
    Approve,
    Reject,
    ReturnToPrevious,
    ReturnToApplicant,
    Withdraw,
    AddSigner,
    Transfer,
    Escalate,
    Remind,
    Comment
}

public enum ConditionField
{
    Amount,
    Days,
    Hours
}

public enum ConditionOperator
{
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
    Equal
}

public enum HolidayType
{
    /// <summary>放假日</summary>
    Holiday,
    /// <summary>補班日</summary>
    MakeupWorkday
}

public enum NotificationType
{
    TaskAssigned,
    Approved,
    Rejected,
    Returned,
    Withdrawn,
    Cc,
    Reminder,
    Escalated,
    DailyDigest,
    System
}

public enum OvertimeCompensation
{
    Pay,
    CompensatoryLeave
}

public enum EmailStatus
{
    Pending,
    Written,
    Failed
}

public enum PaymentMethod
{
    BankTransfer,
    Cash,
    PettyCash
}
