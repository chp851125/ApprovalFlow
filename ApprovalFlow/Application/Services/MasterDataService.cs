using System.Globalization;
using System.Net.Mail;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;

namespace ApprovalFlow.Application.Services;

/// <summary>基本資料維護：假別、費用類別、假日行事曆、系統參數</summary>
public sealed class MasterDataService(
    IRepository<LeaveType> leaveTypes,
    IRepository<ExpenseCategory> expenseCategories,
    IRepository<BudgetSubject> budgetSubjects,
    IRepository<Holiday> holidays,
    IRepository<SystemSetting> systemSettings,
    AuditService audit)
{
    // ---------------- 假別 ----------------

    public async Task<LeaveType> SaveLeaveTypeAsync(LeaveTypeInput input)
    {
        var code = input.Code.Trim().ToUpperInvariant();
        var name = input.Name.Trim();
        var all = await leaveTypes.GetAllAsync();
        if (all.Any(x => x.Id != input.Id && x.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException($"假別代碼「{code}」已被使用。", nameof(input.Code));
        if (all.Any(x => x.Id != input.Id && x.Name == name))
            throw new ValidationException($"假別名稱「{name}」已存在。", nameof(input.Name));
        if (input.UseSeniorityQuota && input.DefaultQuotaHours is not null)
            throw new ValidationException("依年資計算額度時，年度額度請留白（由年資自動計算）。", nameof(input.DefaultQuotaHours));
        if (input.MinUnitHours * 2 != Math.Floor(input.MinUnitHours * 2))
            throw new ValidationException("最小請假單位需為 0.5 小時的倍數。", nameof(input.MinUnitHours));

        LeaveType? before = null;
        var entity = input.Id == 0 ? new LeaveType() : await leaveTypes.GetByIdAsync(input.Id) ?? throw new NotFoundException("假別", input.Id);
        if (input.Id != 0) before = await leaveTypes.GetByIdAsync(input.Id);

        entity.Code = code;
        entity.Name = name;
        entity.DefaultQuotaHours = input.DefaultQuotaHours;
        entity.UseSeniorityQuota = input.UseSeniorityQuota;
        entity.PayRatio = input.PayRatio;
        entity.MinUnitHours = input.MinUnitHours;
        entity.RequiresAttachment = input.RequiresAttachment;
        entity.Description = Clean(input.Description);
        entity.SortOrder = input.SortOrder;
        entity.IsActive = input.IsActive;
        return await SaveAsync(leaveTypes, entity, before, input.Version, "假別", name);
    }

    // ---------------- 費用類別 ----------------

    public async Task<ExpenseCategory> SaveExpenseCategoryAsync(ExpenseCategoryInput input)
    {
        var code = input.Code.Trim().ToUpperInvariant();
        var name = input.Name.Trim();
        var all = await expenseCategories.GetAllAsync();
        if (all.Any(x => x.Id != input.Id && x.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException($"類別代碼「{code}」已被使用。", nameof(input.Code));
        if (all.Any(x => x.Id != input.Id && x.Name == name))
            throw new ValidationException($"類別名稱「{name}」已存在。", nameof(input.Name));
        var subject = await budgetSubjects.GetByIdAsync(input.BudgetSubjectId)
                      ?? throw new ValidationException("預算科目不存在。", nameof(input.BudgetSubjectId));
        if (!subject.IsActive) throw new ValidationException($"預算科目「{subject.Name}」已停用。", nameof(input.BudgetSubjectId));

        ExpenseCategory? before = null;
        var entity = input.Id == 0 ? new ExpenseCategory() : await expenseCategories.GetByIdAsync(input.Id) ?? throw new NotFoundException("費用類別", input.Id);
        if (input.Id != 0) before = await expenseCategories.GetByIdAsync(input.Id);

        entity.Code = code;
        entity.Name = name;
        entity.BudgetSubjectId = input.BudgetSubjectId;
        entity.MaxAmountPerItem = input.MaxAmountPerItem;
        entity.RequiresReceipt = input.RequiresReceipt;
        entity.Description = Clean(input.Description);
        entity.SortOrder = input.SortOrder;
        entity.IsActive = input.IsActive;
        return await SaveAsync(expenseCategories, entity, before, input.Version, "費用類別", name);
    }

    // ---------------- 假日行事曆 ----------------

    public async Task<Holiday> SaveHolidayAsync(HolidayInput input)
    {
        var date = input.Date!.Value;
        var name = input.Name.Trim();
        var clash = (await holidays.QueryAsync(h => h.Date == date && h.Id != input.Id)).FirstOrDefault();
        if (clash is not null)
            throw new ValidationException($"{date:yyyy/MM/dd} 已設定為「{clash.Name}」。", nameof(input.Date));

        var weekend = date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
        if (input.Type == Domain.Enums.HolidayType.MakeupWorkday && !weekend)
            throw new ValidationException("補班日應設定在週六或週日。", nameof(input.Type));

        Holiday? before = null;
        var entity = input.Id == 0 ? new Holiday() : await holidays.GetByIdAsync(input.Id) ?? throw new NotFoundException("假日", input.Id);
        if (input.Id != 0) before = await holidays.GetByIdAsync(input.Id);
        entity.Date = date;
        entity.Name = name;
        entity.Type = input.Type;
        return await SaveAsync(holidays, entity, before, input.Version, "假日", $"{date:yyyy/MM/dd} {name}");
    }

    public async Task DeleteHolidayAsync(int id)
    {
        var h = await holidays.GetByIdAsync(id) ?? throw new NotFoundException("假日", id);
        await holidays.DeleteAsync(id);
        await audit.LogAsync("Delete", nameof(Holiday), id.ToString(), $"刪除假日「{h.Date:yyyy/MM/dd} {h.Name}」",
            AuditService.Diff<Holiday>(h, null));
    }

    // ---------------- 系統參數 ----------------

    /// <summary>參數值範圍限制（系統規則，非資料）</summary>
    private static readonly Dictionary<string, (int Min, int Max)> IntRanges = new()
    {
        [SettingKeys.PasswordMinLength] = (6, 64),
        [SettingKeys.LockoutThreshold] = (3, 20),
        [SettingKeys.LockoutMinutes] = (1, 1440),
        [SettingKeys.UploadMaxSizeMb] = (1, 50),
        [SettingKeys.UploadMaxFilesPerDocument] = (1, 20),
        [SettingKeys.SlaDefaultHours] = (1, 240),
        [SettingKeys.SlaReminderBeforeHours] = (0, 72),
        [SettingKeys.SlaEscalateAfterHours] = (1, 240),
        [SettingKeys.DigestHour] = (0, 23)
    };

    /// <summary>批次更新系統參數；只更新有變動的項目，回傳變動筆數</summary>
    public async Task<int> UpdateSettingsAsync(IEnumerable<SettingChange> changes)
    {
        var pending = new List<(SystemSetting Before, SystemSetting After)>();
        foreach (var c in changes)
        {
            var s = await systemSettings.GetByIdAsync(c.Id) ?? throw new NotFoundException("系統參數", c.Id);
            var value = NormalizeSetting(s, c.Value);
            if (value == s.Value) continue;
            if (s.Version != c.Version) throw new ConcurrencyException("系統參數", s.Id);
            var before = (await systemSettings.GetByIdAsync(c.Id))!;
            s.Value = value;
            s.Version = c.Version;
            pending.Add((before, s));
        }

        // 先完成所有驗證再寫入；逐筆寫入時若遇到並行衝突，已寫入的項目仍保留並提示重新整理
        foreach (var (before, after) in pending)
        {
            await systemSettings.UpdateAsync(after);
            await audit.LogAsync("Update", nameof(SystemSetting), after.Id.ToString(),
                $"修改系統參數「{after.Description}」（{after.Key}）", AuditService.Diff(before, after));
        }
        return pending.Count;
    }

    private static string NormalizeSetting(SystemSetting s, string? raw)
    {
        var v = (raw ?? "").Trim();
        var label = string.IsNullOrEmpty(s.Description) ? s.Key : s.Description;
        switch (s.ValueType)
        {
            case "bool":
                return string.Equals(v, "true", StringComparison.OrdinalIgnoreCase) ? "true" : "false";
            case "int":
                if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    throw new ValidationException($"「{label}」必須是整數。", s.Key);
                if (IntRanges.TryGetValue(s.Key, out var r) && (i < r.Min || i > r.Max))
                    throw new ValidationException($"「{label}」需介於 {r.Min}~{r.Max}。", s.Key);
                return i.ToString(CultureInfo.InvariantCulture);
            case "decimal":
                if (!decimal.TryParse(v, NumberStyles.Number, CultureInfo.InvariantCulture, out var d))
                    throw new ValidationException($"「{label}」必須是數字。", s.Key);
                return d.ToString(CultureInfo.InvariantCulture);
            default:
                if (v.Length > 500) throw new ValidationException($"「{label}」最多 500 個字元。", s.Key);
                if (s.Key is SettingKeys.CompanyName && v.Length == 0)
                    throw new ValidationException("公司名稱不可空白。", s.Key);
                if (s.Key is SettingKeys.EmailFromAddress && !MailAddress.TryCreate(v, out _))
                    throw new ValidationException("寄件者 Email 格式不正確。", s.Key);
                if (s.Key is SettingKeys.SiteBaseUrl &&
                    !(Uri.TryCreate(v, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)))
                    throw new ValidationException("系統網址需為 http:// 或 https:// 開頭的完整網址。", s.Key);
                if (s.Key is SettingKeys.UploadAllowedExtensions)
                {
                    var exts = v.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => (x.StartsWith('.') ? x : "." + x).ToLowerInvariant()).Distinct().ToList();
                    if (exts.Count == 0) throw new ValidationException("至少需允許一種副檔名。", s.Key);
                    string[] blocked = [".exe", ".dll", ".bat", ".cmd", ".ps1", ".js", ".vbs", ".html", ".htm", ".svg", ".msi", ".sh", ".com", ".scr"];
                    var bad = exts.Intersect(blocked).ToList();
                    if (bad.Count > 0) throw new ValidationException($"基於安全性，不可允許上傳：{string.Join("、", bad)}", s.Key);
                    if (exts.Any(x => x.Length > 10 || x[1..].Any(ch => !char.IsLetterOrDigit(ch))))
                        throw new ValidationException("副檔名格式不正確。", s.Key);
                    return string.Join(",", exts);
                }
                return v;
        }
    }

    // ---------------- 共用 ----------------

    private async Task<T> SaveAsync<T>(IRepository<T> repo, T entity, T? before, int version, string label, string name)
        where T : class, Domain.Common.IEntity
    {
        if (entity.Id == 0)
        {
            var created = await repo.AddAsync(entity);
            await audit.LogAsync("Create", typeof(T).Name, created.Id.ToString(), $"新增{label}「{name}」", AuditService.Diff<T>(null, created));
            return created;
        }
        entity.Version = version;
        await repo.UpdateAsync(entity);
        await audit.LogAsync("Update", typeof(T).Name, entity.Id.ToString(), $"修改{label}「{name}」", AuditService.Diff(before, entity));
        return entity;
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
