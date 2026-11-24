using System.Security.Cryptography;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Application.Workflow;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Documents;

/// <summary>
/// 附件：副檔名白名單、大小與數量上限、檔頭檢查（避免改副檔名偽裝）、SHA-256。
/// 只有申請人能在可編輯狀態（草稿 / 已退回 / 已撤回）新增或刪除附件；能檢視單據的人才能下載。
/// </summary>
public sealed class AttachmentService(
    IRepository<Attachment> attachments,
    IRepository<Document> documents,
    IFileStorage storage,
    SettingsService settings,
    WorkflowEngine engine,
    AuditService audit,
    ICurrentUser currentUser,
    IClock clock)
{
    /// <summary>副檔名 → (Content-Type, 檔頭簽章)；簽章 null 代表不檢查</summary>
    private static readonly Dictionary<string, (string ContentType, byte[]? Magic)> KnownTypes = new()
    {
        [".pdf"] = ("application/pdf", "%PDF"u8.ToArray()),
        [".png"] = ("image/png", [0x89, 0x50, 0x4E, 0x47]),
        [".jpg"] = ("image/jpeg", [0xFF, 0xD8, 0xFF]),
        [".jpeg"] = ("image/jpeg", [0xFF, 0xD8, 0xFF]),
        [".gif"] = ("image/gif", "GIF8"u8.ToArray()),
        [".xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", [0x50, 0x4B, 0x03, 0x04]),
        [".docx"] = ("application/vnd.openxmlformats-officedocument.wordprocessingml.document", [0x50, 0x4B, 0x03, 0x04]),
        [".pptx"] = ("application/vnd.openxmlformats-officedocument.presentationml.presentation", [0x50, 0x4B, 0x03, 0x04]),
        [".zip"] = ("application/zip", [0x50, 0x4B, 0x03, 0x04]),
        [".txt"] = ("text/plain", null),
        [".csv"] = ("text/csv", null)
    };

    public static bool IsEditable(DocumentStatus s) => s is DocumentStatus.Draft or DocumentStatus.Returned or DocumentStatus.Withdrawn;

    public async Task<IReadOnlyList<Attachment>> ListAsync(int documentId) =>
        (await attachments.QueryAsync(a => a.DocumentId == documentId)).OrderBy(a => a.UploadedAt).ToList();

    /// <summary>已通過檢查、等待寫入的檔案</summary>
    public sealed record PreparedFile(string Name, string Ext, string ContentType, byte[] Data);

    /// <summary>
    /// 檢查檔案（類型、大小、數量、檔頭），不寫入任何資料。
    /// 單據儲存前先呼叫，避免「表單已存、附件被拒」造成重複草稿。
    /// </summary>
    public async Task<(List<PreparedFile> Files, List<(string? Field, string Message)> Errors)> PrepareAsync(int existingCount, IReadOnlyList<UploadFile> files)
    {
        var errors = new List<(string? Field, string Message)>();
        var prepared = new List<PreparedFile>();
        if (files.Count == 0) return (prepared, errors);
        var allowed = await settings.GetAllowedExtensionsAsync();
        var maxMb = await settings.GetIntAsync(SettingKeys.UploadMaxSizeMb, 10);
        var maxFiles = await settings.GetIntAsync(SettingKeys.UploadMaxFilesPerDocument, 10);
        if (existingCount + files.Count > maxFiles)
            errors.Add(("Files", $"每張單據最多 {maxFiles} 個附件（目前已有 {existingCount} 個）。"));

        foreach (var f in files)
        {
            var name = SanitizeFileName(f.FileName);
            var ext = Path.GetExtension(name).ToLowerInvariant();
            if (!allowed.Contains(ext) || !KnownTypes.TryGetValue(ext, out var known))
            {
                errors.Add(("Files", $"「{name}」的檔案類型不允許上傳（允許：{string.Join("、", allowed)}）。"));
                continue;
            }
            if (f.Length <= 0) { errors.Add(("Files", $"「{name}」是空檔案。")); continue; }
            if (f.Length > maxMb * 1024L * 1024L) { errors.Add(("Files", $"「{name}」超過 {maxMb} MB 上限。")); continue; }

            using var ms = new MemoryStream();
            await using (var s = f.OpenReadStream()) await s.CopyToAsync(ms);
            var data = ms.ToArray();
            if (known.Magic is { } magic && (data.Length < magic.Length || !data.AsSpan(0, magic.Length).SequenceEqual(magic)))
            {
                errors.Add(("Files", $"「{name}」的內容與副檔名不符，可能是偽裝的檔案。"));
                continue;
            }
            prepared.Add(new PreparedFile(name, ext, known.ContentType, data));
        }
        return (prepared, errors);
    }

    /// <summary>上傳多個檔案；任何一個不合格就全部不儲存，並回報所有問題</summary>
    public async Task<int> UploadAsync(int documentId, IReadOnlyList<UploadFile> files)
    {
        if (files.Count == 0) return 0;
        await RequireEditableAsync(documentId);
        var existing = (await attachments.QueryAsync(a => a.DocumentId == documentId)).Count;
        var (prepared, errors) = await PrepareAsync(existing, files);
        if (errors.Count > 0) throw new MultiValidationException(errors);
        return await StoreAsync(documentId, prepared);
    }

    /// <summary>寫入已檢查過的檔案</summary>
    public async Task<int> StoreAsync(int documentId, IReadOnlyList<PreparedFile> prepared)
    {
        foreach (var p in prepared)
        {
            using var ms = new MemoryStream(p.Data);
            var stored = await storage.SaveAsync(ms, p.Ext);
            var a = await attachments.AddAsync(new Attachment
            {
                DocumentId = documentId,
                FileName = p.Name,
                StoredFileName = stored,
                ContentType = p.ContentType,
                SizeBytes = p.Data.LongLength,
                Sha256 = Convert.ToHexStringLower(SHA256.HashData(p.Data)),
                UploadedById = currentUser.Id!.Value,
                UploadedAt = clock.Now
            });
            await audit.LogAsync("Upload", nameof(Attachment), a.Id.ToString(), $"上傳附件「{p.Name}」至單據 #{documentId}");
        }
        return prepared.Count;
    }

    public async Task DeleteAsync(int attachmentId)
    {
        var a = await attachments.GetByIdAsync(attachmentId) ?? throw new NotFoundException("附件", attachmentId);
        await RequireEditableAsync(a.DocumentId);
        await attachments.DeleteAsync(a.Id);
        storage.Delete(a.StoredFileName);
        await audit.LogAsync("Delete", nameof(Attachment), a.Id.ToString(), $"刪除附件「{a.FileName}」（單據 #{a.DocumentId}）");
    }

    /// <summary>下載；檢查使用者能否檢視該單據</summary>
    public async Task<(Attachment Attachment, Stream Content)> OpenAsync(int attachmentId)
    {
        var a = await attachments.GetByIdAsync(attachmentId) ?? throw new NotFoundException("附件", attachmentId);
        var doc = await documents.GetByIdAsync(a.DocumentId) ?? throw new NotFoundException("單據", a.DocumentId);
        if (!await engine.CanViewAsync(doc, currentUser.Id ?? 0)) throw new ForbiddenException("您沒有權限下載此附件。");
        var stream = storage.OpenRead(a.StoredFileName) ?? throw new BusinessException("附件檔案已遺失，請聯絡系統管理員。");
        return (a, stream);
    }

    /// <summary>刪除單據時一併刪除附件</summary>
    public async Task DeleteAllForDocumentAsync(int documentId)
    {
        foreach (var a in await attachments.QueryAsync(x => x.DocumentId == documentId))
        {
            await attachments.DeleteAsync(a.Id);
            storage.Delete(a.StoredFileName);
        }
    }

    private async Task<Document> RequireEditableAsync(int documentId)
    {
        var doc = await documents.GetByIdAsync(documentId) ?? throw new NotFoundException("單據", documentId);
        if (doc.ApplicantId != currentUser.Id) throw new ForbiddenException("只有申請人可以管理附件。");
        if (!IsEditable(doc.Status)) throw new BusinessException($"單據為「{doc.Status.ToDisplay()}」，目前不可變更附件。");
        return doc;
    }

    /// <summary>只保留檔名本身，移除路徑與控制字元，限制長度</summary>
    public static string SanitizeFileName(string? fileName)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/').Split('/').Last());
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Where(c => !char.IsControl(c) && !invalid.Contains(c)).ToArray()).Trim().Trim('.');
        if (name.Length == 0) name = "未命名檔案";
        if (name.Length > 100)
        {
            var ext = Path.GetExtension(name);
            name = name[..(100 - ext.Length)] + ext;
        }
        return name;
    }
}
