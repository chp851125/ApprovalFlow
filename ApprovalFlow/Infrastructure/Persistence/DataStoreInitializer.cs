using ApprovalFlow.Application.Abstractions;
using Microsoft.Extensions.Options;

namespace ApprovalFlow.Infrastructure.Persistence;

/// <summary>
/// 啟動時確保執行期資料夾存在；首次啟動（或檔案缺漏）時，把 SeedData/*.json 複製到 App_Data。
/// </summary>
public sealed class DataStoreInitializer(IOptions<DataStoreOptions> options, ILogger<DataStoreInitializer> logger)
{
    private readonly DataStoreOptions _o = options.Value;

    public void EnsureInitialized()
    {
        Directory.CreateDirectory(_o.DataFullPath);
        Directory.CreateDirectory(_o.UploadFullPath);
        Directory.CreateDirectory(_o.OutboxFullPath);

        if (!Directory.Exists(_o.SeedFullPath))
        {
            logger.LogWarning("找不到範例資料夾 {Path}", _o.SeedFullPath);
            return;
        }

        foreach (var src in Directory.GetFiles(_o.SeedFullPath, "*.json"))
        {
            var dest = Path.Combine(_o.DataFullPath, Path.GetFileName(src));
            if (File.Exists(dest)) continue;
            File.Copy(src, dest);
            logger.LogInformation("已從範例資料建立 {File}", Path.GetFileName(src));
        }
    }

    /// <summary>以範例資料覆蓋所有資料檔，並清除上傳附件</summary>
    public void OverwriteFromSeed()
    {
        Directory.CreateDirectory(_o.DataFullPath);
        foreach (var src in Directory.GetFiles(_o.SeedFullPath, "*.json"))
        {
            var dest = Path.Combine(_o.DataFullPath, Path.GetFileName(src));
            var tmp = dest + ".tmp";
            File.Copy(src, tmp, overwrite: true);
            File.Move(tmp, dest, overwrite: true);
        }
        if (Directory.Exists(_o.UploadFullPath))
        {
            foreach (var f in Directory.GetFiles(_o.UploadFullPath)) File.Delete(f);
            foreach (var d in Directory.GetDirectories(_o.UploadFullPath)) Directory.Delete(d, recursive: true);
        }
        // 模擬郵件的寄送紀錄已重設，對應的 .eml 也一併清除
        if (Directory.Exists(_o.OutboxFullPath))
        {
            foreach (var d in Directory.GetDirectories(_o.OutboxFullPath)) Directory.Delete(d, recursive: true);
        }
        logger.LogWarning("資料已重設為範例資料");
    }
}

/// <summary>重設為範例資料：覆蓋檔案後通知所有 Repository 重新載入</summary>
public sealed class JsonDataStoreAdmin(DataStoreInitializer initializer, IEnumerable<IReloadableStore> stores) : IDataStoreAdmin
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task ResetToSeedAsync(CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            initializer.OverwriteFromSeed();
            foreach (var s in stores) s.Reload();
        }
        finally
        {
            Gate.Release();
        }
    }
}
