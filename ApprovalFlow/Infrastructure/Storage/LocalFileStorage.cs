using System.Text.RegularExpressions;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ApprovalFlow.Infrastructure.Storage;

/// <summary>附件存放於 App_Data/uploads/{yyyyMM}/{隨機名稱}{副檔名}</summary>
public sealed partial class LocalFileStorage(IOptions<DataStoreOptions> options) : IFileStorage
{
    private readonly string _root = options.Value.UploadFullPath;

    [GeneratedRegex(@"^\d{6}/[0-9a-f]{32}\.[a-z0-9]{1,8}$")]
    private static partial Regex StoredNamePattern();

    public async Task<string> SaveAsync(Stream content, string extension, CancellationToken ct = default)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        var name = $"{DateTime.UtcNow:yyyyMM}/{Guid.NewGuid():N}.{ext}";
        var path = Resolve(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await content.CopyToAsync(fs, ct);
        File.Move(temp, path);
        return name;
    }

    public Stream? OpenRead(string storedName)
    {
        var path = Resolve(storedName);
        return File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    public void Delete(string storedName)
    {
        var path = Resolve(storedName);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>只接受系統產生格式的名稱，並確認最終路徑仍在上傳資料夾內</summary>
    private string Resolve(string storedName)
    {
        if (!StoredNamePattern().IsMatch(storedName)) throw new InvalidOperationException("不合法的檔案名稱。");
        var full = Path.GetFullPath(Path.Combine(_root, storedName));
        if (!full.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("不合法的檔案路徑。");
        return full;
    }
}
