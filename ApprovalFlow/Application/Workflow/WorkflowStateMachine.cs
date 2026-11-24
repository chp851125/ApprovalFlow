using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Workflow;

/// <summary>
/// 單據狀態與可執行動作的對照表。任何不在表內的組合都視為不合法狀態轉換並拒絕。
/// <code>
/// 草稿 ──送出──▶ 簽核中 ──(全部關卡核准)──▶ 已核准
///                  │ ├─駁回──────────────▶ 已駁回
///                  │ ├─退回申請人────────▶ 已退回 ──重新送出──▶ 簽核中
///                  │ └─撤回──────────────▶ 已撤回 ──重新送出──▶ 簽核中
///                  └─(超過時效)──▶ 逾時（仍可執行簽核中的所有動作）
/// </code>
/// </summary>
public static class WorkflowStateMachine
{
    private static readonly ApprovalActionType[] InFlow =
    [
        ApprovalActionType.Approve, ApprovalActionType.Reject, ApprovalActionType.ReturnToPrevious,
        ApprovalActionType.ReturnToApplicant, ApprovalActionType.Withdraw, ApprovalActionType.AddSigner,
        ApprovalActionType.Transfer, ApprovalActionType.Escalate, ApprovalActionType.Remind, ApprovalActionType.Comment
    ];

    private static readonly Dictionary<DocumentStatus, ApprovalActionType[]> Allowed = new()
    {
        [DocumentStatus.Draft] = [ApprovalActionType.Submit],
        [DocumentStatus.InProgress] = InFlow,
        [DocumentStatus.Overdue] = InFlow,
        [DocumentStatus.Returned] = [ApprovalActionType.Resubmit, ApprovalActionType.Comment],
        [DocumentStatus.Withdrawn] = [ApprovalActionType.Resubmit, ApprovalActionType.Comment],
        [DocumentStatus.Approved] = [ApprovalActionType.Comment],
        [DocumentStatus.Rejected] = [ApprovalActionType.Comment]
    };

    public static bool CanPerform(DocumentStatus status, ApprovalActionType action) =>
        Allowed.TryGetValue(status, out var actions) && actions.Contains(action);

    public static void Ensure(DocumentStatus status, ApprovalActionType action)
    {
        if (!CanPerform(status, action))
            throw new BusinessException($"單據目前為「{status.ToDisplay()}」，不可執行「{action.ToDisplay()}」。請重新整理頁面確認最新狀態。");
    }

    /// <summary>送出動作：草稿為首次送出，其餘為重新送出</summary>
    public static ApprovalActionType SubmitActionFor(DocumentStatus status) =>
        status == DocumentStatus.Draft ? ApprovalActionType.Submit : ApprovalActionType.Resubmit;
}
