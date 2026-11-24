using System.ComponentModel.DataAnnotations;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Inputs;

/// <summary>流程範本編輯。Id = 0 代表建立新版本（SourceId 為複製來源）</summary>
public class WorkflowTemplateInput
{
    public int Id { get; set; }
    public int Version { get; set; }
    public int? SourceId { get; set; }

    [Display(Name = "單別")]
    public DocumentType DocumentType { get; set; }

    [Required(ErrorMessage = "請輸入流程名稱")]
    [StringLength(50, ErrorMessage = "流程名稱最多 50 個字")]
    [Display(Name = "流程名稱")]
    public string Name { get; set; } = "";

    [StringLength(300, ErrorMessage = "說明最多 300 個字")]
    [Display(Name = "版本說明")]
    public string? Description { get; set; }

    public List<WorkflowStepInput> Steps { get; set; } = [];
}

public class WorkflowStepInput
{
    [Required(ErrorMessage = "請輸入關卡名稱")]
    [StringLength(30, ErrorMessage = "關卡名稱最多 30 個字")]
    public string Name { get; set; } = "";

    public ApproverSource ApproverSource { get; set; } = ApproverSource.DirectManager;
    public string? RoleName { get; set; }
    public List<int> UserIds { get; set; } = [];

    [Range(1, 5, ErrorMessage = "往上層級需介於 1~5")]
    public int Levels { get; set; } = 1;

    public ApprovalMode Mode { get; set; } = ApprovalMode.Single;

    public bool HasCondition { get; set; }
    public ConditionField ConditionField { get; set; } = ConditionField.Amount;
    public ConditionOperator ConditionOperator { get; set; } = ConditionOperator.GreaterThanOrEqual;

    [Range(0, 100_000_000, ErrorMessage = "條件值需介於 0~100,000,000")]
    public decimal ConditionValue { get; set; }

    [Range(1, 240, ErrorMessage = "時效需介於 1~240 個工作小時")]
    public int? SlaHours { get; set; }

    public bool AllowWithdraw { get; set; } = true;
}

/// <summary>流程試算條件</summary>
public class WorkflowPreviewInput
{
    public int? ApplicantId { get; set; }
    public decimal? Amount { get; set; }
    public decimal? Hours { get; set; }
}
