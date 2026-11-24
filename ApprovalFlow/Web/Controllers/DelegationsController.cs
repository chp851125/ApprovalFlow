using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Web.Infrastructure;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ApprovalFlow.Web.Controllers;

/// <summary>代理人設定（所有登入者；人資與管理員可管理全部）</summary>
public class DelegationsController(
    DelegationService service,
    IRepository<Delegation> delegations,
    IRepository<Employee> employees,
    IRepository<Department> departments,
    IClock clock) : Controller
{
    public async Task<IActionResult> Index(string scope = "mine", bool expired = false)
    {
        var me = User.GetUserId()!.Value;
        var today = clock.Today;
        var canAll = service.CanManageAll;
        if (scope == "all" && !canAll) scope = "mine";

        var names = (await employees.GetAllAsync()).ToDictionary(e => e.Id, e => e.Name);
        var all = await delegations.GetAllAsync();

        DelegationRow ToRow(Delegation d)
        {
            var (text, badge) =
                !d.IsEnabled ? ("已取消", "text-bg-light border") :
                d.EndDate < today ? ("已結束", "text-bg-secondary") :
                d.StartDate > today ? ("尚未開始", "text-bg-info") :
                ("生效中", "text-bg-success");
            var canCancel = d.IsEnabled && d.EndDate >= today && (d.PrincipalId == me || d.CreatedById == me || canAll);
            return new DelegationRow(d, names.GetValueOrDefault(d.PrincipalId) ?? "", names.GetValueOrDefault(d.DelegateId) ?? "",
                names.GetValueOrDefault(d.CreatedById) ?? "", text, badge, canCancel);
        }

        IEnumerable<Delegation> q = scope switch
        {
            "acting" => all.Where(d => d.DelegateId == me),
            "all" => all,
            _ => all.Where(d => d.PrincipalId == me)
        };
        if (!expired) q = q.Where(d => d.IsEnabled && d.EndDate >= today);

        return View(new DelegationIndexViewModel
        {
            Scope = scope,
            CanManageAll = canAll,
            IncludeExpired = expired,
            Rows = q.OrderByDescending(d => d.IsEnabled && d.EndDate >= today).ThenByDescending(d => d.StartDate).Select(ToRow).ToList(),
            ActingNow = all.Where(d => d.DelegateId == me && d.IsEffectiveOn(today)).Select(ToRow).ToList()
        });
    }

    [HttpGet]
    public async Task<IActionResult> Create()
    {
        var today = clock.Today;
        return View(await BuildVm(new DelegationInput
        {
            PrincipalId = User.GetUserId()!.Value,
            StartDate = today,
            EndDate = today.AddDays(4)
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([Bind(Prefix = "Input")] DelegationInput input)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var d = await service.CreateAsync(input);
                TempData.SetSuccess("已新增代理設定。期間內代理人可代為簽核，簽核紀錄會註明「代理」。");
                return RedirectToAction(nameof(Index), new { scope = d.PrincipalId == User.GetUserId() ? "mine" : "all" });
            }
            catch (BusinessException ex) when (ex is not ForbiddenException)
            {
                ModelState.AddBusinessError(ex);
            }
        }
        return View(await BuildVm(input));
    }

    [HttpPost]
    public async Task<IActionResult> Disable(int id, int version, string? scope)
    {
        await service.DisableAsync(id, version);
        TempData.SetSuccess("已取消代理設定。");
        return RedirectToAction(nameof(Index), new { scope });
    }

    private async Task<DelegationCreateViewModel> BuildVm(DelegationInput input)
    {
        var me = User.GetUserId()!.Value;
        var depts = (await departments.GetAllAsync()).ToDictionary(d => d.Id);
        var active = (await employees.QueryAsync(e => e.IsActive))
            .OrderBy(e => depts.GetValueOrDefault(e.DepartmentId)?.SortOrder).ThenBy(e => e.EmployeeNo).ToList();
        var groups = depts.Values.ToDictionary(d => d.Id, d => new SelectListGroup { Name = d.Name });
        SelectListItem Item(Employee e) => new($"{e.Name}（{e.Account}）", e.Id.ToString())
        {
            Group = groups.GetValueOrDefault(e.DepartmentId)
        };
        var me0 = active.FirstOrDefault(e => e.Id == me);
        // 預設建議：同部門同仁與主管排在前面
        var delegates = active.Where(e => e.Id != (input.PrincipalId == 0 ? me : input.PrincipalId))
            .OrderByDescending(e => me0 != null && (e.DepartmentId == me0.DepartmentId || e.Id == me0.ManagerId))
            .ThenBy(e => depts.GetValueOrDefault(e.DepartmentId)?.SortOrder).ThenBy(e => e.EmployeeNo);
        return new DelegationCreateViewModel
        {
            Input = input,
            CanManageAll = service.CanManageAll,
            MaxDays = DelegationService.MaxDays,
            Principals = active.Select(Item),
            Delegates = delegates.Select(e => new SelectListItem($"{e.Name}（{e.Account}）・{depts.GetValueOrDefault(e.DepartmentId)?.Name}", e.Id.ToString()))
        };
    }
}
