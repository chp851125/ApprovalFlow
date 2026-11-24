using System.Net;
using System.Text;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;
using Microsoft.Extensions.Logging;

namespace ApprovalFlow.Application.Notifications;

/// <summary>Email 內容中的一列（標籤 + 值）</summary>
public sealed record EmailField(string Label, string Value);

/// <summary>Email 內容</summary>
public sealed record EmailContent(string Subject, string Heading, string Intro, IReadOnlyList<EmailField> Fields, string? LinkPath, string? LinkText, string? Footer = null)
{
    /// <summary>每日摘要等清單型內容</summary>
    public IReadOnlyList<string> ListItems { get; init; } = [];
}

/// <summary>
/// 寄送 Email（模擬）：產生純文字 + HTML 內容，交給 IEmailOutbox 寫出 .eml，並記錄寄送紀錄。
/// 寄送失敗只記錄錯誤，不會影響業務流程。
/// </summary>
public sealed class EmailService(
    IRepository<EmailMessage> messages,
    IEmailOutbox outbox,
    SettingsService settings,
    IClock clock,
    ILogger<EmailService> logger)
{
    public async Task<bool> IsEnabledAsync() => await settings.GetBoolAsync(SettingKeys.EmailEnabled, false);

    public async Task<EmailMessage?> SendAsync(Employee recipient, EmailContent content, string category)
    {
        if (!await IsEnabledAsync()) return null;
        if (!recipient.IsActive || string.IsNullOrWhiteSpace(recipient.Email)) return null;

        var company = await settings.GetStringAsync(SettingKeys.CompanyName, "簽核系統");
        var from = await settings.GetStringAsync(SettingKeys.EmailFromAddress, "noreply@localhost");
        var baseUrl = (await settings.GetStringAsync(SettingKeys.SiteBaseUrl, "")).TrimEnd('/');
        var link = content.LinkPath is null ? null : baseUrl + content.LinkPath;
        var subject = $"[{company} 簽核系統] {content.Subject}";
        var now = clock.Now;

        var text = BuildText(recipient.Name, content, link, company);
        var msg = new EmailMessage
        {
            RecipientId = recipient.Id,
            To = recipient.Email,
            Subject = subject,
            Body = text,
            Category = category,
            Status = EmailStatus.Pending,
            CreatedAt = now
        };
        try
        {
            msg.FileName = await outbox.WriteAsync(new EmailEnvelope(
                from, $"{company} 簽核系統", recipient.Email, recipient.Name, subject,
                text, BuildHtml(recipient.Name, content, link, company), now));
            msg.Status = EmailStatus.Written;
            msg.SentAt = clock.Now;
        }
        catch (Exception ex)
        {
            msg.Status = EmailStatus.Failed;
            msg.Error = ex.Message;
            logger.LogError(ex, "Email 寫出失敗：{To} {Subject}", recipient.Email, subject);
        }
        return await messages.AddAsync(msg);
    }

    private static string BuildText(string name, EmailContent c, string? link, string company)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{name} 您好：").AppendLine().AppendLine(c.Intro).AppendLine();
        foreach (var f in c.Fields) sb.AppendLine($"{f.Label}：{f.Value}");
        foreach (var item in c.ListItems) sb.AppendLine($"・{item}");
        if (link is not null) sb.AppendLine().AppendLine($"{c.LinkText ?? "開啟單據"}：{link}");
        sb.AppendLine().AppendLine(c.Footer ?? "此信件由系統自動發送，請勿直接回覆。").AppendLine($"{company} 簽核系統");
        return sb.ToString();
    }

    private static string BuildHtml(string name, EmailContent c, string? link, string company)
    {
        static string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html lang=\"zh-Hant-TW\"><head><meta charset=\"utf-8\"></head>")
          .Append("<body style=\"margin:0;padding:24px;background:#f3f5f8;font-family:'Microsoft JhengHei','Noto Sans TC',sans-serif;color:#212529\">")
          .Append("<table role=\"presentation\" width=\"100%\" style=\"max-width:600px;margin:0 auto;background:#fff;border-radius:8px;border-collapse:collapse\">")
          .Append($"<tr><td style=\"background:#1f3a5f;color:#fff;padding:16px 24px;border-radius:8px 8px 0 0;font-size:15px\">{E(company)} 簽核系統</td></tr>")
          .Append("<tr><td style=\"padding:24px\">")
          .Append($"<h1 style=\"font-size:20px;margin:0 0 12px\">{E(c.Heading)}</h1>")
          .Append($"<p style=\"margin:0 0 8px\">{E(name)} 您好：</p><p style=\"margin:0 0 16px;line-height:1.6\">{E(c.Intro)}</p>");
        if (c.Fields.Count > 0)
        {
            sb.Append("<table role=\"presentation\" style=\"width:100%;border-collapse:collapse;font-size:14px;margin-bottom:16px\">");
            foreach (var f in c.Fields)
                sb.Append($"<tr><td style=\"padding:6px 8px;color:#6c757d;width:96px;border-bottom:1px solid #eee;vertical-align:top\">{E(f.Label)}</td>")
                  .Append($"<td style=\"padding:6px 8px;border-bottom:1px solid #eee\">{E(f.Value)}</td></tr>");
            sb.Append("</table>");
        }
        if (c.ListItems.Count > 0)
        {
            sb.Append("<ul style=\"padding-left:20px;margin:0 0 16px;line-height:1.8;font-size:14px\">");
            foreach (var item in c.ListItems) sb.Append($"<li>{E(item)}</li>");
            sb.Append("</ul>");
        }
        if (link is not null)
            sb.Append($"<p style=\"margin:24px 0\"><a href=\"{E(link)}\" style=\"background:#3d8bfd;color:#fff;text-decoration:none;padding:10px 20px;border-radius:6px;display:inline-block\">{E(c.LinkText ?? "開啟單據")}</a></p>");
        sb.Append($"<p style=\"color:#6c757d;font-size:12px;margin:24px 0 0\">{E(c.Footer ?? "此信件由系統自動發送，請勿直接回覆。")}</p>")
          .Append("</td></tr></table></body></html>");
        return sb.ToString();
    }
}
