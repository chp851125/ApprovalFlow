using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Common;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Application.Workflow;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Documents;

/// <summary>
/// 單據表單：建立 / 修改草稿、計算衍生欄位（時數、金額、主旨）、送出前驗證，送出交由流程引擎。
/// 草稿只檢查格式；送出時才做完整商業規則檢查（餘額、上限、收據、重疊…），並一次回報所有問題。
/// </summary>
public sealed class DocumentFormService(
    IRepository<Document> documents,
    IRepository<Employee> employees,
    IRepository<Department> departments,
    IRepository<LeaveType> leaveTypes,
    IRepository<ExpenseCategory> categories,
    IRepository<BudgetSubject> budgetSubjects,
    IRepository<Supplier> suppliers,
    IRepository<Holiday> holidays,
    IRepository<DocumentTypeConfig> typeConfigs,
    IRepository<Attachment> attachments,
    LeaveBalanceService balances,
    AttachmentService attachmentService,
    WorkflowEngine engine,
    AuditService audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public const int MaxItems = 30;
    public const int MaxCc = 10;
    public const int ExpenseDeadlineDays = 60;
    public const decimal MonthlyOvertimeCap = 46m;

    private sealed class Errors : List<(string? Field, string Message)>
    {
        public void Add(string field, string message) => Add((field, message));
    }

    /// <summary>建立新草稿的預設值</summary>
    public DocumentFormInput NewInput(DocumentType type)
    {
        var today = clock.Today;
        var input = new DocumentFormInput { DocumentType = type };
        switch (type)
        {
            case DocumentType.Leave:
                var next = today.AddDays(1);
                input.Leave.StartAt = next.ToDateTime(new TimeOnly(9, 0));
                input.Leave.EndAt = next.ToDateTime(new TimeOnly(18, 0));
                break;
            case DocumentType.Expense:
                input.Expense.Items.Add(new ExpenseItemInput { Date = today });
                break;
            case DocumentType.Purchase:
                input.Purchase.Items.Add(new PurchaseItemInput { Quantity = 1, Unit = "個" });
                input.Purchase.RequiredDate = today.AddDays(14);
                break;
            case DocumentType.Overtime:
                input.Overtime.Date = today;
                input.Overtime.StartTime = new TimeOnly(18, 30);
                input.Overtime.EndTime = new TimeOnly(20, 30);
                break;
        }
        return input;
    }

    /// <summary>把既有單據轉回表單輸入（編輯用）</summary>
    public static DocumentFormInput ToInput(Document d)
    {
        var c = d.Content;
        var input = new DocumentFormInput
        {
            Id = d.Id, Version = d.Version, DocumentType = d.DocumentType, Title = d.Title,
            IsUrgent = d.IsUrgent, Remark = c.Remark, CcEmployeeIds = [.. d.CcEmployeeIds]
        };
        if (c.Leave is { } l)
            input.Leave = new LeaveFormInput { LeaveTypeId = l.LeaveTypeId, StartAt = l.StartAt.DateTime, EndAt = l.EndAt.DateTime, Reason = l.Reason, DeputyId = l.DeputyId };
        if (c.Expense is { } e)
            input.Expense = new ExpenseFormInput
            {
                PaymentMethod = e.PaymentMethod, ProjectCode = e.ProjectCode, Purpose = e.Purpose,
                Items = e.Items.Select(i => new ExpenseItemInput { Date = i.Date, CategoryId = i.CategoryId, Description = i.Description, Amount = i.Amount, ReceiptNo = i.ReceiptNo }).ToList()
            };
        if (c.Purchase is { } p)
            input.Purchase = new PurchaseFormInput
            {
                SupplierId = p.SupplierId, BudgetSubjectId = p.BudgetSubjectId, RequiredDate = p.RequiredDate, Purpose = p.Purpose, DeliveryLocation = p.DeliveryLocation,
                Items = p.Items.Select(i => new PurchaseItemInput { Name = i.Name, Specification = i.Specification, Quantity = i.Quantity, Unit = i.Unit, UnitPrice = i.UnitPrice }).ToList()
            };
        if (c.Overtime is { } o)
            input.Overtime = new OvertimeFormInput { Date = o.Date, StartTime = o.StartTime, EndTime = o.EndTime, Compensation = o.Compensation, Reason = o.Reason };
        return input;
    }

    /// <summary>儲存草稿；submit = true 時通過完整驗證後交由流程引擎送出</summary>
    public async Task<Document> SaveAsync(DocumentFormInput input, IReadOnlyList<UploadFile> files, bool submit)
    {
        var me = currentUser.Id ?? throw new ForbiddenException();
        var applicant = await employees.GetByIdAsync(me) ?? throw new ForbiddenException();
        var config = (await typeConfigs.QueryAsync(c => c.DocumentType == input.DocumentType)).FirstOrDefault();
        if (config is { IsEnabled: false }) throw new BusinessException($"「{input.DocumentType.ToDisplay()}」目前停止申請。");

        Document doc;
        var isNew = input.Id == 0;
        if (isNew)
        {
            doc = new Document
            {
                DocumentType = input.DocumentType,
                ApplicantId = me,
                DepartmentId = applicant.DepartmentId,
                Status = DocumentStatus.Draft,
                CreatedAt = clock.Now
            };
        }
        else
        {
            doc = await documents.GetByIdAsync(input.Id) ?? throw new NotFoundException("單據", input.Id);
            if (doc.ApplicantId != me) throw new ForbiddenException("只有申請人可以修改此單據。");
            if (!AttachmentService.IsEditable(doc.Status))
                throw new BusinessException($"單據為「{doc.Status.ToDisplay()}」，無法修改。");
            if (doc.Version != input.Version) throw new ConcurrencyException(nameof(Document), doc.Id);
            if (doc.DocumentType != input.DocumentType) throw new ValidationException("不可變更單別。");
        }

        var errors = new Errors();
        await ApplyAsync(doc, input, applicant, errors, strict: submit);

        // 附件先檢查再存單據，任何錯誤都不會留下半成品
        var existingCount = isNew ? 0 : (await attachments.QueryAsync(a => a.DocumentId == doc.Id)).Count;
        var (prepared, fileErrors) = await attachmentService.PrepareAsync(existingCount, files);
        errors.AddRange(fileErrors);
        // 附件數量（含這次上傳）用於「需附證明 / 收據」檢查
        if (submit) await ValidateForSubmitAsync(doc, applicant, existingCount + prepared.Count, errors);
        if (errors.Count > 0) throw new MultiValidationException(errors);

        doc.UpdatedAt = clock.Now;
        if (isNew) doc = await documents.AddAsync(doc);
        else await documents.UpdateAsync(doc);

        if (prepared.Count > 0) await attachmentService.StoreAsync(doc.Id, prepared);
        await audit.LogAsync(isNew ? "Create" : "Update", nameof(Document), doc.Id.ToString(),
            $"{(isNew ? "建立" : "修改")}{doc.DocumentType.ToDisplay()}草稿「{doc.Title}」");

        if (submit)
        {
            var resubmit = doc.Status != DocumentStatus.Draft;
            doc = await engine.SubmitAsync(doc.Id, doc.Version, resubmit ? doc.Content.Remark : null);
        }
        return doc;
    }

    /// <summary>刪除從未送出的草稿（已送出過的單據保留紀錄，只能撤回）</summary>
    public async Task DeleteDraftAsync(int id, int version)
    {
        var doc = await documents.GetByIdAsync(id) ?? throw new NotFoundException("單據", id);
        if (doc.ApplicantId != currentUser.Id) throw new ForbiddenException();
        if (doc.Status != DocumentStatus.Draft || !string.IsNullOrEmpty(doc.DocumentNo))
            throw new BusinessException("只有尚未送出過的草稿可以刪除。");
        if (doc.Version != version) throw new ConcurrencyException(nameof(Document), id);
        await attachmentService.DeleteAllForDocumentAsync(id);
        await documents.DeleteAsync(id);
        await audit.LogAsync("Delete", nameof(Document), id.ToString(), $"刪除草稿「{doc.Title}」");
    }

    // =====================================================================
    // 套用輸入、計算衍生欄位
    // =====================================================================

    private async Task ApplyAsync(Document doc, DocumentFormInput input, Employee applicant, Errors errors, bool strict)
    {
        // 主旨規則：使用者自訂的主旨會保留；若原本是自動產生的（或留白），則隨內容重新產生
        var oldTitle = doc.Title;
        var oldAuto = doc.Id == 0 ? null : await AutoTitleAsync(doc, applicant);
        var wasAuto = string.IsNullOrEmpty(oldTitle) || oldTitle == oldAuto || oldTitle.EndsWith("（未命名）");

        doc.IsUrgent = input.IsUrgent;
        doc.Content ??= new DocumentContent();
        doc.Content.Remark = Clean(input.Remark);

        var cc = input.CcEmployeeIds.Distinct().Where(id => id != applicant.Id).ToList();
        if (cc.Count > MaxCc) errors.Add("CcEmployeeIds", $"抄送最多 {MaxCc} 人。");
        var activeIds = (await employees.QueryAsync(e => e.IsActive)).Select(e => e.Id).ToHashSet();
        if (cc.Any(id => !activeIds.Contains(id))) errors.Add("CcEmployeeIds", "抄送名單中有已停用的人員。");
        doc.CcEmployeeIds = cc;

        switch (doc.DocumentType)
        {
            case DocumentType.Leave: await ApplyLeaveAsync(doc, input.Leave, applicant, errors, activeIds); break;
            case DocumentType.Expense: await ApplyExpenseAsync(doc, input.Expense, errors); break;
            case DocumentType.Purchase: await ApplyPurchaseAsync(doc, input.Purchase, applicant, errors, strict); break;
            case DocumentType.Overtime: await ApplyOvertimeAsync(doc, input.Overtime, errors); break;
        }

        var userTitle = Clean(input.Title);
        var newAuto = await AutoTitleAsync(doc, applicant);
        if (userTitle is not null && userTitle != oldTitle) doc.Title = userTitle;
        else if (wasAuto || userTitle is null) doc.Title = newAuto ?? $"{doc.DocumentType.ToDisplay()}（未命名）";
        // 否則保留原本自訂的主旨
    }

    /// <summary>依內容產生主旨；內容不足時回傳 null</summary>
    private async Task<string?> AutoTitleAsync(Document doc, Employee applicant)
    {
        var c = doc.Content;
        switch (doc.DocumentType)
        {
            case DocumentType.Leave when c.Leave is { Hours: > 0 } l && l.LeaveTypeId != 0:
                var type = await leaveTypes.GetByIdAsync(l.LeaveTypeId);
                var sd = DateOnly.FromDateTime(l.StartAt.DateTime);
                var ed = DateOnly.FromDateTime(l.EndAt.DateTime);
                return type is null ? null : $"{type.Name} {sd:M/d}{(ed != sd ? $"–{ed:M/d}" : "")}（{LeaveQuota.FormatHours(l.Hours)}）";
            case DocumentType.Expense when c.Expense is { } e && e.Items.Count > 0 && e.Purpose.Length > 0:
                return $"{e.Items.Min(i => i.Date).Month} 月 {e.Purpose}";
            case DocumentType.Purchase when c.Purchase is { } p && p.Items.Count > 0 && p.Items[0].Name.Length > 0:
                var dept = await departments.GetByIdAsync(applicant.DepartmentId);
                return $"{dept?.Name} {p.Items[0].Name}採購" + (p.Items.Count > 1 ? $"等 {p.Items.Count} 項" : "");
            case DocumentType.Overtime when c.Overtime is { Hours: > 0 } o:
                return $"{(o.IsHoliday ? "假日" : "平日")}加班 {o.Date:M/d}（{o.Hours:0.#} 小時）{o.Reason}".Trim();
            default:
                return null;
        }
    }

    private async Task ApplyLeaveAsync(Document doc, LeaveFormInput f, Employee applicant, Errors errors, HashSet<int> activeIds)
    {
        decimal hours = 0;
        if (f.StartAt is { } s && f.EndAt is { } e)
        {
            if (e <= s) errors.Add("Leave.EndAt", "結束時間必須晚於開始時間。");
            else hours = await CalculateLeaveHoursAsync(s, e);
        }
        if (f.DeputyId is int dep)
        {
            if (dep == applicant.Id) errors.Add("Leave.DeputyId", "職務代理人不可為本人。");
            else if (!activeIds.Contains(dep)) errors.Add("Leave.DeputyId", "職務代理人已停用。");
        }

        doc.Content.Leave = new LeaveForm
        {
            LeaveTypeId = f.LeaveTypeId,
            StartAt = f.StartAt is { } a ? ToTaipei(a) : default,
            EndAt = f.EndAt is { } b ? ToTaipei(b) : default,
            Hours = hours,
            Reason = Clean(f.Reason) ?? "",
            DeputyId = f.DeputyId
        };
        doc.Hours = hours > 0 ? hours : null;
        doc.Amount = null;
        // 職務代理人自動列入抄送
        if (f.DeputyId is int d && d != applicant.Id && !doc.CcEmployeeIds.Contains(d)) doc.CcEmployeeIds.Insert(0, d);
    }

    private Task ApplyExpenseAsync(Document doc, ExpenseFormInput f, Errors errors)
    {
        var items = f.Items.Where(i => !IsBlank(i)).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.Amount is < 0) errors.Add($"Expense.Items[{i}].Amount", $"第 {i + 1} 筆金額不可為負數。");
        }
        if (items.Count > MaxItems) errors.Add("Expense.Items", $"明細最多 {MaxItems} 筆。");

        var form = new ExpenseForm
        {
            Items = items.Select(i => new ExpenseItem
            {
                Date = i.Date ?? clock.Today,
                CategoryId = i.CategoryId,
                Description = Clean(i.Description) ?? "",
                Amount = Math.Round(i.Amount ?? 0, 0),
                ReceiptNo = Clean(i.ReceiptNo)?.ToUpperInvariant()
            }).ToList(),
            PaymentMethod = f.PaymentMethod,
            ProjectCode = Clean(f.ProjectCode)?.ToUpperInvariant(),
            Purpose = Clean(f.Purpose) ?? ""
        };
        form.TotalAmount = form.Items.Sum(i => i.Amount); // 金額一律由伺服器重新加總
        doc.Content.Expense = form;
        doc.Amount = form.TotalAmount;
        doc.Hours = null;
        return Task.CompletedTask;
    }

    private async Task ApplyPurchaseAsync(Document doc, PurchaseFormInput f, Employee applicant, Errors errors, bool strict)
    {
        var items = f.Items.Where(i => !IsBlank(i)).ToList();
        if (items.Count > MaxItems) errors.Add("Purchase.Items", $"品項最多 {MaxItems} 筆。");
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Quantity is < 0) errors.Add($"Purchase.Items[{i}].Quantity", $"第 {i + 1} 項數量不可為負數。");
            if (items[i].UnitPrice is < 0) errors.Add($"Purchase.Items[{i}].UnitPrice", $"第 {i + 1} 項單價不可為負數。");
        }

        var form = new PurchaseForm
        {
            SupplierId = f.SupplierId,
            BudgetSubjectId = f.BudgetSubjectId,
            RequiredDate = f.RequiredDate,
            Purpose = Clean(f.Purpose) ?? "",
            DeliveryLocation = Clean(f.DeliveryLocation),
            Items = items.Select(i =>
            {
                var qty = i.Quantity ?? 0;
                var price = i.UnitPrice ?? 0;
                return new PurchaseItem
                {
                    Name = Clean(i.Name) ?? "",
                    Specification = Clean(i.Specification),
                    Quantity = qty,
                    Unit = Clean(i.Unit) ?? "個",
                    UnitPrice = price,
                    Amount = Math.Round(qty * price, 0)
                };
            }).ToList()
        };
        form.TotalAmount = form.Items.Sum(i => i.Amount);
        doc.Content.Purchase = form;
        doc.Amount = form.TotalAmount;
        doc.Hours = null;
    }

    private async Task ApplyOvertimeAsync(Document doc, OvertimeFormInput f, Errors errors)
    {
        decimal hours = 0;
        var isHoliday = false;
        if (f.Date is { } date)
        {
            var calendar = new WorkCalendar(await holidays.GetAllAsync());
            isHoliday = !calendar.IsWorkday(date);
        }
        if (f.StartTime is { } s && f.EndTime is { } e)
        {
            if (e <= s) errors.Add("Overtime.EndTime", "結束時間必須晚於開始時間（跨日請分兩張申請）。");
            else hours = Math.Floor((decimal)(e - s).TotalHours * 2) / 2; // 以 0.5 小時為單位
        }
        doc.Content.Overtime = new OvertimeForm
        {
            Date = f.Date ?? clock.Today,
            StartTime = f.StartTime ?? default,
            EndTime = f.EndTime ?? default,
            Hours = hours,
            Compensation = f.Compensation,
            Reason = Clean(f.Reason) ?? "",
            IsHoliday = isHoliday
        };
        doc.Hours = hours > 0 ? hours : null;
        doc.Amount = null;
    }

    // =====================================================================
    // 送出前完整驗證
    // =====================================================================

    private async Task ValidateForSubmitAsync(Document doc, Employee applicant, int attachmentCount, Errors errors)
    {
        var today = clock.Today;
        switch (doc.DocumentType)
        {
            case DocumentType.Leave:
            {
                var f = doc.Content.Leave!;
                var type = f.LeaveTypeId == 0 ? null : await leaveTypes.GetByIdAsync(f.LeaveTypeId);
                if (type is null) { errors.Add("Leave.LeaveTypeId", "請選擇假別。"); break; }
                if (!type.IsActive) errors.Add("Leave.LeaveTypeId", $"假別「{type.Name}」已停用。");
                if (f.StartAt == default || f.EndAt == default) { errors.Add("Leave.StartAt", "請填寫請假起訖時間。"); break; }
                if (f.Hours <= 0) { errors.Add("Leave.StartAt", "請假期間沒有涵蓋任何工作時段（09:00–12:00、13:00–18:00，不含假日）。"); break; }
                if (f.Hours % type.MinUnitHours != 0)
                    errors.Add("Leave.StartAt", $"「{type.Name}」最小請假單位為 {type.MinUnitHours:0.#} 小時，目前為 {f.Hours:0.#} 小時。");
                if (DateOnly.FromDateTime(f.StartAt.DateTime) < today.AddDays(-30))
                    errors.Add("Leave.StartAt", "請假開始日不可早於 30 天前（補請假請洽人資）。");
                if (f.StartAt.Year != f.EndAt.Year) errors.Add("Leave.EndAt", "跨年度請假請分成兩張申請。");
                if (string.IsNullOrWhiteSpace(f.Reason)) errors.Add("Leave.Reason", "請填寫請假事由。");
                if (type.RequiresAttachment && attachmentCount == 0)
                    errors.Add("Files", $"「{type.Name}」需附證明文件，請上傳附件。");

                var balance = (await balances.GetBalancesAsync(applicant.Id, f.StartAt.Year, doc.Id == 0 ? null : doc.Id))
                    .FirstOrDefault(b => b.Type.Id == type.Id);
                if (balance?.Remaining is decimal remain && f.Hours > remain)
                    errors.Add("Leave.LeaveTypeId", $"「{type.Name}」餘額不足：剩餘 {LeaveQuota.FormatHours(Math.Max(0, remain))}（含簽核中），本次申請 {LeaveQuota.FormatHours(f.Hours)}。");

                var overlap = (await documents.QueryAsync(d => d.ApplicantId == applicant.Id && d.DocumentType == DocumentType.Leave && d.Id != doc.Id))
                    .FirstOrDefault(d => (d.Status == DocumentStatus.Approved || d.Status.IsActiveFlow()) && d.Content.Leave is { } o &&
                                         o.StartAt < f.EndAt && f.StartAt < o.EndAt);
                if (overlap is not null)
                    errors.Add("Leave.StartAt", $"與 {overlap.DocumentNo}「{overlap.Title}」的請假時間重疊。");
                break;
            }
            case DocumentType.Expense:
            {
                var f = doc.Content.Expense!;
                if (f.Items.Count == 0) errors.Add("Expense.Items", "請至少填寫一筆費用明細。");
                if (string.IsNullOrWhiteSpace(f.Purpose)) errors.Add("Expense.Purpose", "請填寫報銷事由。");
                var cats = (await categories.GetAllAsync()).ToDictionary(c => c.Id);
                var needReceipt = false;
                for (var i = 0; i < f.Items.Count; i++)
                {
                    var it = f.Items[i];
                    var n = $"第 {i + 1} 筆";
                    if (!cats.TryGetValue(it.CategoryId, out var cat)) { errors.Add($"Expense.Items[{i}].CategoryId", $"{n}：請選擇費用類別。"); continue; }
                    if (!cat.IsActive) errors.Add($"Expense.Items[{i}].CategoryId", $"{n}：費用類別「{cat.Name}」已停用。");
                    if (it.Description.Length == 0) errors.Add($"Expense.Items[{i}].Description", $"{n}：請填寫說明。");
                    if (it.Amount <= 0) errors.Add($"Expense.Items[{i}].Amount", $"{n}：金額需大於 0。");
                    if (cat.MaxAmountPerItem is decimal max && it.Amount > max)
                        errors.Add($"Expense.Items[{i}].Amount", $"{n}：「{cat.Name}」單筆上限 {max:N0} 元，目前 {it.Amount:N0} 元。");
                    if (it.Date > today) errors.Add($"Expense.Items[{i}].Date", $"{n}：費用日期不可晚於今天。");
                    if (it.Date < today.AddDays(-ExpenseDeadlineDays))
                        errors.Add($"Expense.Items[{i}].Date", $"{n}：已超過報銷期限（{ExpenseDeadlineDays} 日）。");
                    if (cat.RequiresReceipt)
                    {
                        needReceipt = true;
                        if (string.IsNullOrEmpty(it.ReceiptNo)) errors.Add($"Expense.Items[{i}].ReceiptNo", $"{n}：「{cat.Name}」需填寫發票 / 收據號碼。");
                    }
                }
                if (needReceipt && attachmentCount == 0) errors.Add("Files", "明細中有需附收據的費用，請上傳發票或收據影本。");
                if (f.ProjectCode is { Length: > 0 } pc && !System.Text.RegularExpressions.Regex.IsMatch(pc, "^[A-Z0-9-]{2,20}$"))
                    errors.Add("Expense.ProjectCode", "專案代碼只能包含英數字與 -。");
                break;
            }
            case DocumentType.Purchase:
            {
                var f = doc.Content.Purchase!;
                if (f.Items.Count == 0) errors.Add("Purchase.Items", "請至少填寫一個採購品項。");
                for (var i = 0; i < f.Items.Count; i++)
                {
                    var it = f.Items[i];
                    var n = $"第 {i + 1} 項";
                    if (it.Name.Length == 0) errors.Add($"Purchase.Items[{i}].Name", $"{n}：請填寫品名。");
                    if (it.Quantity <= 0) errors.Add($"Purchase.Items[{i}].Quantity", $"{n}：數量需大於 0。");
                    if (it.UnitPrice <= 0) errors.Add($"Purchase.Items[{i}].UnitPrice", $"{n}：單價需大於 0。");
                }
                if (string.IsNullOrWhiteSpace(f.Purpose)) errors.Add("Purchase.Purpose", "請填寫採購目的。");
                if (f.RequiredDate is null) errors.Add("Purchase.RequiredDate", "請選擇需求日期。");
                else if (f.RequiredDate < today) errors.Add("Purchase.RequiredDate", "需求日期不可早於今天。");
                if (f.SupplierId is int sid && await suppliers.GetByIdAsync(sid) is not { IsActive: true })
                    errors.Add("Purchase.SupplierId", "建議供應商已停用或不存在。");

                var subject = f.BudgetSubjectId == 0 ? null : await budgetSubjects.GetByIdAsync(f.BudgetSubjectId);
                if (subject is null) errors.Add("Purchase.BudgetSubjectId", "請選擇預算科目。");
                else if (!subject.IsActive) errors.Add("Purchase.BudgetSubjectId", $"預算科目「{subject.Name}」已停用。");
                else
                {
                    var committed = (await documents.QueryAsync(d => d.DocumentType == DocumentType.Purchase && d.Id != doc.Id))
                        .Where(d => (d.Status == DocumentStatus.Approved || d.Status.IsActiveFlow()) &&
                                    d.Content.Purchase?.BudgetSubjectId == subject.Id && (d.SubmittedAt ?? d.CreatedAt).Year == subject.Year)
                        .Sum(d => d.Amount ?? 0);
                    var remain = subject.AnnualBudget - committed;
                    if (f.TotalAmount > remain)
                        errors.Add("Purchase.BudgetSubjectId", $"超過預算：「{subject.Name}」{subject.Year} 年度剩餘 {Math.Max(0, remain):N0} 元（含簽核中），本次 {f.TotalAmount:N0} 元。");
                }
                break;
            }
            case DocumentType.Overtime:
            {
                var f = doc.Content.Overtime!;
                if (f.Hours <= 0) { errors.Add("Overtime.StartTime", "請填寫加班起訖時間。"); break; }
                if (f.Date < today.AddDays(-30) || f.Date > today.AddDays(30))
                    errors.Add("Overtime.Date", "加班日期需在前後 30 天內。");
                var maxPerDay = f.IsHoliday ? 12m : 4m;
                if (f.Hours > maxPerDay)
                    errors.Add("Overtime.EndTime", $"{(f.IsHoliday ? "休假日" : "工作日")}加班單日最多 {maxPerDay:0} 小時。");
                if (!f.IsHoliday && f.StartTime < new TimeOnly(18, 0) && f.EndTime > new TimeOnly(9, 0))
                    errors.Add("Overtime.StartTime", "工作日加班需在下班時間（18:00）之後。");
                if (string.IsNullOrWhiteSpace(f.Reason)) errors.Add("Overtime.Reason", "請填寫加班事由。");

                var others = (await documents.QueryAsync(d => d.ApplicantId == applicant.Id && d.DocumentType == DocumentType.Overtime && d.Id != doc.Id))
                    .Where(d => (d.Status == DocumentStatus.Approved || d.Status.IsActiveFlow()) && d.Content.Overtime is not null).ToList();
                var clash = others.FirstOrDefault(d => d.Content.Overtime!.Date == f.Date && d.Content.Overtime.StartTime < f.EndTime && f.StartTime < d.Content.Overtime.EndTime);
                if (clash is not null) errors.Add("Overtime.StartTime", $"與 {clash.DocumentNo} 的加班時段重疊。");
                var monthTotal = others.Where(d => d.Content.Overtime!.Date.Year == f.Date.Year && d.Content.Overtime.Date.Month == f.Date.Month).Sum(d => d.Hours ?? 0);
                if (monthTotal + f.Hours > MonthlyOvertimeCap)
                    errors.Add("Overtime.EndTime", $"{f.Date:yyyy/MM} 加班已 {monthTotal:0.#} 小時，加上本次將超過每月 {MonthlyOvertimeCap:0} 小時上限。");
                break;
            }
        }
    }

    // =====================================================================
    // 工具
    // =====================================================================

    /// <summary>請假時數：起訖之間的工作時數（扣除午休、週末、假日），以 0.5 小時為單位</summary>
    public async Task<decimal> CalculateLeaveHoursAsync(DateTime start, DateTime end)
    {
        var calendar = new WorkCalendar(await holidays.GetAllAsync());
        var h = calendar.WorkingHoursBetween(ToTaipei(start), ToTaipei(end));
        return Math.Round((decimal)h * 2, MidpointRounding.AwayFromZero) / 2;
    }

    private static DateTimeOffset ToTaipei(DateTime local) =>
        new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeSpan.FromHours(8));

    private static bool IsBlank(ExpenseItemInput i) =>
        i.CategoryId == 0 && string.IsNullOrWhiteSpace(i.Description) && (i.Amount ?? 0) == 0 && string.IsNullOrWhiteSpace(i.ReceiptNo);

    private static bool IsBlank(PurchaseItemInput i) =>
        string.IsNullOrWhiteSpace(i.Name) && string.IsNullOrWhiteSpace(i.Specification) && (i.UnitPrice ?? 0) == 0;

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
