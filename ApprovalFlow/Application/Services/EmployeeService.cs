using System.Security.Cryptography;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Services;

/// <summary>員工帳號維護：新增、修改、停用/復職、重設密碼、解除鎖定、角色設定</summary>
public sealed class EmployeeService(
    IRepository<Employee> employees,
    IRepository<Department> departments,
    IRepository<JobTitle> titles,
    IRepository<Delegation> delegations,
    IRepository<Document> documents,
    IPasswordService passwords,
    SettingsService settings,
    AuditService audit,
    ICurrentUser currentUser,
    IClock clock)
{
    private static readonly string[] AuditIgnore = ["FailedLoginCount", "LockoutEnd", "LastLoginAt", "PasswordChangedAt"];

    /// <summary>新增員工，回傳臨時密碼（僅顯示一次）</summary>
    public async Task<(Employee Employee, string TempPassword)> CreateAsync(EmployeeInput input)
    {
        EnsureHrOrAdmin();
        var roles = NormalizeRoles(input.Roles);
        if (roles.Contains(Roles.Admin) && !currentUser.IsInRole(Roles.Admin))
            throw new ForbiddenException("只有系統管理員可以指派「系統管理員」角色。");

        var all = await employees.GetAllAsync();
        await ValidateAsync(input, all, null);

        var temp = await GenerateTempPasswordAsync();
        var emp = new Employee
        {
            EmployeeNo = input.EmployeeNo.Trim().ToUpperInvariant(),
            Account = input.Account.Trim().ToLowerInvariant(),
            Name = input.Name.Trim(),
            Email = input.Email.Trim().ToLowerInvariant(),
            Phone = Clean(input.Phone),
            Extension = Clean(input.Extension),
            DepartmentId = input.DepartmentId,
            JobTitleId = input.JobTitleId,
            ManagerId = input.ManagerId,
            HireDate = input.HireDate!.Value,
            Roles = roles,
            IsActive = true,
            PasswordHash = passwords.Hash(temp),
            SecurityStamp = NewStamp(),
            MustChangePassword = true,
            PasswordChangedAt = clock.Now
        };
        emp = await employees.AddAsync(emp);
        await audit.LogAsync("Create", nameof(Employee), emp.Id.ToString(), $"新增員工「{emp.Name}（{emp.Account}）」",
            AuditService.Diff<Employee>(null, emp, AuditIgnore));
        return (emp, temp);
    }

    public async Task<Employee> UpdateAsync(EmployeeInput input)
    {
        var before = await employees.GetByIdAsync(input.Id) ?? throw new NotFoundException("員工", input.Id);
        EnsureCanManage(before);
        var all = await employees.GetAllAsync();
        await ValidateAsync(input, all, before);

        var emp = (await employees.GetByIdAsync(input.Id))!;
        emp.EmployeeNo = input.EmployeeNo.Trim().ToUpperInvariant();
        emp.Account = input.Account.Trim().ToLowerInvariant();
        emp.Name = input.Name.Trim();
        emp.Email = input.Email.Trim().ToLowerInvariant();
        emp.Phone = Clean(input.Phone);
        emp.Extension = Clean(input.Extension);
        emp.DepartmentId = input.DepartmentId;
        emp.JobTitleId = input.JobTitleId;
        emp.ManagerId = input.ManagerId;
        emp.HireDate = input.HireDate!.Value;
        // 帳號變更時讓既有登入失效
        if (!string.Equals(before.Account, emp.Account, StringComparison.Ordinal)) emp.SecurityStamp = NewStamp();
        emp.Version = input.Version;
        await employees.UpdateAsync(emp);
        await audit.LogAsync("Update", nameof(Employee), emp.Id.ToString(), $"修改員工資料「{emp.Name}」",
            AuditService.Diff(before, emp, AuditIgnore));
        return emp;
    }

    public async Task UpdateRolesAsync(int id, int version, IEnumerable<string> roles)
    {
        var before = await employees.GetByIdAsync(id) ?? throw new NotFoundException("員工", id);
        EnsureCanManage(before);
        var newRoles = NormalizeRoles(roles);
        var isAdmin = currentUser.IsInRole(Roles.Admin);

        if (newRoles.Contains(Roles.Admin) != before.HasRole(Roles.Admin))
        {
            if (!isAdmin) throw new ForbiddenException("只有系統管理員可以變更「系統管理員」角色。");
            if (!newRoles.Contains(Roles.Admin))
            {
                if (id == currentUser.Id) throw new BusinessException("不可移除自己的系統管理員角色，以免無人可管理系統。");
                await EnsureAnotherActiveAdminAsync(id);
            }
        }

        var emp = (await employees.GetByIdAsync(id))!;
        emp.Roles = newRoles;
        emp.Version = version;
        await employees.UpdateAsync(emp);
        await audit.LogAsync("UpdateRoles", nameof(Employee), id.ToString(),
            $"變更「{emp.Name}」角色為：{string.Join("、", newRoles.Select(Roles.ToDisplay))}",
            AuditService.Diff(before, emp, AuditIgnore));
    }

    /// <summary>停用（離職）。部門主管與有直屬部屬者需先完成交接。</summary>
    public async Task DeactivateAsync(int id, int version, DateOnly? resignedDate)
    {
        var emp = await employees.GetByIdAsync(id) ?? throw new NotFoundException("員工", id);
        EnsureCanManage(emp);
        if (!emp.IsActive) return;
        if (id == currentUser.Id) throw new BusinessException("不可停用自己的帳號。");
        if (emp.HasRole(Roles.Admin)) await EnsureAnotherActiveAdminAsync(id);

        var headOf = (await departments.QueryAsync(d => d.IsActive && d.ManagerId == id)).Select(d => d.Name).ToList();
        if (headOf.Count > 0)
            throw new BusinessException($"「{emp.Name}」目前是 {string.Join("、", headOf)} 的部門主管，請先指定新的部門主管。");
        var reports = (await employees.QueryAsync(e => e.IsActive && e.ManagerId == id)).Select(e => e.Name).ToList();
        if (reports.Count > 0)
            throw new BusinessException($"「{emp.Name}」尚有 {reports.Count} 位直屬部屬（{string.Join("、", reports.Take(5))}{(reports.Count > 5 ? "…" : "")}），請先調整其直屬主管。");

        var date = resignedDate ?? clock.Today;
        if (date < emp.HireDate) throw new ValidationException("離職日不可早於到職日。");

        emp.IsActive = false;
        emp.ResignedDate = date;
        emp.SecurityStamp = NewStamp();
        emp.Version = version;
        await employees.UpdateAsync(emp);

        // 停用仍有效或未來的代理設定
        var today = clock.Today;
        var related = await delegations.QueryAsync(d => d.IsEnabled && d.EndDate >= today && (d.PrincipalId == id || d.DelegateId == id));
        foreach (var d in related)
        {
            d.IsEnabled = false;
            await delegations.UpdateAsync(d);
        }

        await audit.LogAsync("Disable", nameof(Employee), id.ToString(),
            $"停用員工「{emp.Name}」，離職日 {date:yyyy-MM-dd}" + (related.Count > 0 ? $"，並停用 {related.Count} 筆代理設定" : ""));
    }

    public async Task ReactivateAsync(int id, int version)
    {
        var emp = await employees.GetByIdAsync(id) ?? throw new NotFoundException("員工", id);
        EnsureCanManage(emp);
        if (emp.IsActive) return;
        var dept = await departments.GetByIdAsync(emp.DepartmentId);
        if (dept is not { IsActive: true }) throw new BusinessException("原部門已停用，請先編輯員工資料改到其他部門再復職。");
        if (emp.ManagerId is int m && await employees.GetByIdAsync(m) is { IsActive: false } mgr)
            throw new BusinessException($"原直屬主管「{mgr.Name}」已停用，請先編輯員工資料調整直屬主管。");

        emp.IsActive = true;
        emp.ResignedDate = null;
        emp.FailedLoginCount = 0;
        emp.LockoutEnd = null;
        emp.Version = version;
        await employees.UpdateAsync(emp);
        await audit.LogAsync("Enable", nameof(Employee), id.ToString(), $"復職員工「{emp.Name}」");
    }

    /// <summary>重設密碼為臨時密碼，下次登入須變更；同時登出該使用者所有裝置。</summary>
    public async Task<string> ResetPasswordAsync(int id, int version)
    {
        var emp = await employees.GetByIdAsync(id) ?? throw new NotFoundException("員工", id);
        EnsureCanManage(emp);
        if (!emp.IsActive) throw new BusinessException("帳號已停用，無法重設密碼。");
        var temp = await GenerateTempPasswordAsync();
        emp.PasswordHash = passwords.Hash(temp);
        emp.MustChangePassword = true;
        emp.PasswordChangedAt = clock.Now;
        emp.FailedLoginCount = 0;
        emp.LockoutEnd = null;
        emp.SecurityStamp = NewStamp();
        emp.Version = version;
        await employees.UpdateAsync(emp);
        await audit.LogAsync("ResetPassword", nameof(Employee), id.ToString(), $"重設「{emp.Name}」的密碼");
        return temp;
    }

    public async Task UnlockAsync(int id, int version)
    {
        var emp = await employees.GetByIdAsync(id) ?? throw new NotFoundException("員工", id);
        EnsureCanManage(emp);
        emp.FailedLoginCount = 0;
        emp.LockoutEnd = null;
        emp.Version = version;
        await employees.UpdateAsync(emp);
        await audit.LogAsync("Unlock", nameof(Employee), id.ToString(), $"解除「{emp.Name}」的登入鎖定");
    }

    /// <summary>目前在此員工身上、尚未處理的簽核關卡數</summary>
    public async Task<int> CountPendingTasksAsync(int id) =>
        (await documents.QueryAsync(d => d.Status == DocumentStatus.InProgress || d.Status == DocumentStatus.Overdue))
        .Count(d => d.GetCurrentStep() is { } s && s.Assignees.Any(a => a.EmployeeId == id && a.Decision == AssigneeDecision.Pending));

    public bool CanManage(Employee target) =>
        (currentUser.IsInRole(Roles.HR) || currentUser.IsInRole(Roles.Admin)) &&
        (!target.HasRole(Roles.Admin) || currentUser.IsInRole(Roles.Admin));

    // ---------------- 內部 ----------------

    private void EnsureHrOrAdmin()
    {
        if (!currentUser.IsInRole(Roles.HR) && !currentUser.IsInRole(Roles.Admin))
            throw new ForbiddenException();
    }

    private void EnsureCanManage(Employee target)
    {
        EnsureHrOrAdmin();
        if (!CanManage(target)) throw new ForbiddenException("系統管理員帳號只能由系統管理員維護。");
    }

    private async Task EnsureAnotherActiveAdminAsync(int exceptId)
    {
        var others = (await employees.QueryAsync(e => e.IsActive && e.Id != exceptId)).Count(e => e.HasRole(Roles.Admin));
        if (others == 0) throw new BusinessException("系統至少需保留一位在職的系統管理員。");
    }

    private async Task ValidateAsync(EmployeeInput input, IReadOnlyList<Employee> all, Employee? existing)
    {
        var no = input.EmployeeNo.Trim();
        var account = input.Account.Trim();
        var email = input.Email.Trim();
        if (all.Any(e => e.Id != input.Id && e.EmployeeNo.Equals(no, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException($"員工編號「{no}」已被使用。", nameof(input.EmployeeNo));
        if (all.Any(e => e.Id != input.Id && e.Account.Equals(account, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException($"帳號「{account}」已被使用（含已離職人員）。", nameof(input.Account));
        if (all.Any(e => e.Id != input.Id && e.Email.Equals(email, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException($"Email「{email}」已被使用。", nameof(input.Email));

        var dept = await departments.GetByIdAsync(input.DepartmentId) ?? throw new ValidationException("部門不存在。", nameof(input.DepartmentId));
        if (!dept.IsActive && existing?.DepartmentId != dept.Id)
            throw new ValidationException($"部門「{dept.Name}」已停用。", nameof(input.DepartmentId));
        var title = await titles.GetByIdAsync(input.JobTitleId) ?? throw new ValidationException("職稱不存在。", nameof(input.JobTitleId));
        if (!title.IsActive && existing?.JobTitleId != title.Id)
            throw new ValidationException($"職稱「{title.Name}」已停用。", nameof(input.JobTitleId));

        if (input.HireDate is { } hire && hire > clock.Today.AddYears(1))
            throw new ValidationException("到職日不可超過一年後。", nameof(input.HireDate));

        if (input.ManagerId is int mid)
        {
            if (mid == input.Id) throw new ValidationException("直屬主管不可為自己。", nameof(input.ManagerId));
            var map = all.ToDictionary(e => e.Id);
            if (!map.TryGetValue(mid, out var mgr)) throw new ValidationException("直屬主管不存在。", nameof(input.ManagerId));
            if (!mgr.IsActive) throw new ValidationException($"「{mgr.Name}」已停用，不可擔任直屬主管。", nameof(input.ManagerId));
            // 防止主管鏈循環
            var cursor = mgr;
            for (var guard = 0; cursor is not null && guard < 100; guard++)
            {
                if (input.Id != 0 && cursor.Id == input.Id)
                    throw new ValidationException("直屬主管設定會形成循環（主管是此員工的部屬）。", nameof(input.ManagerId));
                cursor = cursor.ManagerId is int up ? map.GetValueOrDefault(up) : null;
            }
        }
    }

    private static List<string> NormalizeRoles(IEnumerable<string>? roles)
    {
        var set = (roles ?? []).Where(r => Roles.All.Contains(r)).ToHashSet();
        set.Add(Roles.Applicant); // 每位員工都能提出申請
        return Roles.All.Where(set.Contains).ToList();
    }

    private async Task<string> GenerateTempPasswordAsync()
    {
        var policy = await settings.GetPasswordPolicyAsync();
        const string upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        const string lower = "abcdefghijkmnpqrstuvwxyz";
        const string digits = "23456789";
        const string symbols = "!@#$%*?";
        var length = Math.Max(policy.MinLength, 12);
        var chars = new List<char>
        {
            upper[RandomNumberGenerator.GetInt32(upper.Length)],
            lower[RandomNumberGenerator.GetInt32(lower.Length)],
            digits[RandomNumberGenerator.GetInt32(digits.Length)],
            symbols[RandomNumberGenerator.GetInt32(symbols.Length)]
        };
        var pool = upper + lower + digits;
        while (chars.Count < length) chars.Add(pool[RandomNumberGenerator.GetInt32(pool.Length)]);
        // Fisher–Yates 洗牌
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }
        return new string(chars.ToArray());
    }

    private static string NewStamp() => Guid.NewGuid().ToString("N");
    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
