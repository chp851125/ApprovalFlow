using System.ComponentModel.DataAnnotations;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Inputs;

// 管理功能的輸入模型。Id = 0 代表新增；Version 用於樂觀並行控制。

public class DepartmentInput
{
    public int Id { get; set; }
    public int Version { get; set; }

    [Required(ErrorMessage = "請輸入部門代碼")]
    [RegularExpression("^[A-Za-z0-9_-]{2,10}$", ErrorMessage = "部門代碼為 2~10 碼英數字")]
    [Display(Name = "部門代碼")]
    public string Code { get; set; } = "";

    [Required(ErrorMessage = "請輸入部門名稱")]
    [StringLength(30, ErrorMessage = "部門名稱最多 30 個字")]
    [Display(Name = "部門名稱")]
    public string Name { get; set; } = "";

    [Display(Name = "上層部門")]
    public int? ParentId { get; set; }

    [Display(Name = "部門主管")]
    public int? ManagerId { get; set; }

    [Range(0, 999, ErrorMessage = "排序需介於 0~999")]
    [Display(Name = "排序")]
    public int SortOrder { get; set; }
}

public class JobTitleInput
{
    public int Id { get; set; }
    public int Version { get; set; }

    [Required(ErrorMessage = "請輸入職稱代碼")]
    [RegularExpression("^[A-Za-z0-9_-]{2,10}$", ErrorMessage = "職稱代碼為 2~10 碼英數字")]
    [Display(Name = "職稱代碼")]
    public string Code { get; set; } = "";

    [Required(ErrorMessage = "請輸入職稱名稱")]
    [StringLength(20, ErrorMessage = "職稱名稱最多 20 個字")]
    [Display(Name = "職稱名稱")]
    public string Name { get; set; } = "";

    [Range(1, 99, ErrorMessage = "職等需介於 1~99")]
    [Display(Name = "職等（數字越大越高）")]
    public int Rank { get; set; } = 1;

    [Range(0, 999, ErrorMessage = "排序需介於 0~999")]
    [Display(Name = "排序")]
    public int SortOrder { get; set; }
}

public class EmployeeInput
{
    public int Id { get; set; }
    public int Version { get; set; }

    [Required(ErrorMessage = "請輸入員工編號")]
    [RegularExpression("^[A-Za-z0-9-]{2,12}$", ErrorMessage = "員工編號為 2~12 碼英數字")]
    [Display(Name = "員工編號")]
    public string EmployeeNo { get; set; } = "";

    [Required(ErrorMessage = "請輸入登入帳號")]
    [RegularExpression("^[A-Za-z0-9._-]{2,30}$", ErrorMessage = "帳號為 2~30 碼英數字，可含 . _ -")]
    [Display(Name = "登入帳號")]
    public string Account { get; set; } = "";

    [Required(ErrorMessage = "請輸入姓名")]
    [StringLength(20, ErrorMessage = "姓名最多 20 個字")]
    [Display(Name = "姓名")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "請輸入 Email")]
    [EmailAddress(ErrorMessage = "Email 格式不正確")]
    [StringLength(100)]
    [Display(Name = "Email")]
    public string Email { get; set; } = "";

    [StringLength(20)]
    [RegularExpression(@"^[0-9+\-() ]*$", ErrorMessage = "電話只能包含數字與 + - ( )")]
    [Display(Name = "手機")]
    public string? Phone { get; set; }

    [StringLength(10)]
    [RegularExpression("^[0-9]*$", ErrorMessage = "分機只能是數字")]
    [Display(Name = "分機")]
    public string? Extension { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "請選擇部門")]
    [Display(Name = "部門")]
    public int DepartmentId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "請選擇職稱")]
    [Display(Name = "職稱")]
    public int JobTitleId { get; set; }

    [Display(Name = "直屬主管")]
    public int? ManagerId { get; set; }

    [Required(ErrorMessage = "請輸入到職日")]
    [Display(Name = "到職日")]
    public DateOnly? HireDate { get; set; }

    /// <summary>僅新增時使用；編輯時角色由「角色設定」另行處理</summary>
    [Display(Name = "角色")]
    public List<string> Roles { get; set; } = [];
}

public class DelegationInput
{
    [Display(Name = "被代理人")]
    public int PrincipalId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "請選擇代理人")]
    [Display(Name = "代理人")]
    public int DelegateId { get; set; }

    [Required(ErrorMessage = "請選擇開始日期")]
    [Display(Name = "開始日期")]
    public DateOnly? StartDate { get; set; }

    [Required(ErrorMessage = "請選擇結束日期")]
    [Display(Name = "結束日期")]
    public DateOnly? EndDate { get; set; }

    /// <summary>空白代表全部單別</summary>
    [Display(Name = "適用單別")]
    public List<DocumentType> DocumentTypes { get; set; } = [];

    [Required(ErrorMessage = "請輸入代理原因")]
    [StringLength(100, ErrorMessage = "代理原因最多 100 個字")]
    [Display(Name = "代理原因")]
    public string Reason { get; set; } = "";
}

public class LeaveTypeInput
{
    public int Id { get; set; }
    public int Version { get; set; }

    [Required(ErrorMessage = "請輸入假別代碼")]
    [RegularExpression("^[A-Za-z0-9_]{2,20}$", ErrorMessage = "假別代碼為 2~20 碼英數字")]
    [Display(Name = "假別代碼")]
    public string Code { get; set; } = "";

    [Required(ErrorMessage = "請輸入假別名稱")]
    [StringLength(20, ErrorMessage = "假別名稱最多 20 個字")]
    [Display(Name = "假別名稱")]
    public string Name { get; set; } = "";

    [Range(0, 2000, ErrorMessage = "年度額度需介於 0~2000 小時")]
    [Display(Name = "每年額度（小時，空白代表不限）")]
    public decimal? DefaultQuotaHours { get; set; }

    [Display(Name = "依年資計算額度（特休）")]
    public bool UseSeniorityQuota { get; set; }

    [Range(0, 1, ErrorMessage = "給薪比例需介於 0~1")]
    [Display(Name = "給薪比例（0~1）")]
    public decimal PayRatio { get; set; } = 1m;

    [Range(0.5, 8, ErrorMessage = "最小單位需介於 0.5~8 小時")]
    [Display(Name = "最小請假單位（小時）")]
    public decimal MinUnitHours { get; set; } = 1m;

    [Display(Name = "需附證明文件")]
    public bool RequiresAttachment { get; set; }

    [StringLength(200, ErrorMessage = "說明最多 200 個字")]
    [Display(Name = "說明")]
    public string? Description { get; set; }

    [Range(0, 999, ErrorMessage = "排序需介於 0~999")]
    [Display(Name = "排序")]
    public int SortOrder { get; set; }

    [Display(Name = "啟用")]
    public bool IsActive { get; set; } = true;
}

public class ExpenseCategoryInput
{
    public int Id { get; set; }
    public int Version { get; set; }

    [Required(ErrorMessage = "請輸入類別代碼")]
    [RegularExpression("^[A-Za-z0-9_]{2,20}$", ErrorMessage = "類別代碼為 2~20 碼英數字")]
    [Display(Name = "類別代碼")]
    public string Code { get; set; } = "";

    [Required(ErrorMessage = "請輸入類別名稱")]
    [StringLength(20, ErrorMessage = "類別名稱最多 20 個字")]
    [Display(Name = "類別名稱")]
    public string Name { get; set; } = "";

    [Range(1, int.MaxValue, ErrorMessage = "請選擇預算科目")]
    [Display(Name = "預算科目")]
    public int BudgetSubjectId { get; set; }

    [Range(1, 10_000_000, ErrorMessage = "單筆上限需介於 1~10,000,000")]
    [Display(Name = "單筆上限（元，空白代表不限）")]
    public decimal? MaxAmountPerItem { get; set; }

    [Display(Name = "需附收據")]
    public bool RequiresReceipt { get; set; } = true;

    [StringLength(200, ErrorMessage = "說明最多 200 個字")]
    [Display(Name = "說明")]
    public string? Description { get; set; }

    [Range(0, 999, ErrorMessage = "排序需介於 0~999")]
    [Display(Name = "排序")]
    public int SortOrder { get; set; }

    [Display(Name = "啟用")]
    public bool IsActive { get; set; } = true;
}

public class HolidayInput
{
    public int Id { get; set; }
    public int Version { get; set; }

    [Required(ErrorMessage = "請選擇日期")]
    [Display(Name = "日期")]
    public DateOnly? Date { get; set; }

    [Required(ErrorMessage = "請輸入名稱")]
    [StringLength(30, ErrorMessage = "名稱最多 30 個字")]
    [Display(Name = "名稱")]
    public string Name { get; set; } = "";

    [Display(Name = "類型")]
    public HolidayType Type { get; set; } = HolidayType.Holiday;
}

public class SettingChange
{
    public int Id { get; set; }
    public int Version { get; set; }
    public string? Value { get; set; }
}

public class ChangePasswordInput
{
    [Required(ErrorMessage = "請輸入目前密碼")]
    [DataType(DataType.Password)]
    [Display(Name = "目前密碼")]
    public string CurrentPassword { get; set; } = "";

    [Required(ErrorMessage = "請輸入新密碼")]
    [DataType(DataType.Password)]
    [StringLength(100)]
    [Display(Name = "新密碼")]
    public string NewPassword { get; set; } = "";

    [Required(ErrorMessage = "請再次輸入新密碼")]
    [DataType(DataType.Password)]
    [Compare(nameof(NewPassword), ErrorMessage = "兩次輸入的新密碼不一致")]
    [Display(Name = "確認新密碼")]
    public string ConfirmPassword { get; set; } = "";
}
