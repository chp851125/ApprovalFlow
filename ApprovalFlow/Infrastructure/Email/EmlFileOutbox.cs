using System.Text;
using System.Text.RegularExpressions;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Infrastructure.Persistence;
using Microsoft.Extensions.Options;

namespace ApprovalFlow.Infrastructure.Email;

/// <summary>
/// 模擬寄信：把郵件寫成 RFC 5322 格式的 .eml（multipart/alternative，UTF-8 + Base64），
/// 存放於 outbox/yyyyMMdd/。
/// </summary>
public sealed partial class EmlFileOutbox(IOptions<DataStoreOptions> options) : IEmailOutbox
{
    private readonly string _root = options.Value.OutboxFullPath;

    [GeneratedRegex(@"^\d{8}/[0-9]{6}_[0-9a-f]{12}\.eml$")]
    private static partial Regex FileNamePattern();

    public async Task<string> WriteAsync(EmailEnvelope e, CancellationToken ct = default)
    {
        var name = $"{e.Date:yyyyMMdd}/{e.Date:HHmmss}_{Guid.NewGuid():N}"[..(8 + 1 + 6 + 1 + 12)] + ".eml";
        var path = Path.Combine(_root, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var boundary = "=_af_" + Guid.NewGuid().ToString("N");
        var sb = new StringBuilder();
        sb.Append("Date: ").Append(e.Date.ToString("ddd, dd MMM yyyy HH:mm:ss ", System.Globalization.CultureInfo.InvariantCulture))
          .Append(e.Date.ToString("zzz").Replace(":", "")).Append("\r\n");
        sb.Append("From: ").Append(Address(e.FromName, e.From)).Append("\r\n");
        sb.Append("To: ").Append(Address(e.ToName, e.To)).Append("\r\n");
        sb.Append("Subject: ").Append(EncodeHeader(e.Subject)).Append("\r\n");
        sb.Append("Message-ID: <").Append(Guid.NewGuid().ToString("N")).Append('@').Append(Domain(e.From)).Append(">\r\n");
        sb.Append("MIME-Version: 1.0\r\n");
        sb.Append("X-Mailer: ApprovalFlow (simulated outbox)\r\n");
        sb.Append("Content-Type: multipart/alternative; boundary=\"").Append(boundary).Append("\"\r\n\r\n");
        AppendPart(sb, boundary, "text/plain", e.TextBody);
        AppendPart(sb, boundary, "text/html", e.HtmlBody);
        sb.Append("--").Append(boundary).Append("--\r\n");

        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, sb.ToString(), new UTF8Encoding(false), ct);
        File.Move(temp, path, overwrite: true);
        return name;
    }

    public Stream? OpenRead(string fileName)
    {
        if (!FileNamePattern().IsMatch(fileName)) return null;
        var full = Path.GetFullPath(Path.Combine(_root, fileName.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(Path.GetFullPath(_root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return null;
        return File.Exists(full) ? new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read) : null;
    }

    private static void AppendPart(StringBuilder sb, string boundary, string contentType, string body)
    {
        sb.Append("--").Append(boundary).Append("\r\n");
        sb.Append("Content-Type: ").Append(contentType).Append("; charset=utf-8\r\n");
        sb.Append("Content-Transfer-Encoding: base64\r\n\r\n");
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body));
        for (var i = 0; i < b64.Length; i += 76) sb.Append(b64, i, Math.Min(76, b64.Length - i)).Append("\r\n");
        sb.Append("\r\n");
    }

    /// <summary>非 ASCII 標頭以 RFC 2047 encoded-word 編碼；並移除換行避免標頭注入</summary>
    private static string EncodeHeader(string value)
    {
        value = value.Replace("\r", " ").Replace("\n", " ");
        return value.All(c => c < 128) ? value : $"=?utf-8?B?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?=";
    }

    private static string Address(string name, string address)
    {
        address = address.Replace("\r", "").Replace("\n", "").Replace("<", "").Replace(">", "");
        return string.IsNullOrWhiteSpace(name) ? address : $"{EncodeHeader(name)} <{address}>";
    }

    private static string Domain(string address) => address.Contains('@') ? address[(address.IndexOf('@') + 1)..] : "localhost";
}
