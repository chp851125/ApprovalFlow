using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Application.Common;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Web.Infrastructure;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ApprovalFlow.Web.Controllers;

/// <summary>假別維護（人資、系統管理員）</summary>
[Authorize(Policy = Policies.HrOrAdmin)]
public class LeaveTypesController(MasterDataService service, IRepository<LeaveType> repo) : Controller
{
    public async Task<IActionResult> Index() =>
        View((await repo.GetAllAsync()).OrderBy(x => x.SortOrder).ThenBy(x => x.Code).ToList());

    [HttpGet]
    public async Task<IActionResult> Create() =>
        View("Edit", new LeaveTypeEditViewModel
        {
            Input = new LeaveTypeInput { SortOrder = (await repo.GetAllAsync()).Select(x => x.SortOrder).DefaultIfEmpty(0).Max() + 1 }
        });

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var x = await repo.GetByIdAsync(id) ?? throw new NotFoundException("假別", id);
        return View(new LeaveTypeEditViewModel
        {
            Input = new LeaveTypeInput
            {
                Id = x.Id, Version = x.Version, Code = x.Code, Name = x.Name, DefaultQuotaHours = x.DefaultQuotaHours,
                UseSeniorityQuota = x.UseSeniorityQuota, PayRatio = x.PayRatio, MinUnitHours = x.MinUnitHours,
                RequiresAttachment = x.RequiresAttachment, Description = x.Description, SortOrder = x.SortOrder, IsActive = x.IsActive
            }
        });
    }

    [HttpPost]
    public async Task<IActionResult> Save([Bind(Prefix = "Input")] LeaveTypeInput input)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var saved = await service.SaveLeaveTypeAsync(input);
                TempData.SetSuccess($"已儲存假別「{saved.Name}」。");
                return RedirectToAction(nameof(Index));
            }
            catch (BusinessException ex) when (ex is not ForbiddenException)
            {
                ModelState.AddBusinessError(ex);
            }
        }
        return View("Edit", new LeaveTypeEditViewModel { Input = input });
    }
}

/// <summary>費用類別維護（財務、系統管理員）</summary>
[Authorize(Policy = Policies.FinanceOrAdmin)]
public class ExpenseCategoriesController(
    MasterDataService service,
    IRepository<ExpenseCategory> repo,
    IRepository<BudgetSubject> subjects) : Controller
{
    public async Task<IActionResult> Index()
    {
        var subjectMap = (await subjects.GetAllAsync()).ToDictionary(s => s.Id);
        var rows = (await repo.GetAllAsync()).OrderBy(x => x.SortOrder).ThenBy(x => x.Code)
            .Select(x => new ExpenseCategoryRow(x, subjectMap.TryGetValue(x.BudgetSubjectId, out var s) ? $"{s.Code} {s.Name}" : "—"))
            .ToList();
        return View(rows);
    }

    [HttpGet]
    public async Task<IActionResult> Create() =>
        View("Edit", await BuildVm(new ExpenseCategoryInput
        {
            SortOrder = (await repo.GetAllAsync()).Select(x => x.SortOrder).DefaultIfEmpty(0).Max() + 1
        }));

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var x = await repo.GetByIdAsync(id) ?? throw new NotFoundException("費用類別", id);
        return View(await BuildVm(new ExpenseCategoryInput
        {
            Id = x.Id, Version = x.Version, Code = x.Code, Name = x.Name, BudgetSubjectId = x.BudgetSubjectId,
            MaxAmountPerItem = x.MaxAmountPerItem, RequiresReceipt = x.RequiresReceipt, Description = x.Description,
            SortOrder = x.SortOrder, IsActive = x.IsActive
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Save([Bind(Prefix = "Input")] ExpenseCategoryInput input)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var saved = await service.SaveExpenseCategoryAsync(input);
                TempData.SetSuccess($"已儲存費用類別「{saved.Name}」。");
                return RedirectToAction(nameof(Index));
            }
            catch (BusinessException ex) when (ex is not ForbiddenException)
            {
                ModelState.AddBusinessError(ex);
            }
        }
        return View("Edit", await BuildVm(input));
    }

    private async Task<ExpenseCategoryEditViewModel> BuildVm(ExpenseCategoryInput input) => new()
    {
        Input = input,
        BudgetSubjects = (await subjects.GetAllAsync())
            .Where(s => s.IsActive || s.Id == input.BudgetSubjectId)
            .OrderBy(s => s.Code)
            .Select(s => new SelectListItem($"{s.Code} {s.Name}（{s.Year} 年度預算 {s.AnnualBudget:N0}）", s.Id.ToString()))
    };
}

/// <summary>假日行事曆（人資、系統管理員）</summary>
[Authorize(Policy = Policies.HrOrAdmin)]
public class HolidaysController(MasterDataService service, IRepository<Holiday> repo, IClock clock) : Controller
{
    public async Task<IActionResult> Index(int? year)
    {
        var all = await repo.GetAllAsync();
        var y = year ?? clock.Today.Year;
        return View(await BuildIndexVm(y, new HolidayInput { Date = null }, all));
    }

    [HttpPost]
    public async Task<IActionResult> Create([Bind(Prefix = "NewHoliday")] HolidayInput input)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var saved = await service.SaveHolidayAsync(input);
                TempData.SetSuccess($"已新增「{saved.Date:yyyy/MM/dd} {saved.Name}」。");
                return RedirectToAction(nameof(Index), new { year = saved.Date.Year });
            }
            catch (BusinessException ex) when (ex is not ForbiddenException)
            {
                ModelState.AddBusinessError(ex, "NewHoliday");
            }
        }
        var y = input.Date?.Year ?? clock.Today.Year;
        return View("Index", await BuildIndexVm(y, input, await repo.GetAllAsync()));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var h = await repo.GetByIdAsync(id) ?? throw new NotFoundException("假日", id);
        return View(new HolidayEditViewModel { Input = new HolidayInput { Id = h.Id, Version = h.Version, Date = h.Date, Name = h.Name, Type = h.Type } });
    }

    [HttpPost]
    public async Task<IActionResult> Edit([Bind(Prefix = "Input")] HolidayInput input)
    {
        if (ModelState.IsValid)
        {
            try
            {
                var saved = await service.SaveHolidayAsync(input);
                TempData.SetSuccess($"已更新「{saved.Date:yyyy/MM/dd} {saved.Name}」。");
                return RedirectToAction(nameof(Index), new { year = saved.Date.Year });
            }
            catch (BusinessException ex) when (ex is not ForbiddenException)
            {
                ModelState.AddBusinessError(ex);
            }
        }
        return View(new HolidayEditViewModel { Input = input });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, int year)
    {
        await service.DeleteHolidayAsync(id);
        TempData.SetSuccess("已刪除。");
        return RedirectToAction(nameof(Index), new { year });
    }

    private Task<HolidayIndexViewModel> BuildIndexVm(int year, HolidayInput newHoliday, IReadOnlyList<Holiday> all)
    {
        var calendar = new WorkCalendar(all);
        var thisYear = clock.Today.Year;
        var years = all.Select(h => h.Date.Year).Append(thisYear).Append(thisYear + 1).Append(year).Distinct().Order().ToList();
        var first = new DateOnly(year, 1, 1);
        var workdays = Enumerable.Range(0, first.AddYears(1).DayNumber - first.DayNumber).Count(i => calendar.IsWorkday(first.AddDays(i)));
        return Task.FromResult(new HolidayIndexViewModel
        {
            Year = year,
            Years = years,
            Holidays = all.Where(h => h.Date.Year == year).OrderBy(h => h.Date).ToList(),
            NewHoliday = newHoliday,
            WorkdayCount = workdays
        });
    }
}

/// <summary>系統參數（系統管理員）</summary>
[Authorize(Policy = Policies.Admin)]
public class SettingsController(MasterDataService service, IRepository<SystemSetting> repo) : Controller
{
    private static readonly string[] GroupOrder = ["一般", "密碼原則", "登入安全", "附件", "簽核", "時效", "通知"];

    public async Task<IActionResult> Index() => View(await BuildVm(null));

    [HttpPost]
    public async Task<IActionResult> Index(List<SettingChange> settings)
    {
        try
        {
            var count = await service.UpdateSettingsAsync(settings);
            if (count == 0) TempData.SetInfo("沒有任何變更。");
            else TempData.SetSuccess($"已更新 {count} 項系統參數。");
            return RedirectToAction(nameof(Index));
        }
        catch (BusinessException ex) when (ex is not ForbiddenException)
        {
            ModelState.AddModelError(string.Empty, ex.Message);
            // 保留使用者輸入的值
            var vm = await BuildVm(ex is ValidationException v ? v.Field : null);
            var posted = settings.ToDictionary(s => s.Id, s => s.Value);
            foreach (var s in vm.Groups.SelectMany(g => g))
                if (posted.TryGetValue(s.Id, out var value) && ex is not ConcurrencyException) s.Value = value ?? "";
            return View(vm);
        }
    }

    private async Task<SettingsIndexViewModel> BuildVm(string? errorKey) => new()
    {
        ErrorKey = errorKey,
        Groups = (await repo.GetAllAsync())
            .GroupBy(s => s.Group)
            .OrderBy(g => Array.IndexOf(GroupOrder, g.Key) is var i && i < 0 ? 99 : i)
            .ToList()
    };
}
