using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Infrastructure;
using ApprovalFlow.Infrastructure.Persistence;
using ApprovalFlow.Web.Infrastructure;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ApprovalFlow.Web.Controllers;

/// <summary>資料維護：查看資料檔案狀態、重設為範例資料（系統管理員）</summary>
[Authorize(Policy = Policies.Admin)]
public class MaintenanceController(
    IDataStoreAdmin dataStore,
    IEnumerable<IReloadableStore> stores,
    IOptions<DataStoreOptions> options,
    AuditService audit,
    IWebHostEnvironment env,
    ILogger<MaintenanceController> logger) : Controller
{
    public const string ConfirmText = "RESET";

    public IActionResult Index()
    {
        var o = options.Value;
        var files = stores.Select(s =>
        {
            var path = Path.Combine(o.DataFullPath, s.FileName);
            var info = new FileInfo(path);
            return new DataFileInfo(s.FileName, info.Exists ? info.Length : 0, info.Exists ? info.LastWriteTime : null);
        }).OrderBy(f => f.FileName).ToList();

        return View(new MaintenanceViewModel
        {
            Files = files,
            DataPath = Relative(o.DataFullPath),
            UploadPath = Relative(o.UploadFullPath),
            OutboxPath = Relative(o.OutboxFullPath),
            SeedPath = Relative(o.SeedFullPath),
            UploadCount = Directory.Exists(o.UploadFullPath) ? Directory.EnumerateFiles(o.UploadFullPath, "*", SearchOption.AllDirectories).Count() : 0,
            OutboxCount = Directory.Exists(o.OutboxFullPath) ? Directory.EnumerateFiles(o.OutboxFullPath, "*.eml", SearchOption.AllDirectories).Count() : 0,
            ConfirmText = ConfirmText
        });
    }

    /// <summary>以 SeedData 覆蓋目前資料，並清除上傳附件與模擬郵件。需輸入確認字串。</summary>
    [HttpPost]
    public async Task<IActionResult> Reset(string? confirm, CancellationToken ct)
    {
        if (!string.Equals(confirm?.Trim(), ConfirmText, StringComparison.Ordinal))
        {
            TempData.SetError($"請在確認欄位輸入「{ConfirmText}」才會執行重設。");
            return RedirectToAction(nameof(Index));
        }
        var who = User.Identity?.Name;
        await dataStore.ResetToSeedAsync(ct);
        logger.LogWarning("資料已由 {User} 重設為範例資料", who);
        // 稽核紀錄也已被重設，重設後再寫入一筆
        await audit.LogAsync("ResetData", "System", null, $"{who} 將所有資料重設為範例資料（附件與模擬郵件一併清除）");
        TempData.SetSuccess("已重設為範例資料。若您的帳號資料有變動，可能需要重新登入。");
        return RedirectToAction(nameof(Index));
    }

    private string Relative(string full)
    {
        var root = env.ContentRootPath;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full[root.Length..].TrimStart('\\', '/') : full;
    }
}
