using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Application.Workflow;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ApprovalFlow.Web.Models;

// ---------------- 單據檢視 ----------------

public class DocumentDetailsViewModel
{
    public Document Document { get; set; } = new();
    public WorkflowPermissions Permissions { get; set; } = WorkflowPermissions.None;
    public IReadOnlyDictionary<int, string> Names { get; set; } = new Dictionary<int, string>();
    public string ApplicantName { get; set; } = "";
    public string DepartmentName { get; set; } = "";
    public string? TemplateName { get; set; }
    public int CurrentUserId { get; set; }
    public DateTimeOffset Now { get; set; }
    /// <summary>加簽 / 轉簽可選的人員</summary>
    public IEnumerable<SelectListItem> TargetCandidates { get; set; } = [];
    public IReadOnlyList<Attachment> Attachments { get; set; } = [];
    public ContentLookups Lookups { get; set; } = new();
    public bool CanEdit { get; set; }
    public bool CanDeleteDraft { get; set; }

    public string Name(int? id) => id is int i ? Names.GetValueOrDefault(i) ?? $"#{i}" : "系統";
}

/// <summary>單據動作的 Modal 設定</summary>
public sealed record ActionModalModel(
    string Action, string Title, string ButtonText, string ButtonClass, string Icon,
    bool CommentRequired, bool NeedsTarget, string? Hint, DocumentDetailsViewModel Page);

// ---------------- 流程範本 ----------------

public sealed record TemplateVersionRow(WorkflowTemplate Template, int Usage, bool Editable);

public class WorkflowTemplateIndexViewModel
{
    public IReadOnlyList<(DocumentType Type, IReadOnlyList<TemplateVersionRow> Versions)> Groups { get; set; } = [];
    public IReadOnlyDictionary<int, string> Names { get; set; } = new Dictionary<int, string>();
}

public class WorkflowTemplateDetailsViewModel
{
    public WorkflowTemplate Template { get; set; } = new();
    public int Usage { get; set; }
    public bool Editable { get; set; }
    public IReadOnlyDictionary<int, string> Names { get; set; } = new Dictionary<int, string>();
    public WorkflowPreviewInput Preview { get; set; } = new();
    public IEnumerable<SelectListItem> Applicants { get; set; } = [];
    public IReadOnlyList<DocumentStep>? PreviewSteps { get; set; }
    public string? PreviewError { get; set; }
    public IReadOnlyList<TemplateVersionRow> OtherVersions { get; set; } = [];
}

public class WorkflowTemplateEditViewModel
{
    public WorkflowTemplateInput Input { get; set; } = new();
    public int? TemplateVersion { get; set; }
    public IEnumerable<SelectListItem> Employees { get; set; } = [];
    public bool IsNewVersion => Input.Id == 0;
}

// ---------------- 通知與排程 ----------------

public class NotificationIndexViewModel
{
    public string Filter { get; set; } = "all";
    public ApprovalFlow.Web.Infrastructure.PagedList<Notification> Rows { get; set; } = new([], 1, 20);
    public int UnreadCount { get; set; }
    public ApprovalFlow.Web.Infrastructure.PagerModel Pager =>
        new(Rows.Page, Rows.TotalPages, Rows.TotalCount, Rows.FirstItem, Rows.LastItem, "Index", new Dictionary<string, string?> { ["filter"] = Filter });
}

public class SchedulerIndexViewModel
{
    public ApprovalFlow.Web.Infrastructure.BackgroundJobStatus Status { get; set; } = null!;
    public int ReminderBeforeHours { get; set; }
    public bool AutoEscalate { get; set; }
    public int EscalateAfterHours { get; set; }
    public int DefaultSlaHours { get; set; }
    public bool DigestEnabled { get; set; }
    public int DigestHour { get; set; }
    public bool EmailEnabled { get; set; }
    public int EmailTotal { get; set; }
    public int EmailFailed { get; set; }
}

public class EmailLogViewModel
{
    public string? Q { get; set; }
    public string? Category { get; set; }
    public EmailStatus? Status { get; set; }
    public IReadOnlyList<string> Categories { get; set; } = [];
    public IReadOnlyDictionary<int, string> Names { get; set; } = new Dictionary<int, string>();
    public ApprovalFlow.Web.Infrastructure.PagedList<EmailMessage> Rows { get; set; } = new([], 1, 20);
    public ApprovalFlow.Web.Infrastructure.PagerModel Pager =>
        new(Rows.Page, Rows.TotalPages, Rows.TotalCount, Rows.FirstItem, Rows.LastItem, "Emails",
            new Dictionary<string, string?> { ["q"] = Q, ["category"] = Category, ["emailStatus"] = Status?.ToString() });
}

// ---------------- 儀表板、報表、稽核、資料維護 ----------------

public class DashboardViewModel
{
    public bool IsAdmin { get; set; }
    public bool ShowCompany { get; set; }
    public ApprovalFlow.Application.Reports.CompanyDashboard? Company { get; set; }
    public ApprovalFlow.Application.Reports.PersonalDashboard? Personal { get; set; }
    public string ChartJson { get; set; } = "{}";
}

public class ReportIndexViewModel
{
    public ApprovalFlow.Application.Reports.ReportFilter Filter { get; set; } = new();
    public IReadOnlyList<ApprovalFlow.Application.Reports.ReportDefinition> Reports { get; set; } = [];
    public ApprovalFlow.Application.Reports.ReportDefinition Current { get; set; } = null!;
    public ApprovalFlow.Application.Reports.ReportTable Table { get; set; } = new();
    public int PreviewRows { get; set; }
    public IReadOnlyList<DocumentType> AllowedTypes { get; set; } = [];
    public IEnumerable<SelectListItem> Departments { get; set; } = [];
    public IReadOnlyList<int> Years { get; set; } = [];
}

public class AuditIndexViewModel
{
    public ApprovalFlow.Application.Reports.AuditFilter Filter { get; set; } = new();
    public ApprovalFlow.Web.Infrastructure.PagedList<AuditLog> Rows { get; set; } = new([], 1, 30);
    public IReadOnlyList<string> Actions { get; set; } = [];
    public IReadOnlyList<string> EntityTypes { get; set; } = [];
    public ApprovalFlow.Web.Infrastructure.PagerModel Pager => new(Rows.Page, Rows.TotalPages, Rows.TotalCount, Rows.FirstItem, Rows.LastItem, "Index",
        new Dictionary<string, string?>
        {
            ["from"] = Filter.From?.ToString("yyyy-MM-dd"), ["to"] = Filter.To?.ToString("yyyy-MM-dd"),
            ["user"] = Filter.User, ["action"] = Filter.Action, ["entityType"] = Filter.EntityType, ["q"] = Filter.Q
        });
}

public sealed record DataFileInfo(string FileName, long SizeBytes, DateTime? LastWrite);

public class MaintenanceViewModel
{
    public IReadOnlyList<DataFileInfo> Files { get; set; } = [];
    public string DataPath { get; set; } = "";
    public string UploadPath { get; set; } = "";
    public string OutboxPath { get; set; } = "";
    public string SeedPath { get; set; } = "";
    public int UploadCount { get; set; }
    public int OutboxCount { get; set; }
    public string ConfirmText { get; set; } = "";
}
