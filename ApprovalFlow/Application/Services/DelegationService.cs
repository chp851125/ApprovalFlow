using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Services;

/// <summary>
/// 代理人設定。規則：
/// 代理人需在職且不可為本人；同一被代理人在重疊期間、重疊單別不可有兩筆代理；
/// 代理人本身在該期間若也設定了代理，則拒絕（避免連鎖代理）；最長 180 天。
/// 一般同仁只能設定自己的代理，人資與系統管理員可替任何人設定。
/// </summary>
public sealed class DelegationService(
    IRepository<Delegation> delegations,
    IRepository<Employee> employees,
    ICurrentUser currentUser,
    AuditService audit,
    IClock clock)
{
    public const int MaxDays = 180;

    public bool CanManageAll => currentUser.IsInRole(Roles.HR) || currentUser.IsInRole(Roles.Admin);

    public async Task<Delegation> CreateAsync(DelegationInput input)
    {
        var me = currentUser.Id ?? throw new ForbiddenException();
        var principalId = input.PrincipalId == 0 ? me : input.PrincipalId;
        if (principalId != me && !CanManageAll) throw new ForbiddenException("您只能設定自己的代理人。");

        var principal = await employees.GetByIdAsync(principalId) ?? throw new ValidationException("被代理人不存在。", nameof(input.PrincipalId));
        if (!principal.IsActive) throw new ValidationException("被代理人已停用。", nameof(input.PrincipalId));
        var deputy = await employees.GetByIdAsync(input.DelegateId) ?? throw new ValidationException("代理人不存在。", nameof(input.DelegateId));
        if (!deputy.IsActive) throw new ValidationException($"「{deputy.Name}」已停用，不可擔任代理人。", nameof(input.DelegateId));
        if (deputy.Id == principal.Id) throw new ValidationException("代理人不可為本人。", nameof(input.DelegateId));

        var start = input.StartDate!.Value;
        var end = input.EndDate!.Value;
        var today = clock.Today;
        if (start < today) throw new ValidationException("開始日期不可早於今天。", nameof(input.StartDate));
        if (end < start) throw new ValidationException("結束日期不可早於開始日期。", nameof(input.EndDate));
        if (end.DayNumber - start.DayNumber + 1 > MaxDays)
            throw new ValidationException($"單次代理期間最長 {MaxDays} 天。", nameof(input.EndDate));

        var types = input.DocumentTypes.Distinct().Order().ToList();
        if (types.Count == Enum.GetValues<DocumentType>().Length) types = []; // 全選視同全部

        var active = await delegations.QueryAsync(d => d.IsEnabled && d.EndDate >= start && d.StartDate <= end);
        var clash = active.FirstOrDefault(d => d.PrincipalId == principal.Id && TypesOverlap(d.DocumentTypes, types));
        if (clash is not null)
            throw new ValidationException($"{principal.Name} 在 {clash.StartDate:yyyy/MM/dd}～{clash.EndDate:yyyy/MM/dd} 已設定代理，期間與單別不可重疊。");
        var chain = active.FirstOrDefault(d => d.PrincipalId == deputy.Id && TypesOverlap(d.DocumentTypes, types));
        if (chain is not null)
            throw new ValidationException($"{deputy.Name} 在 {chain.StartDate:yyyy/MM/dd}～{chain.EndDate:yyyy/MM/dd} 本身也有代理設定，請選擇其他代理人。", nameof(input.DelegateId));
        var reverse = active.FirstOrDefault(d => d.DelegateId == principal.Id && TypesOverlap(d.DocumentTypes, types));
        if (reverse is not null)
            throw new ValidationException($"{principal.Name} 在此期間正擔任他人的代理人（{reverse.StartDate:yyyy/MM/dd}～{reverse.EndDate:yyyy/MM/dd}），不可同時設定自己的代理。");

        var created = await delegations.AddAsync(new Delegation
        {
            PrincipalId = principal.Id,
            DelegateId = deputy.Id,
            StartDate = start,
            EndDate = end,
            DocumentTypes = types,
            Reason = input.Reason.Trim(),
            IsEnabled = true,
            CreatedById = me,
            CreatedAt = clock.Now
        });
        await audit.LogAsync("Create", nameof(Delegation), created.Id.ToString(),
            $"設定代理：{principal.Name} → {deputy.Name}（{start:yyyy/MM/dd}～{end:yyyy/MM/dd}，{DescribeTypes(types)}）");
        return created;
    }

    /// <summary>取消（或提前結束）代理</summary>
    public async Task DisableAsync(int id, int version)
    {
        var d = await delegations.GetByIdAsync(id) ?? throw new NotFoundException("代理設定", id);
        var me = currentUser.Id;
        if (d.PrincipalId != me && d.CreatedById != me && !CanManageAll) throw new ForbiddenException();
        if (!d.IsEnabled) return;
        if (d.EndDate < clock.Today) throw new BusinessException("此代理設定已過期，無須取消。");

        d.IsEnabled = false;
        d.Version = version;
        await delegations.UpdateAsync(d);
        await audit.LogAsync("Disable", nameof(Delegation), id.ToString(), $"取消代理設定 #{id}");
    }

    public static string DescribeTypes(IReadOnlyCollection<DocumentType> types) =>
        types.Count == 0 ? "全部單別" : string.Join("、", types.Select(t => t.ToDisplay()));

    private static bool TypesOverlap(List<DocumentType> a, List<DocumentType> b) =>
        a.Count == 0 || b.Count == 0 || a.Intersect(b).Any();
}
