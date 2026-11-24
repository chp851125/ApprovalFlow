namespace ApprovalFlow.Application.Reports;

public enum ColumnKind { Text, Integer, Number, Money, Percent, Date, DateTime }

public sealed record ReportColumn(string Header, ColumnKind Kind = ColumnKind.Text, int Width = 0);

/// <summary>表格型報表（網頁預覽與 Excel 匯出共用同一份資料）</summary>
public sealed class ReportTable
{
    public string Title { get; init; } = "";
    public string SheetName { get; init; } = "報表";
    public string? Subtitle { get; init; }
    public IReadOnlyList<ReportColumn> Columns { get; init; } = [];
    public List<object?[]> Rows { get; init; } = [];
    public object?[]? Totals { get; set; }
}

/// <summary>Excel 匯出（實作於 Infrastructure，使用 ClosedXML）</summary>
public interface IExcelExporter
{
    byte[] Export(IReadOnlyList<ReportTable> sheets);
}
