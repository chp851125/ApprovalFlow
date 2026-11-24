using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Common;
using ApprovalFlow.Application.Documents;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Reports;

public sealed record NameValue(string Name, decimal Value);
public sealed record ChartSeries(string Name, IReadOnlyList<decimal> Data);
public sealed record CategoryChart(IReadOnlyList<string> Categories, IReadOnlyList<ChartSeries> Series);

/// <summary>簽核人效率（以工作小時計）</summary>
public sealed record ApproverStat(int EmployeeId, string Name, string Department, int Handled, decimal AvgHours, int Late, decimal SlaRate);

public sealed record OverdueItem(int Id, string DocumentNo, string Title, string Applicant, string StepName, string Assignees, DateTimeOffset DueAt, decimal OverdueHours);

public sealed class PersonalDashboard
{
    public int PendingCount { get; init; }
    public int PendingOverdue { get; init; }
    public int MyInProgress { get; init; }
    public int MyApprovedThisYear { get; init; }
    public decimal MyExpenseThisYear { get; init; }
    public decimal? MyAvgApprovalHours { get; init; }
    public CategoryChart MonthlyApplications { get; init; } = new([], []);
    public IReadOnlyList<NameValue> StatusDistribution { get; init; } = [];
    public CategoryChart LeaveBalance { get; init; } = new([], []);
    public CategoryChart MyHandledMonthly { get; init; } = new([], []);
}

public sealed class CompanyDashboard
{
    public int SubmittedLast12Months { get; init; }
    public int InProgress { get; init; }
    public int Overdue { get; init; }
    public decimal ApprovalRate { get; init; }
    public decimal AvgCycleHours { get; init; }
    public decimal SlaComplianceRate { get; init; }
    public CategoryChart MonthlyByType { get; init; } = new([], []);
    public IReadOnlyList<NameValue> StatusDistribution { get; init; } = [];
    public CategoryChart AvgCycleByType { get; init; } = new([], []);
    public IReadOnlyList<NameValue> ExpenseByCategory { get; init; } = [];
    public IReadOnlyList<NameValue> LeaveHoursByType { get; init; } = [];
    public CategoryChart DepartmentVolume { get; init; } = new([], []);
    public IReadOnlyList<ApproverStat> Approvers { get; init; } = [];
    public IReadOnlyList<OverdueItem> OverdueNow { get; init; } = [];
}

/// <summary>儀表板統計（個人 / 全公司）。所有時間長度皆以工作小時計算。</summary>
public sealed class DashboardService(
    IRepository<Document> documents,
    IRepository<Employee> employees,
    IRepository<Department> departments,
    IRepository<LeaveType> leaveTypes,
    IRepository<ExpenseCategory> categories,
    IRepository<Holiday> holidays,
    DocumentQueryService queries,
    LeaveBalanceService balances,
    ICurrentUser currentUser,
    IClock clock)
{
    private static readonly DocumentType[] Types = Enum.GetValues<DocumentType>();

    public async Task<PersonalDashboard> GetPersonalAsync()
    {
        var me = currentUser.Id ?? 0;
        var today = clock.Today;
        var all = await documents.GetAllAsync();
        var mine = all.Where(d => d.ApplicantId == me && d.Status != DocumentStatus.Draft).ToList();
        var months = LastMonths(today, 12);
        var calendar = new WorkCalendar(await holidays.GetAllAsync());
        var todo = await queries.QueryAsync(new DocumentQuery { Box = DocumentBox.Todo });

        var decided = Decisions(all).Where(x => (x.Assignee.ActedById ?? x.Assignee.EmployeeId) == me).ToList();
        var leaveRows = await balances.GetBalancesAsync(me, today.Year);
        var quota = leaveRows.Where(r => r.Entitled is not null).ToList();

        return new PersonalDashboard
        {
            PendingCount = todo.Count,
            PendingOverdue = todo.Count(t => t.IsOverdue),
            MyInProgress = mine.Count(d => d.Status.IsActiveFlow()),
            MyApprovedThisYear = mine.Count(d => d.Status == DocumentStatus.Approved && d.CompletedAt?.Year == today.Year),
            MyExpenseThisYear = mine.Where(d => d.DocumentType == DocumentType.Expense && d.Status == DocumentStatus.Approved && d.CompletedAt?.Year == today.Year).Sum(d => d.Amount ?? 0),
            MyAvgApprovalHours = decided.Count == 0 ? null : Round1(decided.Average(x => (decimal)calendar.WorkingHoursBetween(x.Step.ActivatedAt!.Value, x.Assignee.DecidedAt!.Value))),
            MonthlyApplications = new(months.Select(Label).ToList(),
                Types.Select(t => new ChartSeries(t.ToDisplay(), months.Select(m => (decimal)mine.Count(d => d.DocumentType == t && InMonth(d.SubmittedAt, m))).ToList())).ToList()),
            StatusDistribution = mine.GroupBy(d => d.Status).OrderBy(g => g.Key).Select(g => new NameValue(g.Key.ToDisplay(), g.Count())).ToList(),
            LeaveBalance = new(quota.Select(r => r.Type.Name).ToList(),
            [
                new ChartSeries("已休", quota.Select(r => r.Used).ToList()),
                new ChartSeries("簽核中", quota.Select(r => r.Pending).ToList()),
                new ChartSeries("剩餘", quota.Select(r => Math.Max(0, r.Remaining ?? 0)).ToList())
            ]),
            MyHandledMonthly = new(months.Select(Label).ToList(),
            [
                new ChartSeries("核准", months.Select(m => (decimal)decided.Count(x => x.Assignee.Decision == AssigneeDecision.Approved && InMonth(x.Assignee.DecidedAt, m))).ToList()),
                new ChartSeries("駁回 / 退回", months.Select(m => (decimal)decided.Count(x => x.Assignee.Decision != AssigneeDecision.Approved && InMonth(x.Assignee.DecidedAt, m))).ToList())
            ])
        };
    }

    public async Task<CompanyDashboard> GetCompanyAsync()
    {
        var today = clock.Today;
        var now = clock.Now;
        var months = LastMonths(today, 12);
        var all = (await documents.GetAllAsync()).Where(d => d.Status != DocumentStatus.Draft).ToList();
        var recent = all.Where(d => d.SubmittedAt is { } s && DateOnly.FromDateTime(s.DateTime) >= months[0]).ToList();
        var calendar = new WorkCalendar(await holidays.GetAllAsync());
        var emps = (await employees.GetAllAsync()).ToDictionary(e => e.Id);
        var depts = (await departments.GetAllAsync()).ToDictionary(d => d.Id);

        var completed = recent.Where(d => d.Status is DocumentStatus.Approved or DocumentStatus.Rejected).ToList();
        decimal CycleHours(Document d) => (decimal)calendar.WorkingHoursBetween(d.SubmittedAt!.Value, d.CompletedAt!.Value);
        var approvedWithTimes = recent.Where(d => d.Status == DocumentStatus.Approved && d.CompletedAt is not null).ToList();

        var decisions = Decisions(recent).ToList();
        var withDue = decisions.Where(x => x.Step.DueAt is not null).ToList();

        var approvers = decisions
            .GroupBy(x => x.Assignee.ActedById ?? x.Assignee.EmployeeId)
            .Select(g =>
            {
                var e = emps.GetValueOrDefault(g.Key);
                var late = g.Count(x => x.Step.DueAt is { } due && x.Assignee.DecidedAt > due);
                var dueCount = g.Count(x => x.Step.DueAt is not null);
                return new ApproverStat(g.Key, e?.Name ?? $"#{g.Key}", e is null ? "" : depts.GetValueOrDefault(e.DepartmentId)?.Name ?? "",
                    g.Count(),
                    Round1(g.Average(x => (decimal)calendar.WorkingHoursBetween(x.Step.ActivatedAt!.Value, x.Assignee.DecidedAt!.Value))),
                    late, dueCount == 0 ? 100 : Round1(100m * (dueCount - late) / dueCount));
            })
            .OrderByDescending(a => a.Handled).ToList();

        var leaveNames = (await leaveTypes.GetAllAsync()).ToDictionary(l => l.Id, l => l.Name);
        var catNames = (await categories.GetAllAsync()).ToDictionary(c => c.Id, c => c.Name);
        var thisYearApproved = all.Where(d => d.Status == DocumentStatus.Approved && d.SubmittedAt?.Year == today.Year).ToList();

        var activeDepts = depts.Values.Where(d => d.IsActive).OrderBy(d => d.SortOrder).ToList();

        return new CompanyDashboard
        {
            SubmittedLast12Months = recent.Count,
            InProgress = all.Count(d => d.Status == DocumentStatus.InProgress),
            Overdue = all.Count(d => d.Status == DocumentStatus.Overdue),
            ApprovalRate = completed.Count == 0 ? 0 : Round1(100m * completed.Count(d => d.Status == DocumentStatus.Approved) / completed.Count),
            AvgCycleHours = approvedWithTimes.Count == 0 ? 0 : Round1(approvedWithTimes.Average(CycleHours)),
            SlaComplianceRate = withDue.Count == 0 ? 100 : Round1(100m * withDue.Count(x => x.Assignee.DecidedAt <= x.Step.DueAt) / withDue.Count),
            MonthlyByType = new(months.Select(Label).ToList(),
                Types.Select(t => new ChartSeries(t.ToDisplay(), months.Select(m => (decimal)recent.Count(d => d.DocumentType == t && InMonth(d.SubmittedAt, m))).ToList())).ToList()),
            StatusDistribution = recent.GroupBy(d => d.Status).OrderBy(g => g.Key).Select(g => new NameValue(g.Key.ToDisplay(), g.Count())).ToList(),
            AvgCycleByType = new(Types.Select(t => t.ToDisplay()).ToList(),
            [
                new ChartSeries("平均完成時間（工作小時）", Types.Select(t =>
                {
                    var list = approvedWithTimes.Where(d => d.DocumentType == t).ToList();
                    return list.Count == 0 ? 0 : Round1(list.Average(CycleHours));
                }).ToList())
            ]),
            ExpenseByCategory = thisYearApproved.Where(d => d.Content.Expense is not null)
                .SelectMany(d => d.Content.Expense!.Items)
                .GroupBy(i => catNames.GetValueOrDefault(i.CategoryId) ?? "其他")
                .Select(g => new NameValue(g.Key, g.Sum(i => i.Amount))).OrderByDescending(x => x.Value).ToList(),
            LeaveHoursByType = thisYearApproved.Where(d => d.Content.Leave is not null)
                .GroupBy(d => leaveNames.GetValueOrDefault(d.Content.Leave!.LeaveTypeId) ?? "其他")
                .Select(g => new NameValue(g.Key, g.Sum(d => d.Hours ?? 0))).OrderByDescending(x => x.Value).ToList(),
            DepartmentVolume = new(activeDepts.Select(d => d.Name).ToList(),
                Types.Select(t => new ChartSeries(t.ToDisplay(), activeDepts.Select(dep => (decimal)recent.Count(d => d.DepartmentId == dep.Id && d.DocumentType == t)).ToList())).ToList()),
            Approvers = approvers.Take(10).ToList(),
            OverdueNow = all.Where(d => d.Status == DocumentStatus.Overdue && d.GetCurrentStep() is { DueAt: not null })
                .Select(d =>
                {
                    var s = d.GetCurrentStep()!;
                    return new OverdueItem(d.Id, d.DocumentNo, d.Title, emps.GetValueOrDefault(d.ApplicantId)?.Name ?? "", s.Name,
                        string.Join("、", s.Assignees.Where(a => a.Decision == AssigneeDecision.Pending).Select(a => emps.GetValueOrDefault(a.EmployeeId)?.Name)),
                        s.DueAt!.Value, Round1((decimal)calendar.WorkingHoursBetween(s.DueAt.Value, now)));
                })
                .OrderByDescending(x => x.OverdueHours).Take(10).ToList()
        };
    }

    /// <summary>所有已做出決定（核准 / 駁回 / 退回）的簽核紀錄</summary>
    public static IEnumerable<(Document Doc, DocumentStep Step, StepAssignee Assignee)> Decisions(IEnumerable<Document> docs) =>
        from d in docs
        from s in d.Steps
        where s.ActivatedAt is not null
        from a in s.Assignees
        where a.DecidedAt is not null && a.DecidedAt >= s.ActivatedAt &&
              a.Decision is AssigneeDecision.Approved or AssigneeDecision.Rejected or AssigneeDecision.Returned
        select (d, s, a);

    public static List<DateOnly> LastMonths(DateOnly today, int count)
    {
        var first = new DateOnly(today.Year, today.Month, 1).AddMonths(-(count - 1));
        return Enumerable.Range(0, count).Select(i => first.AddMonths(i)).ToList();
    }

    private static bool InMonth(DateTimeOffset? at, DateOnly month) => at is { } t && t.Year == month.Year && t.Month == month.Month;
    private static string Label(DateOnly m) => m.Month == 1 ? $"{m.Year}/{m.Month}" : $"{m.Month}月";
    private static decimal Round1(decimal v) => Math.Round(v, 1);
}
