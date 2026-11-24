using ApprovalFlow.Domain.Common;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Domain.Entities;

/// <summary>
/// 流程範本。同一單別可有多個版本（每個版本一筆記錄），
/// 單據送出時記錄所用的 TemplateId，之後範本改版不影響進行中單據。
/// </summary>
public class WorkflowTemplate : EntityBase
{
    public DocumentType DocumentType { get; set; }
    public string Name { get; set; } = "";
    /// <summary>範本版本號（不同於樂觀並行用的 Version）</summary>
    public int TemplateVersion { get; set; } = 1;
    /// <summary>是否為該單別目前使用中的版本</summary>
    public bool IsActive { get; set; }
    public DateOnly EffectiveFrom { get; set; }
    public string? Description { get; set; }
    public List<WorkflowStepDefinition> Steps { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public int? CreatedById { get; set; }
}

/// <summary>範本中的關卡定義</summary>
public class WorkflowStepDefinition
{
    public int StepNo { get; set; }
    public string Name { get; set; } = "";
    public ApproverSource ApproverSource { get; set; }
    /// <summary>ApproverSource = Role 時使用</summary>
    public string? RoleName { get; set; }
    /// <summary>ApproverSource = SpecificUser 時使用</summary>
    public List<int> UserIds { get; set; } = [];
    /// <summary>ApproverSource = ManagerLevelN 時使用</summary>
    public int Levels { get; set; } = 1;
    public ApprovalMode Mode { get; set; } = ApprovalMode.Single;
    /// <summary>條件；null 代表一律經過此關卡</summary>
    public StepCondition? Condition { get; set; }
    /// <summary>關卡時效（小時）；null 則使用系統預設</summary>
    public int? SlaHours { get; set; }
    /// <summary>申請人可否在此關卡撤回</summary>
    public bool AllowWithdraw { get; set; } = true;
}

/// <summary>關卡條件（依金額 / 天數 / 時數）</summary>
public class StepCondition
{
    public ConditionField Field { get; set; }
    public ConditionOperator Operator { get; set; }
    public decimal Value { get; set; }

    public bool IsSatisfied(decimal? amount, decimal? hours)
    {
        decimal actual = Field switch
        {
            ConditionField.Amount => amount ?? 0,
            ConditionField.Hours => hours ?? 0,
            ConditionField.Days => (hours ?? 0) / 8m,
            _ => 0
        };
        return Operator switch
        {
            ConditionOperator.GreaterThan => actual > Value,
            ConditionOperator.GreaterThanOrEqual => actual >= Value,
            ConditionOperator.LessThan => actual < Value,
            ConditionOperator.LessThanOrEqual => actual <= Value,
            ConditionOperator.Equal => actual == Value,
            _ => false
        };
    }

    public string Describe() => $"{Field.ToDisplay()} {Operator.ToSymbol()} {Value:0.##}";
}
