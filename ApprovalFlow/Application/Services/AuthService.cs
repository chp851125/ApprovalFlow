using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;

namespace ApprovalFlow.Application.Services;

public sealed record LoginResult(bool Succeeded, string? Error, Employee? Employee)
{
    public static LoginResult Fail(string error) => new(false, error, null);
}

/// <summary>登入驗證（密碼雜湊、失敗鎖定）</summary>
public sealed class AuthService(
    IRepository<Employee> employees,
    IPasswordService passwords,
    SettingsService settings,
    AuditService audit,
    IClock clock)
{
    private const string GenericError = "帳號或密碼錯誤。";

    public async Task<LoginResult> LoginAsync(string account, string password)
    {
        account = (account ?? "").Trim();
        var emp = (await employees.QueryAsync(e => e.Account.ToLower() == account.ToLower())).FirstOrDefault();
        if (emp is null)
        {
            await audit.LogAsync("LoginFailed", nameof(Employee), null, $"登入失敗：帳號「{account}」不存在", userName: account);
            return LoginResult.Fail(GenericError);
        }

        if (!emp.IsActive)
        {
            await audit.LogAsync("LoginFailed", nameof(Employee), emp.Id.ToString(), "登入失敗：帳號已停用", userId: emp.Id, userName: emp.Name);
            return LoginResult.Fail("此帳號已停用，請洽系統管理員。");
        }

        var now = clock.Now;
        if (emp.LockoutEnd is { } end && end > now)
        {
            await audit.LogAsync("LoginFailed", nameof(Employee), emp.Id.ToString(), "登入失敗：帳號鎖定中", userId: emp.Id, userName: emp.Name);
            return LoginResult.Fail($"登入失敗次數過多，帳號已暫時鎖定，請於 {clock.ToLocal(end):HH:mm} 後再試。");
        }

        var check = passwords.Verify(emp.PasswordHash, password ?? "");
        if (check == PasswordCheckResult.Failed)
        {
            var threshold = await settings.GetIntAsync(SettingKeys.LockoutThreshold, 5);
            var minutes = await settings.GetIntAsync(SettingKeys.LockoutMinutes, 15);
            var locked = false;
            await SaveWithRetryAsync(emp.Id, e =>
            {
                e.FailedLoginCount++;
                if (e.FailedLoginCount >= threshold)
                {
                    e.LockoutEnd = now.AddMinutes(minutes);
                    e.FailedLoginCount = 0;
                    locked = true;
                }
            });
            await audit.LogAsync(locked ? "AccountLocked" : "LoginFailed", nameof(Employee), emp.Id.ToString(),
                locked ? $"連續登入失敗達 {threshold} 次，鎖定 {minutes} 分鐘" : "登入失敗：密碼錯誤",
                userId: emp.Id, userName: emp.Name);
            return LoginResult.Fail(locked
                ? $"登入失敗次數過多，帳號已鎖定 {minutes} 分鐘。"
                : GenericError);
        }

        var updated = await SaveWithRetryAsync(emp.Id, e =>
        {
            e.FailedLoginCount = 0;
            e.LockoutEnd = null;
            e.LastLoginAt = now;
            if (check == PasswordCheckResult.SuccessRehashNeeded) e.PasswordHash = passwords.Hash(password!);
        });
        await audit.LogAsync("Login", nameof(Employee), emp.Id.ToString(), "登入成功", userId: emp.Id, userName: emp.Name);
        return new LoginResult(true, null, updated);
    }

    /// <summary>使用者自行變更密碼。成功後會更新 SecurityStamp，呼叫端需以新的身分重新登入（其他裝置會被登出）。</summary>
    public async Task<Employee> ChangePasswordAsync(int employeeId, string currentPassword, string newPassword)
    {
        var emp = await employees.GetByIdAsync(employeeId) ?? throw new NotFoundException(nameof(Employee), employeeId);
        if (passwords.Verify(emp.PasswordHash, currentPassword ?? "") == PasswordCheckResult.Failed)
            throw new ValidationException("目前密碼不正確。", "CurrentPassword");
        if (currentPassword == newPassword)
            throw new ValidationException("新密碼不可與目前密碼相同。", "NewPassword");
        var policy = await settings.GetPasswordPolicyAsync();
        var errors = policy.Validate(newPassword);
        if (errors.Count > 0) throw new ValidationException(string.Join("；", errors) + "。", "NewPassword");
        if (newPassword.Contains(emp.Account, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("密碼不可包含帳號。", "NewPassword");

        var updated = await SaveWithRetryAsync(employeeId, e =>
        {
            e.PasswordHash = passwords.Hash(newPassword);
            e.MustChangePassword = false;
            e.PasswordChangedAt = clock.Now;
            e.SecurityStamp = Guid.NewGuid().ToString("N");
        });
        await audit.LogAsync("ChangePassword", nameof(Employee), employeeId.ToString(), "使用者變更密碼", userId: emp.Id, userName: emp.Name);
        return updated;
    }

    /// <summary>以最新版本套用變更；遇到並行衝突時重新讀取再試（僅用於系統性欄位）</summary>
    private async Task<Employee> SaveWithRetryAsync(int id, Action<Employee> apply)
    {
        for (var attempt = 0; ; attempt++)
        {
            var e = await employees.GetByIdAsync(id) ?? throw new NotFoundException(nameof(Employee), id);
            apply(e);
            try
            {
                await employees.UpdateAsync(e);
                return e;
            }
            catch (ConcurrencyException) when (attempt < 3)
            {
                // 重試
            }
        }
    }
}
