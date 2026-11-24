using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace ApprovalFlow.Web.Infrastructure;

/// <summary>Development 專用：快速切換使用者下拉選單</summary>
public sealed class QuickSwitchViewComponent(
    IWebHostEnvironment env,
    IRepository<Employee> employees,
    IRepository<Department> departments,
    IRepository<JobTitle> titles) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(string variant = "navbar")
    {
        if (!env.IsDevelopment()) return Content(string.Empty);

        var depts = (await departments.GetAllAsync()).ToDictionary(d => d.Id);
        var titleMap = (await titles.GetAllAsync()).ToDictionary(t => t.Id, t => t.Name);
        var items = (await employees.QueryAsync(e => e.IsActive))
            .OrderBy(e => depts.GetValueOrDefault(e.DepartmentId)?.SortOrder)
            .ThenByDescending(e => e.ManagerId is null ? 1 : 0)
            .ThenBy(e => e.EmployeeNo)
            .Select(e =>
            {
                var roles = e.Roles.Where(r => r != Roles.Applicant).Select(Roles.ToDisplay).ToList();
                return new QuickSwitchItem
                {
                    Id = e.Id,
                    Group = depts.GetValueOrDefault(e.DepartmentId)?.Name ?? "",
                    Label = $"{e.Name}｜{titleMap.GetValueOrDefault(e.JobTitleId)}｜{e.Account}" + (roles.Count > 0 ? $"（{string.Join("、", roles)}）" : "")
                };
            }).ToList();

        ViewData["Variant"] = variant;
        ViewData["CurrentId"] = HttpContext.User.GetUserId();
        return View(items);
    }
}
