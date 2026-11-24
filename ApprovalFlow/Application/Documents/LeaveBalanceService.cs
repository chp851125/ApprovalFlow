using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Common;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Documents;

/// <summary>假別餘額。Entitled 為 null 代表不限額度。</summary>
public sealed record LeaveBalanceRow(LeaveType Type, decimal? Entitled, decimal Used, decimal Pending)
{
    public decimal? Remaining => Entitled is decimal e ? e - Used - Pending : null;
}

/// <summary>
/// 假別餘額計算：年度額度 − 期初已休 − 已核准 − 簽核中。
/// 年度額度取自 leaveBalances；沒有資料時，特休依年資計算、其他假別用預設額度。
/// </summary>
public sealed class LeaveBalanceService(
    IRepository<LeaveType> leaveTypes,
    IRepository<LeaveBalance> balances,
    IRepository<Document> documents,
    IRepository<Employee> employees)
{
    public async Task<IReadOnlyList<LeaveBalanceRow>> GetBalancesAsync(int employeeId, int year, int? excludeDocumentId = null)
    {
        var emp = await employees.GetByIdAsync(employeeId);
        var rows = (await balances.QueryAsync(b => b.EmployeeId == employeeId && b.Year == year)).ToDictionary(b => b.LeaveTypeId);
        var leaves = (await documents.QueryAsync(d => d.ApplicantId == employeeId && d.DocumentType == DocumentType.Leave && d.Id != (excludeDocumentId ?? 0)))
            .Where(d => d.Content.Leave is { } l && l.StartAt.Year == year)
            .ToList();

        var result = new List<LeaveBalanceRow>();
        foreach (var t in (await leaveTypes.GetAllAsync()).Where(t => t.IsActive).OrderBy(t => t.SortOrder))
        {
            decimal? entitled = rows.TryGetValue(t.Id, out var b)
                ? b.EntitledHours
                : t.UseSeniorityQuota && emp is not null
                    ? LeaveQuota.AnnualLeaveDays(emp.HireDate, new DateOnly(year, 1, 1)) * 8m
                    : t.DefaultQuotaHours;

            var ofType = leaves.Where(d => d.Content.Leave!.LeaveTypeId == t.Id).ToList();
            var used = (b?.OpeningUsedHours ?? 0) + ofType.Where(d => d.Status == DocumentStatus.Approved).Sum(d => d.Hours ?? 0);
            var pending = ofType.Where(d => d.Status.IsActiveFlow()).Sum(d => d.Hours ?? 0);
            result.Add(new LeaveBalanceRow(t, entitled, used, pending));
        }
        return result;
    }
}
