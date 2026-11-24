using System.Linq.Expressions;
using ApprovalFlow.Domain.Common;

namespace ApprovalFlow.Application.Abstractions;

/// <summary>
/// 通用資料存取介面。商業邏輯只依賴此介面，日後換成資料庫實作時 Service 不需修改。
/// 回傳的物件為獨立副本，修改後須呼叫 <see cref="UpdateAsync"/> 才會保存。
/// </summary>
public interface IRepository<T> where T : class, IEntity
{
    Task<IReadOnlyList<T>> GetAllAsync(CancellationToken ct = default);

    Task<T?> GetByIdAsync(int id, CancellationToken ct = default);

    /// <summary>以運算式查詢（使用 Expression 以便日後轉譯為 SQL）</summary>
    Task<IReadOnlyList<T>> QueryAsync(Expression<Func<T, bool>> predicate, CancellationToken ct = default);

    /// <summary>新增；Id 為 0 時由儲存層指派，Version 設為 1。回傳已保存的副本。</summary>
    Task<T> AddAsync(T entity, CancellationToken ct = default);

    /// <summary>
    /// 更新；entity.Version 必須等於目前儲存的版本，否則拋出 <see cref="Exceptions.ConcurrencyException"/>。
    /// 成功後 entity.Version 會遞增。
    /// </summary>
    Task UpdateAsync(T entity, CancellationToken ct = default);

    /// <summary>刪除；不存在時拋出 <see cref="Exceptions.NotFoundException"/>。</summary>
    Task DeleteAsync(int id, CancellationToken ct = default);
}
