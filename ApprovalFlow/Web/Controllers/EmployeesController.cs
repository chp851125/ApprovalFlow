using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Web.Infrastructure;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ApprovalFlow.Web.Controllers;

/// <summary>員工帳號管理（人資、系統管理員）</summary>
[Authorize(Policy = Policies.HrOrAdmin)]
public class EmployeesController(
    EmployeeService service,
    IRepository<Employee> employees,
    IRepository<Department> departments,
    IRepository<JobTitle> titles,
    IRepository<Delegation> delegations,
    IRepository<AuditLog> auditLogs,
    IClock clock) : Controller
{
    private const int PageSize = 15;

    public async Task<IActionResult> Index([FromQuery] EmployeeFilter filter)
    {
        var all = await employees.GetAllAsync();
        var depts = (await departments.GetAllAsync()).ToDictionary(d => d.Id);
        var titleMap = (await titles.GetAllAsync()).ToDictionary(t => t.Id, t => t.Name);
        var names = all.ToDictionary(e => e.Id, e => e.Name);
        var now = clock.Now;
        bool Locked(Employee e) => e.LockoutEnd is { } end && end > now;

        // 部門篩選含下層部門
        HashSet<int>? deptIds = null;
        if (filter.DepartmentId is int rootId)
        {
            deptIds = [rootId];
            bool added;
            do
            {
                added = false;
                foreach (var d in depts.Values.Where(d => d.ParentId is int p && deptIds.Contains(p) && !deptIds.Contains(d.Id)))
                    added |= deptIds.Add(d.Id);
            } while (added);
        }

        IEnumerable<Employee> q = all;
        q = filter.Status switch
        {
            "inactive" => q.Where(e => !e.IsActive),
            "locked" => q.Where(e => e.IsActive && Locked(e)),
            "all" => q,
            _ => q.Where(e => e.IsActive)
        };
        if (deptIds is not null) q = q.Where(e => deptIds.Contains(e.DepartmentId));
        if (!string.IsNullOrEmpty(filter.Role) && Roles.All.Contains(filter.Role)) q = q.Where(e => e.HasRole(filter.Role));
        if (!string.IsNullOrWhiteSpace(filter.Q))
        {
            var k = filter.Q.Trim();
            q = q.Where(e => e.Name.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                             e.Account.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                             e.EmployeeNo.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                             e.Email.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                             (e.Extension ?? "").Contains(k));
        }

        var rows = q.OrderBy(e => depts.GetValueOrDefault(e.DepartmentId)?.SortOrder)
            .ThenBy(e => e.EmployeeNo)
            .Select(e => new EmployeeRow(e,
                depts.GetValueOrDefault(e.DepartmentId)?.Name ?? "",
                titleMap.GetValueOrDefault(e.JobTitleId) ?? "",
                e.ManagerId is int m ? names.GetValueOrDefault(m) : null,
                Locked(e)))
            .ToList();

        return View(new EmployeeIndexViewModel
        {
            Filter = filter,
            Rows = new PagedList<EmployeeRow>(rows, filter.Page, PageSize),
            Departments = depts.Values.OrderBy(d => d.SortOrder)
                .Select(d => new SelectListItem(d.IsActive ? d.Name : d.Name + "（停用）", d.Id.ToString(), d.Id == filter.DepartmentId)),
            ActiveCount = all.Count(e => e.IsActive),
            InactiveCount = all.Count(e => !e.IsActive),
            LockedCount = all.Count(e => e.IsActive && Locked(e))
        });
    }

    public async Task<IActionResult> Details(int id)
    {
        var e = await employees.GetByIdAsync(id) ?? throw new NotFoundException("員工", id);
        var all = await employees.GetAllAsync();
        var today = clock.Today;
        var idText = id.ToString();
        return View(new EmployeeDetailsViewModel
        {
            Employee = e,
            DepartmentName = (await departments.GetByIdAsync(e.DepartmentId))?.Name ?? "",
            TitleName = (await titles.GetByIdAsync(e.JobTitleId))?.Name ?? "",
            Manager = e.ManagerId is int m ? all.FirstOrDefault(x => x.Id == m) : null,
            DirectReports = all.Where(x => x.IsActive && x.ManagerId == id).OrderBy(x => x.EmployeeNo).ToList(),
            HeadOfDepartments = (await departments.QueryAsync(d => d.IsActive && d.ManagerId == id)).Select(d => d.Name).ToList(),
            Delegations = (await delegations.QueryAsync(d => d.PrincipalId == id || d.DelegateId == id))
                .Where(d => d.IsEnabled && d.EndDate >= today).OrderBy(d => d.StartDate).ToList(),
            Names = all.ToDictionary(x => x.Id, x => x.Name),
            RecentAudits = (await auditLogs.QueryAsync(a => a.EntityType == nameof(Employee) && a.EntityId == idText))
                .OrderByDescending(a => a.At).Take(8).ToList(),
            PendingTasks = await service.CountPendingTasksAsync(id),
            IsLocked = e.LockoutEnd is { } end && end > clock.Now,
            CanManage = service.CanManage(e),
            CanAssignAdmin = User.IsInRole(Roles.Admin),
            IsSelf = User.GetUserId() == id,
            Today = today
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create(int? departmentId) =>
        View("Edit", await BuildEditVm(new EmployeeInput
        {
            DepartmentId = departmentId ?? 0,
            HireDate = clock.Today,
            Roles = [Roles.Applicant],
            EmployeeNo = await SuggestEmployeeNoAsync()
        }));

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var e = await employees.GetByIdAsync(id) ?? throw new NotFoundException("員工", id);
        if (!service.CanManage(e)) throw new ForbiddenException("系統管理員帳號只能由系統管理員維護。");
        return View(await BuildEditVm(new EmployeeInput
        {
            Id = e.Id, Version = e.Version, EmployeeNo = e.EmployeeNo, Account = e.Account, Name = e.Name,
            Email = e.Email, Phone = e.Phone, Extension = e.Extension, DepartmentId = e.DepartmentId,
            JobTitleId = e.JobTitleId, ManagerId = e.ManagerId, HireDate = e.HireDate, Roles = e.Roles
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Save([Bind(Prefix = "Input")] EmployeeInput input)
    {
        if (ModelState.IsValid)
        {
            try
            {
                if (input.Id == 0)
                {
                    var (emp, temp) = await service.CreateAsync(input);
                    return View("PasswordIssued", new PasswordIssuedViewModel
                    {
                        EmployeeId = emp.Id, EmployeeName = emp.Name, Account = emp.Account, TempPassword = temp, IsNewAccount = true
                    });
                }
                var saved = await service.UpdateAsync(input);
                TempData.SetSuccess($"已更新「{saved.Name}」的資料。");
                return RedirectToAction(nameof(Details), new { id = saved.Id });
            }
            catch (BusinessException ex) when (ex is not ForbiddenException)
            {
                ModelState.AddBusinessError(ex);
            }
        }
        return View("Edit", await BuildEditVm(input));
    }

    [HttpPost]
    public async Task<IActionResult> UpdateRoles(int id, int version, List<string> roles)
    {
        await service.UpdateRolesAsync(id, version, roles);
        TempData.SetSuccess("已更新角色，該使用者下一次操作時立即生效。");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Deactivate(int id, int version, DateOnly? resignedDate)
    {
        await service.DeactivateAsync(id, version, resignedDate);
        TempData.SetSuccess("已停用帳號，該使用者已被登出。");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Reactivate(int id, int version)
    {
        await service.ReactivateAsync(id, version);
        TempData.SetSuccess("已復職並啟用帳號。");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> ResetPassword(int id, int version)
    {
        var temp = await service.ResetPasswordAsync(id, version);
        var e = (await employees.GetByIdAsync(id))!;
        // 不寫入 TempData / Log，只在本次回應顯示一次
        Response.Headers.CacheControl = "no-store";
        return View("PasswordIssued", new PasswordIssuedViewModel { EmployeeId = id, EmployeeName = e.Name, Account = e.Account, TempPassword = temp });
    }

    [HttpPost]
    public async Task<IActionResult> Unlock(int id, int version)
    {
        await service.UnlockAsync(id, version);
        TempData.SetSuccess("已解除登入鎖定。");
        return RedirectToAction(nameof(Details), new { id });
    }

    // ---------------- 內部 ----------------

    private async Task<string> SuggestEmployeeNoAsync()
    {
        var nos = (await employees.GetAllAsync()).Select(e => e.EmployeeNo).ToList();
        var prefix = new string((nos.FirstOrDefault() ?? "E").TakeWhile(char.IsLetter).ToArray());
        var max = nos.Where(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(n => int.TryParse(n[prefix.Length..], out var v) ? v : 0).DefaultIfEmpty(0).Max();
        return $"{prefix}{max + 1:000}";
    }

    private async Task<EmployeeEditViewModel> BuildEditVm(EmployeeInput input)
    {
        var depts = await departments.GetAllAsync();
        var deptNames = depts.ToDictionary(d => d.Id, d => d.Name);
        var current = input.Id == 0 ? null : await employees.GetByIdAsync(input.Id);
        var titleList = await titles.GetAllAsync();
        var titleNames = titleList.ToDictionary(t => t.Id, t => t.Name);
        return new EmployeeEditViewModel
        {
            Input = input,
            CanAssignAdmin = User.IsInRole(Roles.Admin),
            Departments = depts.Where(d => d.IsActive || d.Id == current?.DepartmentId).OrderBy(d => d.SortOrder)
                .Select(d => new SelectListItem(d.Name, d.Id.ToString())),
            JobTitles = titleList.Where(t => t.IsActive || t.Id == current?.JobTitleId).OrderBy(t => t.SortOrder)
                .Select(t => new SelectListItem(t.Name, t.Id.ToString())),
            Managers = (await employees.QueryAsync(e => e.IsActive && e.Id != input.Id))
                .OrderBy(e => depts.FirstOrDefault(d => d.Id == e.DepartmentId)?.SortOrder).ThenBy(e => e.EmployeeNo)
                .Select(e => new SelectListItem($"{e.Name}・{deptNames.GetValueOrDefault(e.DepartmentId)} {titleNames.GetValueOrDefault(e.JobTitleId)}", e.Id.ToString()))
        };
    }
}
