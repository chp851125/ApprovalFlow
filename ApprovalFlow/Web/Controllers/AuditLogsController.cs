using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Reports;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Web.Infrastructure;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ApprovalFlow.Web.Controllers;

/// <summary>稽核紀錄（系統管理員）</summary>
[Authorize(Policy = Policies.Admin)]
public class AuditLogsController(AuditQueryService query, IExcelExporter excel, AuditService audit, IClock clock) : Controller
{
    private const int PageSize = 30;

    public async Task<IActionResult> Index([FromQuery] AuditFilter filter)
    {
        filter.From ??= clock.Today.AddDays(-30);
        var rows = await query.QueryAsync(filter);
        var (actions, entities) = await query.OptionsAsync();
        return View(new AuditIndexViewModel
        {
            Filter = filter,
            Rows = new PagedList<AuditLog>(rows, filter.Page, PageSize),
            Actions = actions,
            EntityTypes = entities
        });
    }

    public async Task<IActionResult> Export([FromQuery] AuditFilter filter)
    {
        var rows = await query.QueryAsync(filter);
        var bytes = excel.Export([AuditQueryService.ToTable(rows, filter)]);
        await audit.LogAsync("Export", nameof(AuditLog), null, $"匯出稽核紀錄 {Math.Min(rows.Count, ReportService.MaxRows)} 筆");
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"稽核紀錄_{clock.Now:yyyyMMdd_HHmm}.xlsx");
    }
}
