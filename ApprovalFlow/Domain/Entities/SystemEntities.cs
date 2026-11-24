using ApprovalFlow.Domain.Common;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Domain.Entities;

/// <summary>附件中繼資料；實體檔另存於上傳資料夾</summary>
public class Attachment : EntityBase
{
    public int DocumentId { get; set; }
    public string FileName { get; set; } = "";
    /// <summary>儲存於磁碟的檔名（隨機產生，避免路徑攻擊）</summary>
    public string StoredFileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long SizeBytes { get; set; }
    public string? Sha256 { get; set; }
    public int UploadedById { get; set; }
    public DateTimeOffset UploadedAt { get; set; }
}

/// <summary>站內通知</summary>
public class Notification : EntityBase
{
    public int RecipientId { get; set; }
    public NotificationType Type { get; set; }
    public string Title { get; set; } = "";
    public string Message { get; set; } = "";
    public int? DocumentId { get; set; }
    public string? Link { get; set; }
    public bool IsRead { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}

/// <summary>Email 寄送紀錄（模擬：寫入 outbox 的 .eml 檔）</summary>
public class EmailMessage : EntityBase
{
    public int? RecipientId { get; set; }
    public string To { get; set; } = "";
    public string Subject { get; set; } = "";
    public string Body { get; set; } = "";
    public string? FileName { get; set; }
    public EmailStatus Status { get; set; }
    public string? Error { get; set; }
    public string Category { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>稽核紀錄</summary>
public class AuditLog : EntityBase
{
    public DateTimeOffset At { get; set; }
    public int? UserId { get; set; }
    public string UserName { get; set; } = "";
    public string? IpAddress { get; set; }
    /// <summary>動作代碼，例：Login、LoginFailed、Update、Approve</summary>
    public string Action { get; set; } = "";
    public string EntityType { get; set; } = "";
    public string? EntityId { get; set; }
    public string Description { get; set; } = "";
    public List<FieldChange> Changes { get; set; } = [];
}

public class FieldChange
{
    public string Field { get; set; } = "";
    public string? OldValue { get; set; }
    public string? NewValue { get; set; }
}
