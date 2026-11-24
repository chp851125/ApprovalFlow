using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Domain.Entities;

namespace ApprovalFlow.Application.Reports;

public sealed class AuditFilter
{
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? User { get; set; }
    public string? Action { get; set; }
    public string? EntityType { get; set; }
    public string? Q { get; set; }
    public int Page { get; set; } = 1;
}

/// <summary>稽核紀錄查詢與匯出</summary>
public sealed class AuditQueryService(IRepository<AuditLog> logs)
{
    public async Task<IReadOnlyList<AuditLog>> QueryAsync(AuditFilter f)
    {
        IEnumerable<AuditLog> q = await logs.GetAllAsync();
        if (f.From is { } from) q = q.Where(l => DateOnly.FromDateTime(l.At.DateTime) >= from);
        if (f.To is { } to) q = q.Where(l => DateOnly.FromDateTime(l.At.DateTime) <= to);
        if (!string.IsNullOrWhiteSpace(f.User)) q = q.Where(l => l.UserName.Contains(f.User.Trim(), StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(f.Action)) q = q.Where(l => l.Action == f.Action);
        if (!string.IsNullOrWhiteSpace(f.EntityType)) q = q.Where(l => l.EntityType == f.EntityType);
        if (!string.IsNullOrWhiteSpace(f.Q))
        {
            var k = f.Q.Trim();
            q = q.Where(l => l.Description.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                             (l.EntityId ?? "").Equals(k, StringComparison.OrdinalIgnoreCase) ||
                             (l.IpAddress ?? "").Contains(k) ||
                             l.Changes.Any(c => c.Field.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                                (c.NewValue ?? "").Contains(k, StringComparison.OrdinalIgnoreCase) ||
                                                (c.OldValue ?? "").Contains(k, StringComparison.OrdinalIgnoreCase)));
        }
        return q.OrderByDescending(l => l.At).ThenByDescending(l => l.Id).ToList();
    }

    public async Task<(IReadOnlyList<string> Actions, IReadOnlyList<string> EntityTypes)> OptionsAsync()
    {
        var all = await logs.GetAllAsync();
        return (all.Select(l => l.Action).Distinct().Order().ToList(), all.Select(l => l.EntityType).Distinct().Order().ToList());
    }

    public static ReportTable ToTable(IReadOnlyList<AuditLog> rows, AuditFilter f) => new()
    {
        Title = "稽核紀錄",
        SheetName = "稽核紀錄",
        Subtitle = $"{f.From?.ToString("yyyy/MM/dd") ?? "最早"}～{f.To?.ToString("yyyy/MM/dd") ?? "最新"}・共 {rows.Count} 筆" + (rows.Count >= ReportService.MaxRows ? $"（僅匯出前 {ReportService.MaxRows} 筆）" : ""),
        Columns =
        [
            new("時間", ColumnKind.DateTime), new("使用者"), new("IP"), new("動作"), new("對象"), new("對象 Id"),
            new("說明", Width: 50), new("異動欄位", Width: 60)
        ],
        Rows = rows.Take(ReportService.MaxRows).Select(l => new object?[]
        {
            l.At, l.UserName, l.IpAddress, l.Action, l.EntityType, l.EntityId, l.Description,
            l.Changes.Count == 0 ? null : string.Join("\n", l.Changes.Select(c => $"{c.Field}：{c.OldValue ?? "（空）"} → {c.NewValue ?? "（空）"}"))
        }).ToList()
    };
}
