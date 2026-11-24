using ApprovalFlow.Domain.Common;

namespace ApprovalFlow.Domain.Entities;

/// <summary>部門（樹狀，以 ParentId 串接）</summary>
public class Department : EntityBase
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int? ParentId { get; set; }
    /// <summary>部門主管（員工 Id）</summary>
    public int? ManagerId { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>職稱</summary>
public class JobTitle : EntityBase
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>職等，數字越大職位越高</summary>
    public int Rank { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>員工（同時也是系統使用者）</summary>
public class Employee : EntityBase
{
    public string EmployeeNo { get; set; } = "";
    /// <summary>登入帳號</summary>
    public string Account { get; set; } = "";
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string? Phone { get; set; }
    public string? Extension { get; set; }
    public int DepartmentId { get; set; }
    public int JobTitleId { get; set; }
    /// <summary>直屬主管（員工 Id）</summary>
    public int? ManagerId { get; set; }
    public List<string> Roles { get; set; } = [];
    public DateOnly HireDate { get; set; }
    public DateOnly? ResignedDate { get; set; }
    public bool IsActive { get; set; } = true;

    // ---- 安全性 ----
    public string PasswordHash { get; set; } = "";
    /// <summary>變更密碼、停用、角色異動時更新，使既有登入 Cookie 失效</summary>
    public string SecurityStamp { get; set; } = Guid.NewGuid().ToString("N");
    public bool MustChangePassword { get; set; }
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }

    public bool HasRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);
}

/// <summary>代理人設定：起訖日期內，Delegate 可代 Principal 簽核</summary>
public class Delegation : EntityBase
{
    /// <summary>被代理人</summary>
    public int PrincipalId { get; set; }
    /// <summary>代理人</summary>
    public int DelegateId { get; set; }
    public DateOnly StartDate { get; set; }
    public DateOnly EndDate { get; set; }
    /// <summary>適用單別；空集合代表全部單別</summary>
    public List<Enums.DocumentType> DocumentTypes { get; set; } = [];
    public string Reason { get; set; } = "";
    public bool IsEnabled { get; set; } = true;
    public int CreatedById { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public bool IsEffectiveOn(DateOnly date, Enums.DocumentType? type = null) =>
        IsEnabled && date >= StartDate && date <= EndDate &&
        (type is null || DocumentTypes.Count == 0 || DocumentTypes.Contains(type.Value));
}
