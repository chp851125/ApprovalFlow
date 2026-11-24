namespace ApprovalFlow.Domain.Constants;

/// <summary>系統角色代碼（角色是系統功能定義，非 Mock 資料）。</summary>
public static class Roles
{
    public const string Applicant = "Applicant";
    public const string Manager = "Manager";
    public const string DeptManager = "DeptManager";
    public const string Finance = "Finance";
    public const string HR = "HR";
    public const string Admin = "Admin";

    public static readonly IReadOnlyList<string> All =
        [Applicant, Manager, DeptManager, Finance, HR, Admin];

    public static string ToDisplay(string role) => role switch
    {
        Applicant => "申請人",
        Manager => "主管",
        DeptManager => "部門經理",
        Finance => "財務",
        HR => "人資",
        Admin => "系統管理員",
        _ => role
    };
}

/// <summary>授權原則名稱</summary>
public static class Policies
{
    public const string Admin = "Policy.Admin";
    public const string HrOrAdmin = "Policy.HrOrAdmin";
    public const string FinanceOrAdmin = "Policy.FinanceOrAdmin";
    public const string ManagerOrAbove = "Policy.ManagerOrAbove";
}
