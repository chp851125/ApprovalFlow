using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Reports;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ApprovalFlow.Web.Controllers;

/// <summary>報表與 Excel 匯出（系統管理員、人資、財務；各報表權限由 ReportService 判斷）</summary>
[Authorize(Roles = $"{Roles.Admin},{Roles.HR},{Roles.Finance}")]
public class ReportsController(
    ReportService reports,
    IExcelExporter excel,
    AuditService audit,
    IRepository<Department> departments,
    IClock clock) : Controller
{
    private const int PreviewRows = 200;

    public async Task<IActionResult> Index([FromQuery] ReportFilter filter)
    {
        var available = reports.Available();
        if (available.All(r => r.Key != filter.Report)) filter.Report = available.FirstOrDefault()?.Key ?? "documents";
        filter.Year ??= clock.Today.Year;
        filter.From ??= clock.Today.AddMonths(-3);
        filter.To ??= clock.Today;
        var table = await reports.BuildAsync(filter);
        return View(new ReportIndexViewModel
        {
            Filter = filter,
            Reports = available,
            Current = available.First(r => r.Key == filter.Report),
            Table = table,
            PreviewRows = PreviewRows,
            AllowedTypes = reports.AllowedTypes(),
            Departments = (await departments.GetAllAsync()).OrderBy(d => d.SortOrder)
                .Select(d => new SelectListItem(d.Name, d.Id.ToString(), d.Id == filter.DepartmentId)),
            Years = Enumerable.Range(clock.Today.Year - 2, 3).Reverse().ToList()
        });
    }

    public async Task<IActionResult> Export([FromQuery] ReportFilter filter)
    {
        var table = await reports.BuildAsync(filter);
        var bytes = excel.Export([table]);
        await audit.LogAsync("Export", "Report", filter.Report, $"匯出報表「{table.Title}」（{table.Rows.Count} 筆）");
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            $"{table.SheetName}_{clock.Now:yyyyMMdd_HHmm}.xlsx");
    }
}
