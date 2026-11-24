namespace ApprovalFlow.Application.Abstractions;

/// <summary>時間來源；系統一律使用台北時間（Asia/Taipei）</summary>
public interface IClock
{
    DateTimeOffset Now { get; }
    DateOnly Today { get; }
    /// <summary>將任意時間轉為台北時間</summary>
    DateTimeOffset ToLocal(DateTimeOffset value);
}

/// <summary>目前登入者資訊（由 Web 層實作）</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    int? Id { get; }
    string Name { get; }
    string? IpAddress { get; }
    bool IsInRole(string role);
}

/// <summary>密碼雜湊</summary>
public interface IPasswordService
{
    string Hash(string password);
    PasswordCheckResult Verify(string hash, string password);
}

public enum PasswordCheckResult
{
    Failed,
    Success,
    SuccessRehashNeeded
}

/// <summary>資料儲存管理（重設為範例資料等）</summary>
public interface IDataStoreAdmin
{
    /// <summary>以範例資料覆蓋目前資料並重新載入</summary>
    Task ResetToSeedAsync(CancellationToken ct = default);
}
