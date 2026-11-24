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

/// <summary>組織架構：部門樹與職稱</summary>
[Authorize(Policy = Policies.HrOrAdmin)]
public class OrganizationController(
    OrganizationService service,
    IRepository<Department> departments,
    IRepository<JobTitle> titles,
    IRepository<Employee> employees) : Controller
{
    public async Task<IActionResult> Index(string tab = "dept")
    {
        var depts = await departments.GetAllAsync();
        var emps = await employees.QueryAsync(e => e.IsActive);
        var names = (await employees.GetAllAsync()).ToDictionary(e => e.Id, e => e.Name);
        var headcount = emps.GroupBy(e => e.DepartmentId).ToDictionary(g => g.Key, g => g.Count());
        var deptNames = depts.ToDictionary(d => d.Id, d => d.Name);

        var nodes = new List<DepartmentNode>();
        void Walk(int? parentId, int depth)
        {
            foreach (var d in depts.Where(x => x.ParentId == parentId).OrderBy(x => x.SortOrder).ThenBy(x => x.Code))
            {
                nodes.Add(new DepartmentNode(d, depth,
                    d.ManagerId is int m ? names.GetValueOrDefault(m) : null,
                    headcount.GetValueOrDefault(d.Id),
                    d.ParentId is int p ? deptNames.GetValueOrDefault(p) : null));
                if (depth < 20) Walk(d.Id, depth + 1);
            }
        }
        Walk(null, 0);
        // 上層部門遺失的孤兒部門也要顯示
        nodes.AddRange(depts.Where(d => nodes.All(n => n.Department.Id != d.Id))
            .Select(d => new DepartmentNode(d, 0, d.ManagerId is int m ? names.GetValueOrDefault(m) : null, headcount.GetValueOrDefault(d.Id), null)));

        return View(new OrganizationIndexViewModel
        {
            Tab = tab == "title" ? "title" : "dept",
            Departments = nodes,
            JobTitles = (await titles.GetAllAsync()).OrderBy(t => t.SortOrder).ThenByDescending(t => t.Rank).ToList(),
            TitleHeadcount = emps.GroupBy(e => e.JobTitleId).ToDictionary(g => g.Key, g => g.Count())
        });
    }

    // ---------------- 部門 ----------------

    [HttpGet]
    public async Task<IActionResult> CreateDepartment(int? parentId) =>
        View("DepartmentEdit", await BuildDepartmentVm(new DepartmentInput { ParentId = parentId, SortOrder = await NextDeptSortAsync() }, true));

    [HttpGet]
    public async Task<IActionResult> EditDepartment(int id)
    {
        var d = await departments.GetByIdAsync(id) ?? throw new NotFoundException("部門", id);
        var input = new DepartmentInput
        {
            Id = d.Id, Version = d.Version, Code = d.Code, Name = d.Name,
            ParentId = d.ParentId, ManagerId = d.ManagerId, SortOrder = d.SortOrder
        };
        return View("DepartmentEdit", await BuildDepartmentVm(input, d.IsActive));
    }

    [HttpPost]
    public async Task<IActionResult> SaveDepartment([Bind(Prefix = "Input")] DepartmentInput input)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var saved = await service.SaveDepartmentAsync(input);
                TempData.SetSuccess($"已儲存部門「{saved.Name}」。");
                return RedirectToAction(nameof(Index));
            }
            catch (BusinessException ex) when (ex is not ForbiddenException)
            {
                ModelState.AddBusinessError(ex);
            }
        }
        var current = input.Id == 0 ? null : await departments.GetByIdAsync(input.Id);
        return View("DepartmentEdit", await BuildDepartmentVm(input, current?.IsActive ?? true));
    }

    [HttpPost]
    public async Task<IActionResult> ToggleDepartment(int id, int version, bool active)
    {
        await service.SetDepartmentActiveAsync(id, version, active);
        TempData.SetSuccess(active ? "已啟用部門。" : "已停用部門。");
        return RedirectToAction(nameof(Index));
    }

    // ---------------- 職稱 ----------------

    [HttpGet]
    public IActionResult CreateJobTitle() => View("JobTitleEdit", new JobTitleEditViewModel());

    [HttpGet]
    public async Task<IActionResult> EditJobTitle(int id)
    {
        var t = await titles.GetByIdAsync(id) ?? throw new NotFoundException("職稱", id);
        return View("JobTitleEdit", new JobTitleEditViewModel
        {
            IsActive = t.IsActive,
            Input = new JobTitleInput { Id = t.Id, Version = t.Version, Code = t.Code, Name = t.Name, Rank = t.Rank, SortOrder = t.SortOrder }
        });
    }

    [HttpPost]
    public async Task<IActionResult> SaveJobTitle([Bind(Prefix = "Input")] JobTitleInput input)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var saved = await service.SaveJobTitleAsync(input);
                TempData.SetSuccess($"已儲存職稱「{saved.Name}」。");
                return RedirectToAction(nameof(Index), new { tab = "title" });
            }
            catch (BusinessException ex) when (ex is not ForbiddenException)
            {
                ModelState.AddBusinessError(ex);
            }
        }
        var current = input.Id == 0 ? null : await titles.GetByIdAsync(input.Id);
        return View("JobTitleEdit", new JobTitleEditViewModel { Input = input, IsActive = current?.IsActive ?? true });
    }

    [HttpPost]
    public async Task<IActionResult> ToggleJobTitle(int id, int version, bool active)
    {
        await service.SetJobTitleActiveAsync(id, version, active);
        TempData.SetSuccess(active ? "已啟用職稱。" : "已停用職稱。");
        return RedirectToAction(nameof(Index), new { tab = "title" });
    }

    // ---------------- 內部 ----------------

    private async Task<int> NextDeptSortAsync() =>
        ((await departments.GetAllAsync()).Select(d => d.SortOrder).DefaultIfEmpty(0).Max()) + 1;

    private async Task<DepartmentEditViewModel> BuildDepartmentVm(DepartmentInput input, bool isActive)
    {
        var depts = await departments.GetAllAsync();
        var emps = (await employees.QueryAsync(e => e.IsActive)).OrderBy(e => e.EmployeeNo);
        var deptNames = depts.ToDictionary(d => d.Id, d => d.Name);
        return new DepartmentEditViewModel
        {
            Input = input,
            IsActive = isActive,
            Parents = depts.Where(d => d.Id != input.Id && (d.IsActive || d.Id == input.ParentId))
                .OrderBy(d => d.SortOrder)
                .Select(d => new SelectListItem($"{d.Name}（{d.Code}）", d.Id.ToString(), d.Id == input.ParentId)),
            Managers = emps.Select(e => new SelectListItem($"{e.Name}・{deptNames.GetValueOrDefault(e.DepartmentId)}（{e.EmployeeNo}）",
                e.Id.ToString(), e.Id == input.ManagerId))
        };
    }
}
