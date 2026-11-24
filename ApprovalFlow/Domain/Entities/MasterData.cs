using ApprovalFlow.Domain.Common;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Domain.Entities;

/// <summary>假別</summary>
public class LeaveType : EntityBase
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>每年預設額度（小時）；null 代表不設上限</summary>
    public decimal? DefaultQuotaHours { get; set; }
    /// <summary>依年資計算額度（特休）</summary>
    public bool UseSeniorityQuota { get; set; }
    /// <summary>給薪比例 0~1</summary>
    public decimal PayRatio { get; set; } = 1m;
    /// <summary>最小請假單位（小時）</summary>
    public decimal MinUnitHours { get; set; } = 1m;
    public bool RequiresAttachment { get; set; }
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>員工每年各假別可用額度</summary>
public class LeaveBalance : EntityBase
{
    public int EmployeeId { get; set; }
    public int LeaveTypeId { get; set; }
    public int Year { get; set; }
    public decimal EntitledHours { get; set; }
    /// <summary>系統上線前已使用時數（期初）</summary>
    public decimal OpeningUsedHours { get; set; }
}

/// <summary>費用類別</summary>
public class ExpenseCategory : EntityBase
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int BudgetSubjectId { get; set; }
    /// <summary>單筆上限；null 代表不限</summary>
    public decimal? MaxAmountPerItem { get; set; }
    public bool RequiresReceipt { get; set; } = true;
    public string? Description { get; set; }
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>預算科目</summary>
public class BudgetSubject : EntityBase
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int Year { get; set; }
    public decimal AnnualBudget { get; set; }
    public int? OwnerDepartmentId { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>供應商</summary>
public class Supplier : EntityBase
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>統一編號</summary>
    public string TaxId { get; set; } = "";
    public string ContactName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Email { get; set; } = "";
    public string Address { get; set; } = "";
    public string Category { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

/// <summary>行事曆特殊日（放假或補班）；一般週末由程式判斷</summary>
public class Holiday : EntityBase
{
    public DateOnly Date { get; set; }
    public string Name { get; set; } = "";
    public HolidayType Type { get; set; }
}

/// <summary>系統參數</summary>
public class SystemSetting : EntityBase
{
    public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public string Group { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>string / int / bool / decimal</summary>
    public string ValueType { get; set; } = "string";
}

/// <summary>單別設定（名稱、單號規則、是否可批次核准）</summary>
public class DocumentTypeConfig : EntityBase
{
    public DocumentType DocumentType { get; set; }
    public string Name { get; set; } = "";
    /// <summary>單號前綴，例：LV</summary>
    public string Prefix { get; set; } = "";
    /// <summary>單號格式；支援 {prefix}、{yyyy}、{MM}、{yyyyMM}、{seq:0000}</summary>
    public string NumberFormat { get; set; } = "{prefix}-{yyyyMM}-{seq:0000}";
    /// <summary>流水號重置週期：Monthly / Yearly</summary>
    public string ResetPeriod { get; set; } = "Monthly";
    /// <summary>低風險單別，可批次核准</summary>
    public bool AllowBatchApprove { get; set; }
    public bool IsEnabled { get; set; } = true;
    public string? Icon { get; set; }
    public string? Description { get; set; }
}

/// <summary>單號流水號</summary>
public class DocumentSequence : EntityBase
{
    public string Prefix { get; set; } = "";
    /// <summary>期別，例 202611 或 2026</summary>
    public string Period { get; set; } = "";
    public int LastNumber { get; set; }
}
