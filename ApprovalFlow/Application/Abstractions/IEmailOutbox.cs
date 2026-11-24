namespace ApprovalFlow.Application.Abstractions;

/// <summary>要寄出的郵件</summary>
public sealed record EmailEnvelope(string From, string FromName, string To, string ToName, string Subject, string TextBody, string HtmlBody, DateTimeOffset Date);

/// <summary>
/// 郵件寄送出口。目前實作為寫入 outbox 資料夾的 .eml 檔（可用 Outlook / Thunderbird 直接開啟），
/// 日後改成 SMTP 只需替換實作。
/// </summary>
public interface IEmailOutbox
{
    /// <summary>寫出郵件，回傳相對於 outbox 的檔名</summary>
    Task<string> WriteAsync(EmailEnvelope email, CancellationToken ct = default);
    /// <summary>開啟已寫出的 .eml；不存在時回傳 null</summary>
    Stream? OpenRead(string fileName);
}
