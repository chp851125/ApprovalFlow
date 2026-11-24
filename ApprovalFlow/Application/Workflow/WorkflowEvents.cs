using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Workflow;

/// <summary>流程事件（送達、核准、駁回…），由引擎在單據儲存成功後發布</summary>
public sealed record WorkflowEvent(
    NotificationType Type,
    int RecipientId,
    Document Document,
    int? ActorId,
    string? StepName = null,
    string? Comment = null,
    int? PrincipalId = null); // 代理通知：收件者是代理人，PrincipalId 為被代理人

/// <summary>流程事件接收者（站內通知、Email…）。接收者失敗不影響簽核結果。</summary>
public interface IWorkflowEventSink
{
    Task PublishAsync(IReadOnlyList<WorkflowEvent> events, CancellationToken ct = default);
}

/// <summary>目前使用者對單據可執行的動作</summary>
public sealed record WorkflowPermissions
{
    public bool CanView { get; init; }
    public bool CanSubmit { get; init; }
    public bool CanApprove { get; init; }
    public bool CanReject { get; init; }
    public bool CanReturnToPrevious { get; init; }
    public bool CanReturnToApplicant { get; init; }
    public bool CanWithdraw { get; init; }
    public bool CanAddSigner { get; init; }
    public bool CanTransfer { get; init; }
    public bool CanComment { get; init; }
    /// <summary>以代理人身分簽核時的被代理人</summary>
    public int? OnBehalfOfId { get; init; }
    /// <summary>不可撤回時的原因（顯示用）</summary>
    public string? WithdrawBlockedReason { get; init; }

    public bool CanActOnStep => CanApprove;
    public bool HasAnyAction => CanSubmit || CanApprove || CanWithdraw;

    public static readonly WorkflowPermissions None = new();
}
