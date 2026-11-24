namespace ApprovalFlow.Infrastructure.Persistence;

/// <summary>資料儲存路徑設定（相對於 ContentRoot）</summary>
public sealed class DataStoreOptions
{
    public const string Section = "DataStore";

    /// <summary>範例資料（進版控）</summary>
    public string SeedPath { get; set; } = "SeedData";
    /// <summary>執行期資料（不進版控）</summary>
    public string DataPath { get; set; } = "App_Data/data";
    public string UploadPath { get; set; } = "App_Data/uploads";
    public string OutboxPath { get; set; } = "outbox";

    // 以下由啟動程式解析為絕對路徑
    public string SeedFullPath { get; set; } = "";
    public string DataFullPath { get; set; } = "";
    public string UploadFullPath { get; set; } = "";
    public string OutboxFullPath { get; set; } = "";

    public void Resolve(string contentRoot)
    {
        SeedFullPath = Path.GetFullPath(Path.Combine(contentRoot, SeedPath));
        DataFullPath = Path.GetFullPath(Path.Combine(contentRoot, DataPath));
        UploadFullPath = Path.GetFullPath(Path.Combine(contentRoot, UploadPath));
        OutboxFullPath = Path.GetFullPath(Path.Combine(contentRoot, OutboxPath));
    }
}
