using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Workflow;

/// <summary>組織資料快照（純記憶體資料，供流程運算使用，不依賴儲存實作）</summary>
public sealed class OrgSnapshot
{
    private readonly Dictionary<int, Employee> _employees;
    private readonly Dictionary<int, Department> _departments;

    public OrgSnapshot(IEnumerable<Employee> employees, IEnumerable<Department> departments)
    {
        _employees = employees.ToDictionary(e => e.Id);
        _departments = departments.ToDictionary(d => d.Id);
    }

    public Employee? Employee(int? id) => id is int i && _employees.TryGetValue(i, out var e) ? e : null;
    public Department? Department(int? id) => id is int i && _departments.TryGetValue(i, out var d) ? d : null;
    public IEnumerable<Employee> Employees => _employees.Values;
    public IEnumerable<Department> Departments => _departments.Values;

    public bool IsActive(int id) => Employee(id)?.IsActive == true;

    /// <summary>取得有效的直屬主管；若主管已停用則往上找</summary>
    public Employee? ActiveManagerOf(int employeeId)
    {
        var current = Employee(employeeId);
        var guard = 0;
        while (current?.ManagerId is int mid && guard++ < 20)
        {
            var mgr = Employee(mid);
            if (mgr is null) return null;
            if (mgr.IsActive) return mgr;
            current = mgr;
        }
        return null;
    }
}

/// <summary>依關卡定義解析簽核人</summary>
public static class ApproverResolver
{
    public static List<int> Resolve(WorkflowStepDefinition def, Employee applicant, OrgSnapshot org)
    {
        IEnumerable<int> ids = def.ApproverSource switch
        {
            ApproverSource.DirectManager => One(org.ActiveManagerOf(applicant.Id)?.Id),
            ApproverSource.DepartmentManager => One(DepartmentManager(applicant, org)),
            ApproverSource.Role => org.Employees
                .Where(e => e.IsActive && def.RoleName != null && e.HasRole(def.RoleName))
                .OrderBy(e => e.EmployeeNo).Select(e => e.Id),
            ApproverSource.SpecificUser => def.UserIds.Where(org.IsActive),
            ApproverSource.ManagerLevelN => One(ManagerLevelN(applicant, Math.Max(1, def.Levels), org)),
            _ => []
        };
        return ids.Where(id => id != applicant.Id).Distinct().ToList();
    }

    private static IEnumerable<int> One(int? id) => id is int i ? [i] : [];

    /// <summary>申請人所屬部門主管；若申請人本身即部門主管，改找上層部門主管</summary>
    private static int? DepartmentManager(Employee applicant, OrgSnapshot org)
    {
        var dept = org.Department(applicant.DepartmentId);
        var guard = 0;
        while (dept != null && guard++ < 20)
        {
            if (dept.ManagerId is int m && m != applicant.Id && org.IsActive(m)) return m;
            dept = org.Department(dept.ParentId);
        }
        return null;
    }

    /// <summary>往上 N 級主管；組織層級不足時取最高可到達者</summary>
    private static int? ManagerLevelN(Employee applicant, int levels, OrgSnapshot org)
    {
        int? result = null;
        var currentId = applicant.Id;
        for (var i = 0; i < levels; i++)
        {
            var mgr = org.ActiveManagerOf(currentId);
            if (mgr is null) break;
            result = mgr.Id;
            currentId = mgr.Id;
        }
        return result;
    }
}
