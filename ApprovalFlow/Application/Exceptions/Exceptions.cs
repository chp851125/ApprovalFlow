namespace ApprovalFlow.Application.Exceptions;

/// <summary>商業規則錯誤；訊息可直接顯示給使用者</summary>
public class BusinessException(string message) : Exception(message);

/// <summary>樂觀並行衝突：資料已被他人修改</summary>
public class ConcurrencyException(string entityName, int id)
    : BusinessException("此筆資料已被其他人修改，請重新整理頁面取得最新內容後再操作。")
{
    public string EntityName { get; } = entityName;
    public int EntityId { get; } = id;
}

public class NotFoundException(string entityName, int id)
    : BusinessException($"找不到資料（{entityName} #{id}），可能已被刪除。");

/// <summary>沒有權限執行此動作（伺服器端授權檢查）</summary>
public class ForbiddenException(string message = "您沒有權限執行此操作。") : BusinessException(message);

/// <summary>輸入驗證錯誤；Field 為對應的欄位名稱（null 代表整體錯誤）</summary>
public class ValidationException(string message, string? field = null) : BusinessException(message)
{
    public string? Field { get; } = field;
}

/// <summary>多筆輸入驗證錯誤（表單送出時一次回報所有問題）</summary>
public class MultiValidationException(IReadOnlyList<(string? Field, string Message)> errors)
    : ValidationException(errors.Count == 1 ? errors[0].Message : $"表單有 {errors.Count} 個問題需要修正。", errors.Count == 1 ? errors[0].Field : null)
{
    public IReadOnlyList<(string? Field, string Message)> Errors { get; } = errors;
}
