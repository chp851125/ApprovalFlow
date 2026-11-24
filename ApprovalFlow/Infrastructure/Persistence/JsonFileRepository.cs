using System.Linq.Expressions;
using System.Text.Json;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Domain.Common;
using Microsoft.Extensions.Options;

namespace ApprovalFlow.Infrastructure.Persistence;

/// <summary>可重新載入的儲存體（重設範例資料時使用）</summary>
public interface IReloadableStore
{
    string FileName { get; }
    void Reload();
}

/// <summary>
/// 以 JSON 檔案為儲存媒介的 Repository：
/// <list type="bullet">
/// <item>啟動時載入記憶體；讀取回傳深複製副本，避免呼叫端繞過版本檢查直接改到快取。</item>
/// <item>異動時先寫入暫存檔再以原子性取代正式檔，避免寫到一半造成檔案毀損。</item>
/// <item>以鎖保護，執行緒安全；以 Version 欄位做樂觀並行控制。</item>
/// </list>
/// </summary>
public sealed class JsonFileRepository<T> : IRepository<T>, IReloadableStore where T : class, IEntity
{
    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly ILogger<JsonFileRepository<T>> _logger;
    private List<T> _items = [];

    public string FileName { get; }

    public JsonFileRepository(IOptions<DataStoreOptions> options, ILogger<JsonFileRepository<T>> logger)
    {
        _logger = logger;
        FileName = JsonDefaults.FileNameFor(typeof(T));
        _filePath = Path.Combine(options.Value.DataFullPath, FileName);
        Reload();
    }

    public void Reload()
    {
        lock (_gate)
        {
            if (!File.Exists(_filePath))
            {
                _items = [];
                _logger.LogWarning("資料檔不存在，以空集合啟動：{File}", _filePath);
                return;
            }
            try
            {
                var bytes = File.ReadAllBytes(_filePath);
                _items = bytes.Length == 0
                    ? []
                    : JsonSerializer.Deserialize<List<T>>(bytes, JsonDefaults.Options) ?? [];
                _logger.LogInformation("已載入 {Count} 筆 {Entity}（{File}）", _items.Count, typeof(T).Name, FileName);
            }
            catch (JsonException ex)
            {
                _logger.LogError(ex, "資料檔格式錯誤：{File}", _filePath);
                throw new InvalidOperationException($"資料檔 {FileName} 格式錯誤：{ex.Message}", ex);
            }
        }
    }

    public Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            IReadOnlyList<T> result = _items.Select(Clone).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<T?> GetByIdAsync(int id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(x => x.Id == id);
            return Task.FromResult(item is null ? null : Clone(item));
        }
    }

    public Task<IReadOnlyList<T>> QueryAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default)
    {
        var func = predicate.Compile();
        lock (_gate)
        {
            IReadOnlyList<T> result = _items.Where(func).Select(Clone).ToList();
            return Task.FromResult(result);
        }
    }

    public Task<T> AddAsync(T entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        lock (_gate)
        {
            if (entity.Id == 0)
                entity.Id = _items.Count == 0 ? 1 : _items.Max(x => x.Id) + 1;
            else if (_items.Any(x => x.Id == entity.Id))
                throw new InvalidOperationException($"{typeof(T).Name} Id {entity.Id} 已存在。");

            entity.Version = 1;
            var stored = Clone(entity);
            _items.Add(stored);
            try
            {
                Persist();
            }
            catch
            {
                _items.Remove(stored);
                throw;
            }
            return Task.FromResult(Clone(stored));
        }
    }

    public Task UpdateAsync(T entity, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        lock (_gate)
        {
            var index = _items.FindIndex(x => x.Id == entity.Id);
            if (index < 0) throw new NotFoundException(typeof(T).Name, entity.Id);

            var current = _items[index];
            if (current.Version != entity.Version)
                throw new ConcurrencyException(typeof(T).Name, entity.Id);

            entity.Version = current.Version + 1;
            _items[index] = Clone(entity);
            try
            {
                Persist();
            }
            catch
            {
                _items[index] = current;
                entity.Version = current.Version;
                throw;
            }
            return Task.CompletedTask;
        }
    }

    public Task DeleteAsync(int id, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var index = _items.FindIndex(x => x.Id == id);
            if (index < 0) throw new NotFoundException(typeof(T).Name, id);
            var removed = _items[index];
            _items.RemoveAt(index);
            try
            {
                Persist();
            }
            catch
            {
                _items.Insert(index, removed);
                throw;
            }
            return Task.CompletedTask;
        }
    }

    /// <summary>寫暫存檔再取代正式檔（呼叫端需持有鎖）</summary>
    private void Persist()
    {
        var dir = Path.GetDirectoryName(_filePath)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{FileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(_items.OrderBy(x => x.Id).ToList(), JsonDefaults.Options);
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                fs.Write(bytes);
                fs.Flush(true);
            }
            File.Move(tmp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "寫入資料檔失敗：{File}", _filePath);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* 忽略清理失敗 */ }
            throw;
        }
    }

    private static T Clone(T item) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(item, JsonDefaults.Options), JsonDefaults.Options)!;
}
