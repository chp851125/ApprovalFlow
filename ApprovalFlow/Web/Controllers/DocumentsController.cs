using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Documents;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Application.Workflow;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;
using ApprovalFlow.Web.Infrastructure;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ApprovalFlow.Web.Controllers;

/// <summary>單據：清單、新增 / 編輯、檢視、簽核動作、附件、批次核准</summary>
public class DocumentsController(
    WorkflowEngine engine,
    DocumentFormService forms,
    DocumentQueryService queries,
    AttachmentService attachmentService,
    LeaveBalanceService balanceService,
    ApprovalFlow.Application.Notifications.NotificationService notificationService,
    SettingsService settings,
    IRepository<Document> documents,
    IRepository<Employee> employees,
    IRepository<Department> departments,
    IRepository<WorkflowTemplate> templates,
    IRepository<DocumentTypeConfig> typeConfigs,
    IRepository<LeaveType> leaveTypes,
    IRepository<ExpenseCategory> categories,
    IRepository<BudgetSubject> budgetSubjects,
    IRepository<Supplier> suppliers,
    IClock clock) : Controller
{
    private const int PageSize = 15;
    private const long MaxRequestBytes = 120L * 1024 * 1024;

    // =====================================================================
    // 清單
    // =====================================================================

    public async Task<IActionResult> Index([FromQuery] DocumentListFilter filter)
    {
        var box = filter.ParsedBox;
        filter.Box = box.ToString().ToLowerInvariant();
        var rows = await queries.QueryAsync(new DocumentQuery
        {
            Box = box, Type = filter.Type, Status = filter.Status, Keyword = filter.Q,
            From = filter.From, To = filter.To, Sort = filter.Sort
        });
        return View(new DocumentListViewModel
        {
            Filter = filter,
            Rows = new PagedList<DocumentListItem>(rows, filter.Page, PageSize),
            Counts = await queries.CountsAsync(),
            BatchEnabled = box == DocumentBox.Todo && rows.Any(r => r.CanBatchApprove),
            Title = box switch
            {
                DocumentBox.Done => "已辦",
                DocumentBox.Mine => "我的申請",
                DocumentBox.Cc => "抄送給我",
                DocumentBox.Drafts => "草稿 / 待修改",
                _ => "待我簽核"
            }
        });
    }

    [HttpPost]
    public async Task<IActionResult> BatchApprove(List<int> ids, List<int> versions, string? comment, string? returnUrl)
    {
        var map = new Dictionary<int, int>();
        for (var i = 0; i < Math.Min(ids.Count, versions.Count); i++) map[ids[i]] = versions[i];
        try
        {
            var result = await queries.BatchApproveAsync(map.Select(kv => (kv.Key, kv.Value)).ToList(), comment);
            if (result.Failures.Count == 0) TempData.SetSuccess($"已批次核准 {result.Succeeded} 張單據。");
            else
            {
                if (result.Succeeded > 0) TempData.SetSuccess($"已批次核准 {result.Succeeded} 張單據。");
                TempData.SetError($"{result.Failures.Count} 張未核准：" + string.Join("；", result.Failures.Take(5)) + (result.Failures.Count > 5 ? "…" : ""));
            }
        }
        catch (BusinessException ex) when (ex is not ForbiddenException)
        {
            TempData.SetError(ex.Message);
        }
        return !string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index));
    }

    // =====================================================================
    // 新增 / 編輯
    // =====================================================================

    /// <summary>選擇單別</summary>
    public async Task<IActionResult> New()
    {
        var active = (await templates.QueryAsync(t => t.IsActive)).Select(t => t.DocumentType).ToHashSet();
        var options = (await typeConfigs.QueryAsync(c => c.IsEnabled)).OrderBy(c => c.DocumentType)
            .Select(c => new DocumentTypeOption { Config = c, HasActiveTemplate = active.Contains(c.DocumentType) }).ToList();
        return View(options);
    }

    [HttpGet]
    public async Task<IActionResult> Create(DocumentType type)
    {
        var cfg = (await typeConfigs.QueryAsync(c => c.DocumentType == type)).FirstOrDefault();
        if (cfg is not { IsEnabled: true }) throw new BusinessException($"「{type.ToDisplay()}」目前停止申請。");
        return View("Edit", await BuildEditVm(forms.NewInput(type), null));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var doc = await documents.GetByIdAsync(id) ?? throw new NotFoundException("單據", id);
        if (doc.ApplicantId != User.GetUserId()) throw new ForbiddenException("只有申請人可以修改此單據。");
        if (!AttachmentService.IsEditable(doc.Status))
        {
            TempData.SetInfo($"單據為「{doc.Status.ToDisplay()}」，無法修改。");
            return RedirectToAction(nameof(Details), new { id });
        }
        return View(await BuildEditVm(DocumentFormService.ToInput(doc), doc));
    }

    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<IActionResult> Save([Bind(Prefix = "Input")] DocumentFormInput input, List<IFormFile>? files, string? submitAction)
    {
        var submit = submitAction == "submit";
        var uploads = (files ?? []).Where(f => f.Length > 0 || !string.IsNullOrEmpty(f.FileName))
            .Select(f => new UploadFile(f.FileName, f.Length, f.OpenReadStream)).ToList();
        if (ModelState.IsValid)
        {
            try
            {
                var doc = await forms.SaveAsync(input, uploads, submit);
                if (!submit)
                {
                    TempData.SetSuccess("已儲存草稿。");
                    return RedirectToAction(nameof(Edit), new { id = doc.Id });
                }
                TempData.SetSuccess(doc.Status == DocumentStatus.Approved
                    ? $"已送出 {doc.DocumentNo}，流程無需簽核，已自動核准。"
                    : $"已送出 {doc.DocumentNo}，目前關卡：{doc.GetCurrentStep()?.Name}。");
                return RedirectToAction(nameof(Details), new { id = doc.Id });
            }
            catch (BusinessException ex) when (ex is not ForbiddenException)
            {
                ModelState.AddBusinessError(ex);
            }
        }
        if (uploads.Count > 0)
            ModelState.AddModelError("Files", "因表單有錯誤，這次選擇的附件尚未上傳，請修正後重新選擇檔案。");
        var existing = input.Id == 0 ? null : await documents.GetByIdAsync(input.Id);
        return View("Edit", await BuildEditVm(input, existing));
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, int version)
    {
        await forms.DeleteDraftAsync(id, version);
        TempData.SetSuccess("已刪除草稿。");
        return RedirectToAction(nameof(Index), new { box = "drafts" });
    }

    /// <summary>計算請假時數並回傳餘額（表單即時試算用）</summary>
    [HttpGet]
    public async Task<IActionResult> LeaveHours(DateTime? start, DateTime? end, int? leaveTypeId, int? documentId)
    {
        decimal hours = 0;
        if (start is { } s && end is { } e && e > s) hours = await forms.CalculateLeaveHoursAsync(s, e);
        var year = (start ?? clock.Today.ToDateTime(TimeOnly.MinValue)).Year;
        var rows = await balanceService.GetBalancesAsync(User.GetUserId()!.Value, year, documentId);
        var row = rows.FirstOrDefault(r => r.Type.Id == leaveTypeId);
        return Json(new
        {
            hours,
            text = hours > 0 ? $"{hours:0.#} 小時" + (hours >= 8 ? $"（{hours / 8:0.##} 天）" : "") : "未涵蓋工作時段",
            minUnit = row?.Type.MinUnitHours,
            remaining = row?.Remaining,
            remainingText = row?.Remaining is decimal r ? $"{r:0.#} 小時（{r / 8:0.##} 天）" : "不限",
            insufficient = row?.Remaining is decimal rem && hours > rem
        });
    }

    // =====================================================================
    // 附件
    // =====================================================================

    [HttpGet]
    public async Task<IActionResult> Attachment(int id)
    {
        var (a, stream) = await attachmentService.OpenAsync(id);
        Response.Headers.CacheControl = "private, no-store";
        // 一律以下載方式提供，避免瀏覽器直接執行內容
        return File(stream, a.ContentType, a.FileName);
    }

    [HttpPost]
    public async Task<IActionResult> DeleteAttachment(int attachmentId, int documentId)
    {
        await attachmentService.DeleteAsync(attachmentId);
        TempData.SetSuccess("已刪除附件。");
        return RedirectToAction(nameof(Edit), new { id = documentId });
    }

    // =====================================================================
    // 檢視與簽核
    // =====================================================================

    public async Task<IActionResult> Details(int id)
    {
        var doc = await documents.GetByIdAsync(id) ?? throw new NotFoundException("單據", id);
        var me = User.GetUserId()!.Value;
        var permissions = await engine.GetPermissionsAsync(doc, me);
        if (!permissions.CanView) throw new ForbiddenException("您沒有權限檢視此單據。");
        await notificationService.MarkReadForDocumentAsync(id);

        var all = await employees.GetAllAsync();
        var depts = (await departments.GetAllAsync()).ToDictionary(d => d.Id, d => d.Name);
        var tpl = doc.TemplateId is int tid ? await templates.GetByIdAsync(tid) : null;
        var currentPending = doc.GetCurrentStep()?.Assignees.Where(a => a.Decision == AssigneeDecision.Pending).Select(a => a.EmployeeId).ToHashSet() ?? [];
        var isApplicant = doc.ApplicantId == me;

        return View(new DocumentDetailsViewModel
        {
            Document = doc,
            Permissions = permissions,
            Names = all.ToDictionary(e => e.Id, e => e.Name),
            ApplicantName = all.FirstOrDefault(e => e.Id == doc.ApplicantId)?.Name ?? "",
            DepartmentName = depts.GetValueOrDefault(doc.DepartmentId) ?? "",
            TemplateName = tpl is null ? null : $"{tpl.Name}（v{tpl.TemplateVersion}）",
            CurrentUserId = me,
            Now = clock.Now,
            Attachments = await attachmentService.ListAsync(doc.Id),
            Lookups = await BuildLookupsAsync(),
            CanEdit = isApplicant && AttachmentService.IsEditable(doc.Status),
            CanDeleteDraft = isApplicant && doc.Status == DocumentStatus.Draft && string.IsNullOrEmpty(doc.DocumentNo),
            TargetCandidates = permissions.CanApprove
                ? all.Where(e => e.IsActive && e.Id != doc.ApplicantId && e.Id != me && !currentPending.Contains(e.Id))
                    .OrderBy(e => e.DepartmentId).ThenBy(e => e.EmployeeNo)
                    .Select(e => new SelectListItem($"{e.Name}・{depts.GetValueOrDefault(e.DepartmentId)}", e.Id.ToString()))
                    .ToList()
                : []
        });
    }

    /// <summary>所有簽核動作的單一入口；實際權限與狀態檢查由流程引擎負責</summary>
    [HttpPost]
    public async Task<IActionResult> Act(int id, int version, string action, string? comment, int? targetId, int? asPrincipalId)
    {
        try
        {
            var doc = action switch
            {
                "submit" => await engine.SubmitAsync(id, version, comment),
                "approve" => await engine.ApproveAsync(id, version, comment, asPrincipalId),
                "reject" => await engine.RejectAsync(id, version, comment, asPrincipalId),
                "returnApplicant" => await engine.ReturnToApplicantAsync(id, version, comment, asPrincipalId),
                "returnPrevious" => await engine.ReturnToPreviousAsync(id, version, comment, asPrincipalId),
                "withdraw" => await engine.WithdrawAsync(id, version, comment),
                "addSigner" => await engine.AddSignerAsync(id, version, targetId ?? 0, comment, asPrincipalId),
                "transfer" => await engine.TransferAsync(id, version, targetId ?? 0, comment, asPrincipalId),
                "comment" => await engine.CommentAsync(id, version, comment),
                _ => throw new BusinessException("不支援的操作。")
            };
            TempData.SetSuccess(action switch
            {
                "submit" => doc.Status == DocumentStatus.Approved ? $"已送出 {doc.DocumentNo}，流程無需簽核，已自動核准。" : $"已送出 {doc.DocumentNo}，目前關卡：{doc.GetCurrentStep()?.Name}。",
                "approve" => doc.Status == DocumentStatus.Approved ? "已核准，流程全部完成。" : "已核准。",
                "reject" => "已駁回此單據。",
                "returnApplicant" => "已退回申請人修改。",
                "returnPrevious" => $"已退回上一關「{doc.GetCurrentStep()?.Name}」。",
                "withdraw" => "已撤回單據，可修改後重新送出。",
                "addSigner" => "已加簽，您核准後會送至加簽人員。",
                "transfer" => "已轉簽。",
                _ => "已送出留言。"
            });
            // 簽核人處理完畢後回到待辦清單，方便連續處理
            if (action is "approve" or "reject" or "returnApplicant" or "returnPrevious" or "transfer" && Request.Form["next"] == "todo")
                return RedirectToAction(nameof(Index), new { box = "todo" });
        }
        catch (BusinessException ex) when (ex is not ForbiddenException)
        {
            TempData.SetError(ex.Message);
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    // =====================================================================
    // 內部
    // =====================================================================

    private async Task<ContentLookups> BuildLookupsAsync() => new()
    {
        Employees = (await employees.GetAllAsync()).ToDictionary(e => e.Id, e => e.Name),
        LeaveTypes = (await leaveTypes.GetAllAsync()).ToDictionary(x => x.Id),
        Categories = (await categories.GetAllAsync()).ToDictionary(x => x.Id),
        BudgetSubjects = (await budgetSubjects.GetAllAsync()).ToDictionary(x => x.Id),
        Suppliers = (await suppliers.GetAllAsync()).ToDictionary(x => x.Id)
    };

    private async Task<DocumentEditViewModel> BuildEditVm(DocumentFormInput input, Document? existing)
    {
        var me = User.GetUserId()!.Value;
        var meEmp = await employees.GetByIdAsync(me);
        var depts = (await departments.GetAllAsync()).ToDictionary(d => d.Id);
        var cats = (await categories.GetAllAsync()).OrderBy(c => c.SortOrder).ToList();
        var year = input.Leave.StartAt?.Year ?? clock.Today.Year;

        return new DocumentEditViewModel
        {
            Input = input,
            Existing = existing,
            Config = (await typeConfigs.QueryAsync(c => c.DocumentType == input.DocumentType)).FirstOrDefault(),
            Attachments = existing is null ? [] : await attachmentService.ListAsync(existing.Id),
            LeaveBalances = input.DocumentType == DocumentType.Leave ? await balanceService.GetBalancesAsync(me, year, existing?.Id) : [],
            LeaveTypes = (await leaveTypes.GetAllAsync()).Where(l => l.IsActive || l.Id == input.Leave.LeaveTypeId).OrderBy(l => l.SortOrder)
                .Select(l => new SelectListItem(l.Name, l.Id.ToString())),
            Categories = cats.Where(c => c.IsActive || input.Expense.Items.Any(i => i.CategoryId == c.Id))
                .Select(c => new SelectListItem(c.Name, c.Id.ToString())),
            CategoryInfo = cats,
            BudgetSubjects = (await budgetSubjects.GetAllAsync()).Where(b => b.IsActive || b.Id == input.Purchase.BudgetSubjectId).OrderBy(b => b.Code)
                .Select(b => new SelectListItem($"{b.Code} {b.Name}", b.Id.ToString())),
            Suppliers = (await suppliers.GetAllAsync()).Where(s => s.IsActive || s.Id == input.Purchase.SupplierId).OrderBy(s => s.Code)
                .Select(s => new SelectListItem($"{s.Name}（{s.Category}）", s.Id.ToString())),
            Colleagues = (await employees.QueryAsync(e => e.IsActive && e.Id != me))
                .OrderByDescending(e => e.DepartmentId == meEmp?.DepartmentId)
                .ThenBy(e => depts.GetValueOrDefault(e.DepartmentId)?.SortOrder).ThenBy(e => e.EmployeeNo)
                .Select(e => new SelectListItem($"{e.Name}・{depts.GetValueOrDefault(e.DepartmentId)?.Name}", e.Id.ToString()))
                .ToList(),
            AllowedExtensions = string.Join(",", await settings.GetAllowedExtensionsAsync()),
            MaxFileMb = await settings.GetIntAsync(SettingKeys.UploadMaxSizeMb, 10),
            MaxFiles = await settings.GetIntAsync(SettingKeys.UploadMaxFilesPerDocument, 10)
        };
    }
}
