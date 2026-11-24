using ApprovalFlow.Application.Documents;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;
using ApprovalFlow.Web.Infrastructure;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ApprovalFlow.Web.Models;

/// <summary>清單頁查詢條件（對應 QueryString）</summary>
public class DocumentListFilter
{
    public string Box { get; set; } = "todo";
    public DocumentType? Type { get; set; }
    public DocumentStatus? Status { get; set; }
    public string? Q { get; set; }
    public DateOnly? From { get; set; }
    public DateOnly? To { get; set; }
    public string? Sort { get; set; }
    public int Page { get; set; } = 1;

    public DocumentBox ParsedBox => Box?.ToLowerInvariant() switch
    {
        "done" => DocumentBox.Done,
        "mine" => DocumentBox.Mine,
        "cc" => DocumentBox.Cc,
        "drafts" => DocumentBox.Drafts,
        _ => DocumentBox.Todo
    };

    public bool HasFilter => Type is not null || Status is not null || !string.IsNullOrWhiteSpace(Q) || From is not null || To is not null;

    public IDictionary<string, string?> ToRouteValues() => new Dictionary<string, string?>
    {
        ["box"] = Box, ["type"] = Type?.ToString(), ["status"] = Status?.ToString(), ["q"] = Q,
        ["from"] = From?.ToString("yyyy-MM-dd"), ["to"] = To?.ToString("yyyy-MM-dd"), ["sort"] = Sort
    };
}

public class DocumentListViewModel
{
    public DocumentListFilter Filter { get; set; } = new();
    public PagedList<DocumentListItem> Rows { get; set; } = new([], 1, 20);
    public BoxCounts Counts { get; set; } = new(0, 0, 0);
    public bool BatchEnabled { get; set; }
    public string Title { get; set; } = "";
    public PagerModel Pager => new(Rows.Page, Rows.TotalPages, Rows.TotalCount, Rows.FirstItem, Rows.LastItem, "Index", Filter.ToRouteValues());
}

/// <summary>顯示表單內容所需的名稱對照</summary>
public class ContentLookups
{
    public IReadOnlyDictionary<int, string> Employees { get; set; } = new Dictionary<int, string>();
    public IReadOnlyDictionary<int, LeaveType> LeaveTypes { get; set; } = new Dictionary<int, LeaveType>();
    public IReadOnlyDictionary<int, ExpenseCategory> Categories { get; set; } = new Dictionary<int, ExpenseCategory>();
    public IReadOnlyDictionary<int, BudgetSubject> BudgetSubjects { get; set; } = new Dictionary<int, BudgetSubject>();
    public IReadOnlyDictionary<int, Supplier> Suppliers { get; set; } = new Dictionary<int, Supplier>();
}

public sealed record DocumentContentModel(Document Document, ContentLookups Lookups);

public class DocumentTypeOption
{
    public DocumentTypeConfig Config { get; set; } = new();
    public bool HasActiveTemplate { get; set; }
}

public class DocumentEditViewModel
{
    public DocumentFormInput Input { get; set; } = new();
    public Document? Existing { get; set; }
    public DocumentTypeConfig? Config { get; set; }
    public IReadOnlyList<Attachment> Attachments { get; set; } = [];
    public IReadOnlyList<LeaveBalanceRow> LeaveBalances { get; set; } = [];
    public IEnumerable<SelectListItem> LeaveTypes { get; set; } = [];
    public IEnumerable<SelectListItem> Categories { get; set; } = [];
    public IEnumerable<SelectListItem> BudgetSubjects { get; set; } = [];
    public IEnumerable<SelectListItem> Suppliers { get; set; } = [];
    public IEnumerable<SelectListItem> Colleagues { get; set; } = [];
    public string AllowedExtensions { get; set; } = "";
    public int MaxFileMb { get; set; }
    public int MaxFiles { get; set; }
    /// <summary>費用類別資訊（給前端顯示上限 / 是否需收據）</summary>
    public IReadOnlyList<ExpenseCategory> CategoryInfo { get; set; } = [];
    public bool IsNew => Input.Id == 0;
    public bool IsResubmit => Existing is not null && Existing.Status != DocumentStatus.Draft;
}
