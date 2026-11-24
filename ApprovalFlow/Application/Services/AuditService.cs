using System.Collections;
using System.Globalization;
using System.Reflection;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Domain.Entities;

namespace ApprovalFlow.Application.Services;

/// <summary>稽核紀錄寫入</summary>
public sealed class AuditService(IRepository<AuditLog> repo, ICurrentUser currentUser, IClock clock)
{
    public async Task LogAsync(string action, string entityType, string? entityId, string description,
        IEnumerable<FieldChange>? changes = null, int? userId = null, string? userName = null)
    {
        var log = new AuditLog
        {
            At = clock.Now,
            UserId = userId ?? currentUser.Id,
            UserName = userName ?? (currentUser.IsAuthenticated ? currentUser.Name : "（匿名）"),
            IpAddress = currentUser.IpAddress,
            Action = action,
            EntityType = entityType,
            EntityId = entityId,
            Description = description,
            Changes = changes?.ToList() ?? []
        };
        await repo.AddAsync(log);
    }

    private static readonly string[] AlwaysIgnore = ["Version", "PasswordHash", "SecurityStamp"];

    /// <summary>比對兩個物件的簡單屬性（字串、數字、日期、列舉、布林、簡單集合），產生欄位前後值。</summary>
    public static List<FieldChange> Diff<T>(T? before, T? after, params string[] ignore) where T : class
    {
        var changes = new List<FieldChange>();
        var props = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && !AlwaysIgnore.Contains(p.Name) && !ignore.Contains(p.Name));
        foreach (var p in props)
        {
            if (!IsSimple(p.PropertyType)) continue;
            var o = Format(before is null ? null : p.GetValue(before));
            var n = Format(after is null ? null : p.GetValue(after));
            if (o != n) changes.Add(new FieldChange { Field = p.Name, OldValue = o, NewValue = n });
        }
        return changes;
    }

    private static bool IsSimple(Type t)
    {
        var u = Nullable.GetUnderlyingType(t) ?? t;
        if (u.IsPrimitive || u.IsEnum || u == typeof(string) || u == typeof(decimal) ||
            u == typeof(DateTime) || u == typeof(DateTimeOffset) || u == typeof(DateOnly) || u == typeof(TimeOnly))
            return true;
        if (u.IsGenericType && typeof(IEnumerable).IsAssignableFrom(u))
            return IsSimple(u.GetGenericArguments()[0]);
        return false;
    }

    private static string? Format(object? v) => v switch
    {
        null => null,
        string s => s,
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IEnumerable e => string.Join(",", e.Cast<object?>().Select(x => x?.ToString())),
        _ => v.ToString()
    };
}
