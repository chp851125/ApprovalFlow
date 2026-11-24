using System.ComponentModel.DataAnnotations;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Inputs;

// 單據表單輸入。草稿可只填部分欄位，因此必填檢查統一在 DocumentFormService 進行（送出時才嚴格檢查）。

public class DocumentFormInput
{
    public int Id { get; set; }
    public int Version { get; set; }
    public DocumentType DocumentType { get; set; }

    [StringLength(100, ErrorMessage = "主旨最多 100 個字")]
    [Display(Name = "主旨")]
    public string? Title { get; set; }

    [Display(Name = "急件")]
    public bool IsUrgent { get; set; }

    [StringLength(1000, ErrorMessage = "備註最多 1000 個字")]
    [Display(Name = "備註")]
    public string? Remark { get; set; }

    [Display(Name = "抄送")]
    public List<int> CcEmployeeIds { get; set; } = [];

    public LeaveFormInput Leave { get; set; } = new();
    public ExpenseFormInput Expense { get; set; } = new();
    public PurchaseFormInput Purchase { get; set; } = new();
    public OvertimeFormInput Overtime { get; set; } = new();
}

public class LeaveFormInput
{
    public int LeaveTypeId { get; set; }
    /// <summary>開始時間（台北時間）</summary>
    public DateTime? StartAt { get; set; }
    public DateTime? EndAt { get; set; }
    [StringLength(200, ErrorMessage = "請假事由最多 200 個字")]
    public string? Reason { get; set; }
    public int? DeputyId { get; set; }
}

public class ExpenseFormInput
{
    public List<ExpenseItemInput> Items { get; set; } = [];
    public PaymentMethod PaymentMethod { get; set; } = PaymentMethod.BankTransfer;
    [StringLength(20)]
    public string? ProjectCode { get; set; }
    [StringLength(100, ErrorMessage = "報銷事由最多 100 個字")]
    public string? Purpose { get; set; }
}

public class ExpenseItemInput
{
    public DateOnly? Date { get; set; }
    public int CategoryId { get; set; }
    [StringLength(100)]
    public string? Description { get; set; }
    public decimal? Amount { get; set; }
    [StringLength(30)]
    public string? ReceiptNo { get; set; }
}

public class PurchaseFormInput
{
    public int? SupplierId { get; set; }
    public int BudgetSubjectId { get; set; }
    public List<PurchaseItemInput> Items { get; set; } = [];
    public DateOnly? RequiredDate { get; set; }
    [StringLength(200, ErrorMessage = "採購目的最多 200 個字")]
    public string? Purpose { get; set; }
    [StringLength(100)]
    public string? DeliveryLocation { get; set; }
}

public class PurchaseItemInput
{
    [StringLength(60)]
    public string? Name { get; set; }
    [StringLength(100)]
    public string? Specification { get; set; }
    public decimal? Quantity { get; set; }
    [StringLength(10)]
    public string? Unit { get; set; }
    public decimal? UnitPrice { get; set; }
}

public class OvertimeFormInput
{
    public DateOnly? Date { get; set; }
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public OvertimeCompensation Compensation { get; set; } = OvertimeCompensation.Pay;
    [StringLength(200, ErrorMessage = "加班事由最多 200 個字")]
    public string? Reason { get; set; }
}

/// <summary>上傳檔案（由 Web 層轉換，Application 不依賴 IFormFile）</summary>
public sealed record UploadFile(string FileName, long Length, Func<Stream> OpenReadStream);
