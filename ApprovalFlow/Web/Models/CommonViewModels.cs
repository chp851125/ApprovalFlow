using System.ComponentModel.DataAnnotations;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Web.Models;

public class LoginViewModel
{
    [Required(ErrorMessage = "請輸入帳號")]
    [Display(Name = "帳號")]
    [StringLength(50)]
    public string Account { get; set; } = "";

    [Required(ErrorMessage = "請輸入密碼")]
    [DataType(DataType.Password)]
    [Display(Name = "密碼")]
    [StringLength(100)]
    public string Password { get; set; } = "";

    [Display(Name = "保持登入")]
    public bool RememberMe { get; set; }

    public string? ReturnUrl { get; set; }
}

public class ErrorViewModel
{
    public string? RequestId { get; set; }
    public int StatusCode { get; set; } = 500;
    public string Title { get; set; } = "系統發生錯誤";
    public string Message { get; set; } = "很抱歉，處理您的要求時發生問題。請稍後再試，若持續發生請聯絡系統管理員。";
    /// <summary>僅 Development 顯示</summary>
    public string? Detail { get; set; }
}

public class HomeViewModel
{
    public string UserName { get; set; } = "";
    public string Greeting { get; set; } = "";
    public string TodayText { get; set; } = "";
    public int PendingCount { get; set; }
    public int InProgressCount { get; set; }
    public int DraftCount { get; set; }
    public int ApprovedThisMonth { get; set; }
    public int UnreadNotifications { get; set; }
    public IReadOnlyList<HomeDocumentRow> RecentDocuments { get; set; } = [];
    /// <summary>待我簽核（CurrentStep 欄位放申請人姓名）</summary>
    public IReadOnlyList<HomeDocumentRow> PendingDocuments { get; set; } = [];
    public IReadOnlyList<Holiday> UpcomingHolidays { get; set; } = [];
    public IReadOnlyList<Delegation> ActiveDelegations { get; set; } = [];
    public IReadOnlyDictionary<int, string> EmployeeNames { get; set; } = new Dictionary<int, string>();
}

public record HomeDocumentRow(int Id, string DocumentNo, DocumentType Type, string Title, DocumentStatus Status, DateTimeOffset UpdatedAt, string? CurrentStep);

public class QuickSwitchItem
{
    public int Id { get; set; }
    public string Label { get; set; } = "";
    public string Group { get; set; } = "";
}
