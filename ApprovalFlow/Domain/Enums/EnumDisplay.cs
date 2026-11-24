namespace ApprovalFlow.Domain.Enums;

/// <summary>列舉的繁體中文顯示名稱。</summary>
public static class EnumDisplay
{
    public static string ToDisplay(this DocumentType v) => v switch
    {
        DocumentType.Leave => "請假單",
        DocumentType.Expense => "費用報銷單",
        DocumentType.Purchase => "採購申請單",
        DocumentType.Overtime => "加班申請單",
        _ => v.ToString()
    };

    public static string ToDisplay(this DocumentStatus v) => v switch
    {
        DocumentStatus.Draft => "草稿",
        DocumentStatus.InProgress => "簽核中",
        DocumentStatus.Approved => "已核准",
        DocumentStatus.Rejected => "已駁回",
        DocumentStatus.Returned => "已退回",
        DocumentStatus.Withdrawn => "已撤回",
        DocumentStatus.Overdue => "逾時",
        _ => v.ToString()
    };

    /// <summary>Bootstrap 顏色類別</summary>
    public static string ToBadgeClass(this DocumentStatus v) => v switch
    {
        DocumentStatus.Draft => "text-bg-secondary",
        DocumentStatus.InProgress => "text-bg-primary",
        DocumentStatus.Approved => "text-bg-success",
        DocumentStatus.Rejected => "text-bg-danger",
        DocumentStatus.Returned => "text-bg-warning",
        DocumentStatus.Withdrawn => "text-bg-light border",
        DocumentStatus.Overdue => "text-bg-danger",
        _ => "text-bg-secondary"
    };

    public static string ToDisplay(this ApproverSource v) => v switch
    {
        ApproverSource.DirectManager => "直屬主管",
        ApproverSource.DepartmentManager => "部門主管",
        ApproverSource.Role => "指定角色",
        ApproverSource.SpecificUser => "指定人員",
        ApproverSource.ManagerLevelN => "上 N 級主管",
        _ => v.ToString()
    };

    public static string ToDisplay(this ApprovalMode v) => v switch
    {
        ApprovalMode.Single => "單簽",
        ApprovalMode.All => "會簽",
        ApprovalMode.Any => "或簽",
        _ => v.ToString()
    };

    public static string ToDisplay(this StepStatus v) => v switch
    {
        StepStatus.Pending => "未開始",
        StepStatus.Active => "簽核中",
        StepStatus.Approved => "已核准",
        StepStatus.Rejected => "已駁回",
        StepStatus.Returned => "已退回",
        StepStatus.Skipped => "略過",
        StepStatus.Cancelled => "已取消",
        _ => v.ToString()
    };

    public static string ToDisplay(this AssigneeDecision v) => v switch
    {
        AssigneeDecision.Pending => "待簽核",
        AssigneeDecision.Approved => "同意",
        AssigneeDecision.Rejected => "駁回",
        AssigneeDecision.Returned => "退回",
        AssigneeDecision.Transferred => "已轉簽",
        AssigneeDecision.Escalated => "逾時升級",
        AssigneeDecision.NotRequired => "免簽",
        AssigneeDecision.Cancelled => "已取消",
        _ => v.ToString()
    };

    public static string ToDisplay(this ApprovalActionType v) => v switch
    {
        ApprovalActionType.Submit => "送出",
        ApprovalActionType.Resubmit => "修改後重新送出",
        ApprovalActionType.Approve => "核准",
        ApprovalActionType.Reject => "駁回",
        ApprovalActionType.ReturnToPrevious => "退回上一關",
        ApprovalActionType.ReturnToApplicant => "退回申請人",
        ApprovalActionType.Withdraw => "撤回",
        ApprovalActionType.AddSigner => "加簽",
        ApprovalActionType.Transfer => "轉簽",
        ApprovalActionType.Escalate => "逾時升級",
        ApprovalActionType.Remind => "逾時提醒",
        ApprovalActionType.Comment => "意見",
        _ => v.ToString()
    };

    public static string ToDisplay(this ConditionField v) => v switch
    {
        ConditionField.Amount => "金額",
        ConditionField.Days => "天數",
        ConditionField.Hours => "時數",
        _ => v.ToString()
    };

    public static string ToSymbol(this ConditionOperator v) => v switch
    {
        ConditionOperator.GreaterThan => ">",
        ConditionOperator.GreaterThanOrEqual => "≥",
        ConditionOperator.LessThan => "<",
        ConditionOperator.LessThanOrEqual => "≤",
        ConditionOperator.Equal => "=",
        _ => v.ToString()
    };

    public static string ToDisplay(this HolidayType v) => v switch
    {
        HolidayType.Holiday => "放假",
        HolidayType.MakeupWorkday => "補班",
        _ => v.ToString()
    };

    public static string ToDisplay(this OvertimeCompensation v) => v switch
    {
        OvertimeCompensation.Pay => "加班費",
        OvertimeCompensation.CompensatoryLeave => "補休",
        _ => v.ToString()
    };

    public static string ToDisplay(this PaymentMethod v) => v switch
    {
        PaymentMethod.BankTransfer => "匯款",
        PaymentMethod.Cash => "現金",
        PaymentMethod.PettyCash => "零用金",
        _ => v.ToString()
    };

    public static bool IsActiveFlow(this DocumentStatus v) =>
        v is DocumentStatus.InProgress or DocumentStatus.Overdue;

    public static bool IsFinal(this DocumentStatus v) =>
        v is DocumentStatus.Approved or DocumentStatus.Rejected or DocumentStatus.Withdrawn;

    public static string ToDisplay(this NotificationType v) => v switch
    {
        NotificationType.TaskAssigned => "待簽核",
        NotificationType.Approved => "已核准",
        NotificationType.Rejected => "已駁回",
        NotificationType.Returned => "已退回",
        NotificationType.Withdrawn => "已撤回",
        NotificationType.Cc => "抄送",
        NotificationType.Reminder => "時效提醒",
        NotificationType.Escalated => "逾時升級",
        NotificationType.DailyDigest => "每日摘要",
        NotificationType.System => "系統 / 留言",
        _ => v.ToString()
    };

    /// <summary>通知圖示（Bootstrap Icons）與顏色</summary>
    public static (string Icon, string Color) ToIcon(this NotificationType v) => v switch
    {
        NotificationType.TaskAssigned => ("bi-inbox", "primary"),
        NotificationType.Approved => ("bi-check-circle", "success"),
        NotificationType.Rejected => ("bi-x-circle", "danger"),
        NotificationType.Returned => ("bi-arrow-return-left", "warning"),
        NotificationType.Withdrawn => ("bi-arrow-counterclockwise", "secondary"),
        NotificationType.Cc => ("bi-envelope-paper", "info"),
        NotificationType.Reminder => ("bi-alarm", "warning"),
        NotificationType.Escalated => ("bi-arrow-up-circle", "danger"),
        NotificationType.DailyDigest => ("bi-calendar-check", "primary"),
        _ => ("bi-chat-left-text", "secondary")
    };

    public static string ToDisplay(this EmailStatus v) => v switch
    {
        EmailStatus.Pending => "待寄送",
        EmailStatus.Written => "已寫入 outbox",
        EmailStatus.Failed => "失敗",
        _ => v.ToString()
    };
}
