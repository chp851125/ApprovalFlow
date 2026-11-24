using System.Globalization;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;

namespace ApprovalFlow.Application.Services;

public sealed record PasswordPolicy(int MinLength, bool RequireDigit, bool RequireUpper, bool RequireLower, bool RequireSymbol)
{
    public string Describe()
    {
        var parts = new List<string> { $"至少 {MinLength} 個字元" };
        if (RequireUpper) parts.Add("含大寫英文");
        if (RequireLower) parts.Add("含小寫英文");
        if (RequireDigit) parts.Add("含數字");
        if (RequireSymbol) parts.Add("含符號");
        return string.Join("、", parts);
    }

    public IReadOnlyList<string> Validate(string? password)
    {
        var errors = new List<string>();
        password ??= "";
        if (password.Length < MinLength) errors.Add($"密碼長度至少需 {MinLength} 個字元");
        if (RequireUpper && !password.Any(char.IsUpper)) errors.Add("密碼需包含大寫英文字母");
        if (RequireLower && !password.Any(char.IsLower)) errors.Add("密碼需包含小寫英文字母");
        if (RequireDigit && !password.Any(char.IsDigit)) errors.Add("密碼需包含數字");
        if (RequireSymbol && password.All(char.IsLetterOrDigit)) errors.Add("密碼需包含符號");
        return errors;
    }
}

/// <summary>系統參數讀取。未設定時使用安全的預設值。</summary>
public sealed class SettingsService(IRepository<SystemSetting> repo)
{
    public async Task<string> GetStringAsync(string key, string fallback = "")
    {
        var item = (await repo.QueryAsync(s => s.Key == key)).FirstOrDefault();
        return item?.Value ?? fallback;
    }

    public async Task<int> GetIntAsync(string key, int fallback) =>
        int.TryParse(await GetStringAsync(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public async Task<decimal> GetDecimalAsync(string key, decimal fallback) =>
        decimal.TryParse(await GetStringAsync(key), NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : fallback;

    public async Task<bool> GetBoolAsync(string key, bool fallback) =>
        bool.TryParse(await GetStringAsync(key), out var v) ? v : fallback;

    public async Task<PasswordPolicy> GetPasswordPolicyAsync() => new(
        await GetIntAsync(SettingKeys.PasswordMinLength, 8),
        await GetBoolAsync(SettingKeys.PasswordRequireDigit, true),
        await GetBoolAsync(SettingKeys.PasswordRequireUpper, true),
        await GetBoolAsync(SettingKeys.PasswordRequireLower, true),
        await GetBoolAsync(SettingKeys.PasswordRequireSymbol, false));

    public async Task<IReadOnlyList<string>> GetAllowedExtensionsAsync()
    {
        var raw = await GetStringAsync(SettingKeys.UploadAllowedExtensions, ".pdf,.jpg,.jpeg,.png");
        return raw.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => x.StartsWith('.') ? x.ToLowerInvariant() : "." + x.ToLowerInvariant())
            .Distinct().ToList();
    }
}
