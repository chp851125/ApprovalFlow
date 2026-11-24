using System.Text.Json;
using ApprovalFlow.Application.Reports;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace ApprovalFlow.Web.Controllers;

/// <summary>儀表板：系統管理員看全公司，其他人看個人（管理員也可切換到個人）</summary>
public class DashboardController(DashboardService dashboard) : Controller
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IActionResult> Index(string? view)
    {
        var isAdmin = User.IsInRole(Roles.Admin);
        var company = isAdmin && view != "me";
        var vm = new DashboardViewModel { IsAdmin = isAdmin, ShowCompany = company };
        if (company)
        {
            vm.Company = await dashboard.GetCompanyAsync();
            // 預設編碼器會跳脫 < > & 等字元，可安全放進 <script type="application/json">
            vm.ChartJson = JsonSerializer.Serialize(new
            {
                monthly = vm.Company.MonthlyByType,
                status = vm.Company.StatusDistribution,
                cycle = vm.Company.AvgCycleByType,
                expense = vm.Company.ExpenseByCategory,
                leave = vm.Company.LeaveHoursByType,
                dept = vm.Company.DepartmentVolume
            }, JsonOptions);
        }
        else
        {
            vm.Personal = await dashboard.GetPersonalAsync();
            vm.ChartJson = JsonSerializer.Serialize(new
            {
                monthly = vm.Personal.MonthlyApplications,
                status = vm.Personal.StatusDistribution,
                leaveBalance = vm.Personal.LeaveBalance,
                handled = vm.Personal.MyHandledMonthly
            }, JsonOptions);
        }
        return View(vm);
    }
}
