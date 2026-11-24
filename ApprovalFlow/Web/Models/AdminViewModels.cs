using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ApprovalFlow.Web.Models;

// ---------------- 組織 ----------------

public sealed record DepartmentNode(Department Department, int Depth, string? ManagerName, int Headcount, string? ParentName);

public class OrganizationIndexViewModel
{
    public IReadOnlyList<DepartmentNode> Departments { get; set; } = [];
    public IReadOnlyList<JobTitle> JobTitles { get; set; } = [];
    public IReadOnlyDictionary<int, int> TitleHeadcount { get; set; } = new Dictionary<int, int>();
    public string Tab { get; set; } = "dept";
}

public class DepartmentEditViewModel
{
    public DepartmentInput Input { get; set; } = new();
    public bool IsActive { get; set; } = true;
    public IEnumerable<SelectListItem> Parents { get; set; } = [];
    public IEnumerable<SelectListItem> Managers { get; set; } = [];
}

public class JobTitleEditViewModel
{
    public JobTitleInput Input { get; set; } = new();
    public bool IsActive { get; set; } = true;
}

// ---------------- 員工 ----------------

public class EmployeeFilter
{
    public string? Q { get; set; }
    public int? DepartmentId { get; set; }
    public string? Role { get; set; }
    /// <summary>active / inactive / locked / all</summary>
    public string Status { get; set; } = "active";
    public int Page { get; set; } = 1;

    public IDictionary<string, string?> ToRouteValues() => new Dictionary<string, string?>
    {
        ["q"] = Q, ["departmentId"] = DepartmentId?.ToString(), ["role"] = Role, ["status"] = Status
    };
}

public sealed record EmployeeRow(Employee Employee, string DepartmentName, string TitleName, string? ManagerName, bool IsLocked);

public class EmployeeIndexViewModel
{
    public EmployeeFilter Filter { get; set; } = new();
    public PagedList<EmployeeRow> Rows { get; set; } = new([], 1, 20);
    public IEnumerable<SelectListItem> Departments { get; set; } = [];
    public int ActiveCount { get; set; }
    public int InactiveCount { get; set; }
    public int LockedCount { get; set; }
    public PagerModel Pager => new(Rows.Page, Rows.TotalPages, Rows.TotalCount, Rows.FirstItem, Rows.LastItem, "Index", Filter.ToRouteValues());
}

public class EmployeeEditViewModel
{
    public EmployeeInput Input { get; set; } = new();
    public IEnumerable<SelectListItem> Departments { get; set; } = [];
    public IEnumerable<SelectListItem> JobTitles { get; set; } = [];
    public IEnumerable<SelectListItem> Managers { get; set; } = [];
    public bool CanAssignAdmin { get; set; }
    public bool IsNew => Input.Id == 0;
}

public class EmployeeDetailsViewModel
{
    public Employee Employee { get; set; } = new();
    public string DepartmentName { get; set; } = "";
    public string TitleName { get; set; } = "";
    public Employee? Manager { get; set; }
    public IReadOnlyList<Employee> DirectReports { get; set; } = [];
    public IReadOnlyList<string> HeadOfDepartments { get; set; } = [];
    public IReadOnlyList<Delegation> Delegations { get; set; } = [];
    public IReadOnlyDictionary<int, string> Names { get; set; } = new Dictionary<int, string>();
    public IReadOnlyList<AuditLog> RecentAudits { get; set; } = [];
    public int PendingTasks { get; set; }
    public bool IsLocked { get; set; }
    public bool CanManage { get; set; }
    public bool CanAssignAdmin { get; set; }
    public bool IsSelf { get; set; }
    public DateOnly Today { get; set; }
}

public class PasswordIssuedViewModel
{
    public int EmployeeId { get; set; }
    public string EmployeeName { get; set; } = "";
    public string Account { get; set; } = "";
    public string TempPassword { get; set; } = "";
    public bool IsNewAccount { get; set; }
}

// ---------------- 代理 ----------------

public sealed record DelegationRow(Delegation Delegation, string PrincipalName, string DelegateName, string CreatedByName, string StatusText, string StatusBadge, bool CanCancel);

public class DelegationIndexViewModel
{
    /// <summary>mine / acting / all</summary>
    public string Scope { get; set; } = "mine";
    public bool CanManageAll { get; set; }
    public bool IncludeExpired { get; set; }
    public IReadOnlyList<DelegationRow> Rows { get; set; } = [];
    public IReadOnlyList<DelegationRow> ActingNow { get; set; } = [];
}

public class DelegationCreateViewModel
{
    public DelegationInput Input { get; set; } = new();
    public bool CanManageAll { get; set; }
    public IEnumerable<SelectListItem> Principals { get; set; } = [];
    public IEnumerable<SelectListItem> Delegates { get; set; } = [];
    public int MaxDays { get; set; }
}

// ---------------- 基本資料 ----------------

public class LeaveTypeEditViewModel
{
    public LeaveTypeInput Input { get; set; } = new();
}

public class ExpenseCategoryEditViewModel
{
    public ExpenseCategoryInput Input { get; set; } = new();
    public IEnumerable<SelectListItem> BudgetSubjects { get; set; } = [];
}

public sealed record ExpenseCategoryRow(ExpenseCategory Category, string BudgetSubjectText);

public class HolidayIndexViewModel
{
    public int Year { get; set; }
    public IReadOnlyList<int> Years { get; set; } = [];
    public IReadOnlyList<Holiday> Holidays { get; set; } = [];
    public HolidayInput NewHoliday { get; set; } = new();
    public int WorkdayCount { get; set; }
}

public class HolidayEditViewModel
{
    public HolidayInput Input { get; set; } = new();
}

public class SettingsIndexViewModel
{
    public IReadOnlyList<IGrouping<string, SystemSetting>> Groups { get; set; } = [];
    public string? ErrorKey { get; set; }
}

public class ChangePasswordViewModel
{
    public ChangePasswordInput Input { get; set; } = new();
    public bool IsForced { get; set; }
    public string PolicyText { get; set; } = "";
}

public sealed record RoleCheckboxModel(string FieldName, IReadOnlyCollection<string> Selected, bool CanAssignAdmin);
