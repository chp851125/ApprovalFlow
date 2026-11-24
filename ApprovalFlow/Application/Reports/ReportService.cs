using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Common;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Reports;

public sealed class ReportFilter
{
    public string Report { get; set; } = "documents";
    public int? Year { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public DocumentType? Type { get; set; }
    public DocumentStatus? Status { get; set; }
    public int? DepartmentId { get; set; }
}

public sealed record ReportDefinition(string Key, string Name, string Description, string Icon, bool UsesYear, bool UsesRange, bool UsesType);

/// <summary>
/// 報表。權限：系統管理員可看全部；人資可看請假 / 加班；財務可看費用 / 採購。
/// 單據清單會依角色自動限制單別。
/// </summary>
public sealed class ReportService(
    IRepository<Document> documents,
    IRepository<Employee> employees,
    IRepository<Department> departments,
    IRepository<LeaveType> leaveTypes,
    IRepository<ExpenseCategory> categories,
    IRepository<Holiday> holidays,
    ICurrentUser currentUser,
    IClock clock)
{
    public const int MaxRows = 10_000;

    private bool IsAdmin => currentUser.IsInRole(Roles.Admin);
    private bool IsHr => IsAdmin || currentUser.IsInRole(Roles.HR);
    private bool IsFinance => IsAdmin || currentUser.IsInRole(Roles.Finance);

    public IReadOnlyList<ReportDefinition> Available()
    {
        var list = new List<ReportDefinition>();
        if (IsHr || IsFinance) list.Add(new("documents", "單據明細", "依條件列出單據與處理時間", "bi-table", false, true, true));
        if (IsHr) list.Add(new("leave", "請假統計", "每位員工各假別已核准時數", "bi-calendar2-week", true, false, false));
        if (IsHr) list.Add(new("overtime", "加班統計", "每位員工每月加班時數與補償方式", "bi-moon-stars", true, false, false));
        if (IsFinance) list.Add(new("expense", "費用統計", "各費用類別每月已核准金額", "bi-receipt", true, false, false));
        if (IsAdmin) list.Add(new("approvers", "簽核效率", "每位簽核人處理件數、平均時間與時效達成率", "bi-speedometer2", false, true, false));
        return list;
    }

    /// <summary>單據清單可查詢的單別（依角色）</summary>
    public IReadOnlyList<DocumentType> AllowedTypes() =>
        IsAdmin ? Enum.GetValues<DocumentType>()
        : Enum.GetValues<DocumentType>().Where(t =>
            (IsHr && t is DocumentType.Leave or DocumentType.Overtime) ||
            (IsFinance && t is DocumentType.Expense or DocumentType.Purchase)).ToArray();

    public async Task<ReportTable> BuildAsync(ReportFilter f)
    {
        if (Available().All(r => r.Key != f.Report)) throw new ForbiddenException("您沒有權限檢視此報表。");
        var year = f.Year ?? clock.Today.Year;
        return f.Report switch
        {
            "leave" => await LeaveAsync(year, f.DepartmentId),
            "overtime" => await OvertimeAsync(year, f.DepartmentId),
            "expense" => await ExpenseAsync(year, f.DepartmentId),
            "approvers" => await ApproversAsync(f),
            _ => await DocumentsAsync(f)
        };
    }

    // ---------------------------------------------------------------------

    private async Task<ReportTable> DocumentsAsync(ReportFilter f)
    {
        var (emps, depts, deptScope) = await OrgAsync(f.DepartmentId);
        var calendar = new WorkCalendar(await holidays.GetAllAsync());
        var allowed = AllowedTypes();
        var from = f.From ?? clock.Today.AddMonths(-3);
        var to = f.To ?? clock.Today;

        var rows = (await documents.GetAllAsync())
            .Where(d => d.Status != DocumentStatus.Draft && d.SubmittedAt is not null && allowed.Contains(d.DocumentType))
            .Where(d => f.Type is null || d.DocumentType == f.Type)
            .Where(d => f.Status is null || d.Status == f.Status)
            .Where(d => deptScope is null || deptScope.Contains(d.DepartmentId))
            .Where(d => DateOnly.FromDateTime(d.SubmittedAt!.Value.DateTime) is var s && s >= from && s <= to)
            .OrderBy(d => d.SubmittedAt).Take(MaxRows)
            .Select(d => new object?[]
            {
                d.DocumentNo, d.DocumentType.ToDisplay(), d.Title,
                emps.GetValueOrDefault(d.ApplicantId)?.Name, depts.GetValueOrDefault(d.DepartmentId)?.Name,
                d.Status.ToDisplay(), d.Amount, d.Hours, d.SubmittedAt, d.CompletedAt,
                d.CompletedAt is { } c ? Math.Round((decimal)calendar.WorkingHoursBetween(d.SubmittedAt!.Value, c), 1) : null,
                d.Status.IsActiveFlow() ? d.GetCurrentStep()?.Name : null,
                d.RevisionNo
            }).ToList();

        return new ReportTable
        {
            Title = "單據明細",
            SheetName = "單據明細",
            Subtitle = $"送出日 {from:yyyy/MM/dd}～{to:yyyy/MM/dd}" + (f.Type is { } t ? $"・{t.ToDisplay()}" : "") + (f.Status is { } s ? $"・{s.ToDisplay()}" : "") + $"・共 {rows.Count} 筆",
            Columns =
            [
                new("單號"), new("單別"), new("主旨", Width: 40), new("申請人"), new("部門"), new("狀態"),
                new("金額", ColumnKind.Money), new("時數", ColumnKind.Number), new("送出時間", ColumnKind.DateTime), new("結案時間", ColumnKind.DateTime),
                new("處理工時", ColumnKind.Number), new("目前關卡"), new("送出次數", ColumnKind.Integer)
            ],
            Rows = rows,
            Totals = rows.Count == 0 ? null :
                ["合計", null, $"{rows.Count} 筆", null, null, null, rows.Sum(r => (decimal?)r[6] ?? 0), rows.Sum(r => (decimal?)r[7] ?? 0), null, null, null, null, null]
        };
    }

    private async Task<ReportTable> LeaveAsync(int year, int? deptId)
    {
        var (emps, depts, deptScope) = await OrgAsync(deptId);
        var types = (await leaveTypes.GetAllAsync()).OrderBy(t => t.SortOrder).ToList();
        var leaves = (await documents.QueryAsync(d => d.DocumentType == DocumentType.Leave && d.Status == DocumentStatus.Approved))
            .Where(d => d.Content.Leave?.StartAt.Year == year).ToList();

        var rows = emps.Values
            .Where(e => deptScope is null || deptScope.Contains(e.DepartmentId))
            .Where(e => e.IsActive || leaves.Any(d => d.ApplicantId == e.Id))
            .OrderBy(e => depts.GetValueOrDefault(e.DepartmentId)?.SortOrder).ThenBy(e => e.EmployeeNo)
            .Select(e =>
            {
                var mine = leaves.Where(d => d.ApplicantId == e.Id).ToList();
                var cells = new List<object?> { e.EmployeeNo, e.Name, depts.GetValueOrDefault(e.DepartmentId)?.Name };
                cells.AddRange(types.Select(t => (object?)mine.Where(d => d.Content.Leave!.LeaveTypeId == t.Id).Sum(d => d.Hours ?? 0)));
                cells.Add(mine.Sum(d => d.Hours ?? 0));
                cells.Add(mine.Count);
                return cells.ToArray();
            }).ToList();

        var columns = new List<ReportColumn> { new("員編"), new("姓名"), new("部門") };
        columns.AddRange(types.Select(t => new ReportColumn($"{t.Name}（時）", ColumnKind.Number)));
        columns.Add(new("合計（時）", ColumnKind.Number));
        columns.Add(new("張數", ColumnKind.Integer));
        return new ReportTable
        {
            Title = $"{year} 年請假統計（已核准）",
            SheetName = $"{year}請假統計",
            Subtitle = $"依請假開始日統計・單位：小時（8 小時 = 1 天）",
            Columns = columns,
            Rows = rows,
            Totals = SumTotals(rows, columns, 3, "合計")
        };
    }

    private async Task<ReportTable> OvertimeAsync(int year, int? deptId)
    {
        var (emps, depts, deptScope) = await OrgAsync(deptId);
        var ot = (await documents.QueryAsync(d => d.DocumentType == DocumentType.Overtime && d.Status == DocumentStatus.Approved))
            .Where(d => d.Content.Overtime?.Date.Year == year).ToList();

        var rows = emps.Values
            .Where(e => deptScope is null || deptScope.Contains(e.DepartmentId))
            .Where(e => ot.Any(d => d.ApplicantId == e.Id))
            .OrderBy(e => depts.GetValueOrDefault(e.DepartmentId)?.SortOrder).ThenBy(e => e.EmployeeNo)
            .Select(e =>
            {
                var mine = ot.Where(d => d.ApplicantId == e.Id).ToList();
                var cells = new List<object?> { e.EmployeeNo, e.Name, depts.GetValueOrDefault(e.DepartmentId)?.Name };
                cells.AddRange(Enumerable.Range(1, 12).Select(m => (object?)mine.Where(d => d.Content.Overtime!.Date.Month == m).Sum(d => d.Hours ?? 0)));
                cells.Add(mine.Sum(d => d.Hours ?? 0));
                cells.Add(mine.Where(d => d.Content.Overtime!.Compensation == OvertimeCompensation.Pay).Sum(d => d.Hours ?? 0));
                cells.Add(mine.Where(d => d.Content.Overtime!.Compensation == OvertimeCompensation.CompensatoryLeave).Sum(d => d.Hours ?? 0));
                cells.Add(mine.Where(d => d.Content.Overtime!.IsHoliday).Sum(d => d.Hours ?? 0));
                return cells.ToArray();
            }).ToList();

        var columns = new List<ReportColumn> { new("員編"), new("姓名"), new("部門") };
        columns.AddRange(Enumerable.Range(1, 12).Select(m => new ReportColumn($"{m}月", ColumnKind.Number)));
        columns.AddRange([new("合計", ColumnKind.Number), new("加班費", ColumnKind.Number), new("補休", ColumnKind.Number), new("休假日", ColumnKind.Number)]);
        return new ReportTable
        {
            Title = $"{year} 年加班統計（已核准）",
            SheetName = $"{year}加班統計",
            Subtitle = "單位：小時",
            Columns = columns,
            Rows = rows,
            Totals = SumTotals(rows, columns, 3, "合計")
        };
    }

    private async Task<ReportTable> ExpenseAsync(int year, int? deptId)
    {
        var (_, _, deptScope) = await OrgAsync(deptId);
        var cats = (await categories.GetAllAsync()).OrderBy(c => c.SortOrder).ToList();
        var items = (await documents.QueryAsync(d => d.DocumentType == DocumentType.Expense && d.Status == DocumentStatus.Approved))
            .Where(d => deptScope is null || deptScope.Contains(d.DepartmentId))
            .SelectMany(d => d.Content.Expense?.Items ?? [])
            .Where(i => i.Date.Year == year).ToList();

        var rows = cats.Select(c =>
        {
            var cells = new List<object?> { c.Code, c.Name };
            cells.AddRange(Enumerable.Range(1, 12).Select(m => (object?)items.Where(i => i.CategoryId == c.Id && i.Date.Month == m).Sum(i => i.Amount)));
            cells.Add(items.Where(i => i.CategoryId == c.Id).Sum(i => i.Amount));
            cells.Add(items.Count(i => i.CategoryId == c.Id));
            return cells.ToArray();
        }).ToList();

        var columns = new List<ReportColumn> { new("代碼"), new("費用類別") };
        columns.AddRange(Enumerable.Range(1, 12).Select(m => new ReportColumn($"{m}月", ColumnKind.Money)));
        columns.Add(new("合計", ColumnKind.Money));
        columns.Add(new("筆數", ColumnKind.Integer));
        return new ReportTable
        {
            Title = $"{year} 年費用統計（已核准）",
            SheetName = $"{year}費用統計",
            Subtitle = "依費用發生日統計・單位：新台幣元",
            Columns = columns,
            Rows = rows,
            Totals = SumTotals(rows, columns, 2, "合計")
        };
    }

    private async Task<ReportTable> ApproversAsync(ReportFilter f)
    {
        var (emps, depts, _) = await OrgAsync(null);
        var calendar = new WorkCalendar(await holidays.GetAllAsync());
        var from = f.From ?? clock.Today.AddMonths(-3);
        var to = f.To ?? clock.Today;
        var decisions = DashboardService.Decisions(await documents.GetAllAsync())
            .Where(x => DateOnly.FromDateTime(x.Assignee.DecidedAt!.Value.DateTime) is var dd && dd >= from && dd <= to).ToList();

        var rows = decisions.GroupBy(x => x.Assignee.ActedById ?? x.Assignee.EmployeeId)
            .Select(g =>
            {
                var e = emps.GetValueOrDefault(g.Key);
                var hours = g.Select(x => (decimal)calendar.WorkingHoursBetween(x.Step.ActivatedAt!.Value, x.Assignee.DecidedAt!.Value)).ToList();
                var withDue = g.Where(x => x.Step.DueAt is not null).ToList();
                var late = withDue.Count(x => x.Assignee.DecidedAt > x.Step.DueAt);
                return new object?[]
                {
                    e?.EmployeeNo, e?.Name ?? $"#{g.Key}", e is null ? null : depts.GetValueOrDefault(e.DepartmentId)?.Name,
                    g.Count(), g.Count(x => x.Assignee.Decision == AssigneeDecision.Approved),
                    g.Count(x => x.Assignee.Decision != AssigneeDecision.Approved),
                    g.Count(x => x.Assignee.IsDelegated()),
                    Math.Round(hours.Average(), 1), Math.Round(hours.Max(), 1), late,
                    withDue.Count == 0 ? 100m : Math.Round(100m * (withDue.Count - late) / withDue.Count, 1)
                };
            })
            .OrderByDescending(r => (int)r[3]!).ToList();

        return new ReportTable
        {
            Title = "簽核效率",
            SheetName = "簽核效率",
            Subtitle = $"簽核日 {from:yyyy/MM/dd}～{to:yyyy/MM/dd}・時間單位：工作小時",
            Columns =
            [
                new("員編"), new("姓名"), new("部門"), new("處理件數", ColumnKind.Integer), new("核准", ColumnKind.Integer),
                new("駁回 / 退回", ColumnKind.Integer), new("代理簽核", ColumnKind.Integer), new("平均時間", ColumnKind.Number),
                new("最長時間", ColumnKind.Number), new("逾時件數", ColumnKind.Integer), new("時效達成率", ColumnKind.Percent)
            ],
            Rows = rows
        };
    }

    // ---------------------------------------------------------------------

    private async Task<(Dictionary<int, Employee> Emps, Dictionary<int, Department> Depts, HashSet<int>? Scope)> OrgAsync(int? deptId)
    {
        var emps = (await employees.GetAllAsync()).ToDictionary(e => e.Id);
        var depts = (await departments.GetAllAsync()).ToDictionary(d => d.Id);
        HashSet<int>? scope = null;
        if (deptId is int root)
        {
            scope = [root];
            bool added;
            do
            {
                added = false;
                foreach (var d in depts.Values.Where(d => d.ParentId is int p && scope.Contains(p) && !scope.Contains(d.Id)))
                    added |= scope.Add(d.Id);
            } while (added);
        }
        return (emps, depts, scope);
    }

    /// <summary>從第 start 欄起加總數值欄</summary>
    private static object?[]? SumTotals(List<object?[]> rows, IReadOnlyList<ReportColumn> columns, int start, string label)
    {
        if (rows.Count == 0) return null;
        var totals = new object?[columns.Count];
        totals[0] = label;
        for (var c = start; c < columns.Count; c++)
        {
            totals[c] = columns[c].Kind == ColumnKind.Integer
                ? rows.Sum(r => r[c] is int i ? i : 0)
                : rows.Sum(r => r[c] is decimal m ? m : 0);
        }
        return totals;
    }
}
