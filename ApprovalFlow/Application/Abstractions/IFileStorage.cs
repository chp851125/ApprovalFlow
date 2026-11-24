namespace ApprovalFlow.Application.Abstractions;

/// <summary>附件實體檔儲存（本機資料夾實作位於 Infrastructure）</summary>
public interface IFileStorage
{
    /// <summary>儲存檔案並回傳系統產生的儲存名稱（不使用使用者提供的檔名，避免路徑攻擊）</summary>
    Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default);
    /// <summary>開啟檔案；不存在時回傳 null</summary>
    Stream? OpenRead(string storedName);
    void Delete(string storedName);
}
