using ApprovalFlow.Application.Reports;
using ClosedXML.Excel;

namespace ApprovalFlow.Infrastructure.Reports;

/// <summary>
/// 以 ClosedXML 產生 .xlsx：標題列、凍結表頭、自動篩選、數字 / 日期格式、合計列。
/// 文字一律以字串型別寫入（不會被 Excel 當成公式執行，避免公式注入）。
/// </summary>
public sealed class ClosedXmlExporter : IExcelExporter
{
    public byte[] Export(IReadOnlyList<ReportTable> sheets)
    {
        using var wb = new XLWorkbook();
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var table in sheets)
        {
            var ws = wb.Worksheets.Add(UniqueSheetName(table.SheetName, usedNames));
            var cols = table.Columns.Count;
            var row = 1;

            ws.Cell(row, 1).Value = table.Title;
            ws.Cell(row, 1).Style.Font.SetBold().Font.SetFontSize(14);
            row++;
            if (!string.IsNullOrEmpty(table.Subtitle))
            {
                ws.Cell(row, 1).Value = table.Subtitle;
                ws.Cell(row, 1).Style.Font.SetFontColor(XLColor.Gray);
                row++;
            }
            row++;

            var headerRow = row;
            for (var c = 0; c < cols; c++)
            {
                var cell = ws.Cell(headerRow, c + 1);
                cell.Value = table.Columns[c].Header;
                cell.Style.Font.SetBold().Font.SetFontColor(XLColor.White)
                    .Fill.SetBackgroundColor(XLColor.FromHtml("#1F3A5F"))
                    .Alignment.SetHorizontal(XLAlignmentHorizontalValues.Center);
            }

            foreach (var r in table.Rows)
            {
                row++;
                WriteRow(ws, row, table.Columns, r);
            }
            var lastDataRow = row;

            if (table.Totals is { } totals)
            {
                row++;
                WriteRow(ws, row, table.Columns, totals);
                ws.Range(row, 1, row, cols).Style.Font.SetBold().Fill.SetBackgroundColor(XLColor.FromHtml("#EEF2F7"))
                    .Border.SetTopBorder(XLBorderStyleValues.Thin);
            }

            if (table.Rows.Count > 0)
            {
                ws.Range(headerRow, 1, lastDataRow, cols).SetAutoFilter();
            }
            ws.SheetView.FreezeRows(headerRow);

            for (var c = 0; c < cols; c++)
            {
                var col = ws.Column(c + 1);
                if (table.Columns[c].Width > 0) col.Width = table.Columns[c].Width;
                else
                {
                    col.AdjustToContents(headerRow, row);
                    col.Width = Math.Clamp(col.Width * 1.15, 8, 60); // 中文字寬度補償
                }
            }
            ws.PageSetup.PageOrientation = XLPageOrientation.Landscape;
            ws.PageSetup.FitToPages(1, 0);
        }

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static void WriteRow(IXLWorksheet ws, int row, IReadOnlyList<ReportColumn> columns, object?[] values)
    {
        for (var c = 0; c < columns.Count && c < values.Length; c++)
        {
            var cell = ws.Cell(row, c + 1);
            var v = values[c];
            switch (v)
            {
                case null: break;
                case string s: cell.Value = s; break; // 字串型別，不會被當成公式
                case DateTimeOffset dto:
                    cell.Value = dto.DateTime;
                    cell.Style.DateFormat.Format = columns[c].Kind == ColumnKind.Date ? "yyyy/mm/dd" : "yyyy/mm/dd hh:mm";
                    break;
                case DateOnly d:
                    cell.Value = d.ToDateTime(TimeOnly.MinValue);
                    cell.Style.DateFormat.Format = "yyyy/mm/dd";
                    break;
                case int i: cell.Value = i; break;
                case decimal m:
                    cell.Value = m;
                    cell.Style.NumberFormat.Format = columns[c].Kind switch
                    {
                        ColumnKind.Money => "#,##0",
                        ColumnKind.Percent => "0.0\"%\"",
                        ColumnKind.Integer => "#,##0",
                        _ => "#,##0.0"
                    };
                    break;
                case double dbl: cell.Value = dbl; cell.Style.NumberFormat.Format = "#,##0.0"; break;
                default: cell.Value = v.ToString(); break;
            }
        }
    }

    /// <summary>工作表名稱最多 31 字、不可含 []:*?/\ 且不可重複</summary>
    private static string UniqueSheetName(string name, HashSet<string> used)
    {
        var clean = new string(name.Where(ch => !"[]:*?/\\".Contains(ch)).ToArray());
        if (clean.Length == 0) clean = "Sheet";
        if (clean.Length > 28) clean = clean[..28];
        var result = clean;
        for (var i = 2; !used.Add(result); i++) result = $"{clean}({i})";
        return result;
    }
}
