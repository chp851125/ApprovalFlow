using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Workflow;

/// <summary>
/// 流程範本版本管理。
/// <list type="bullet">
/// <item>已被單據使用或啟用中的版本不可修改，只能「另存新版本」。</item>
/// <item>啟用新版本後，只影響之後送出的單據；已送出單據沿用原版本（重新送出亦同）。</item>
/// </list>
/// </summary>
public sealed class WorkflowTemplateService(
    IRepository<WorkflowTemplate> templates,
    IRepository<Document> documents,
    IRepository<Employee> employees,
    IRepository<Department> departments,
    SettingsService settings,
    AuditService audit,
    ICurrentUser currentUser,
    IClock clock)
{
    public async Task<int> CountUsageAsync(int templateId) =>
        (await documents.QueryAsync(d => d.TemplateId == templateId)).Count;

    public async Task<Dictionary<int, int>> UsageByTemplateAsync() =>
        (await documents.QueryAsync(d => d.TemplateId != null))
        .GroupBy(d => d.TemplateId!.Value).ToDictionary(g => g.Key, g => g.Count());

    /// <summary>可直接修改：未啟用且沒有任何單據使用</summary>
    public async Task<bool> IsEditableAsync(WorkflowTemplate t) => !t.IsActive && await CountUsageAsync(t.Id) == 0;

    public async Task<WorkflowTemplate> SaveAsync(WorkflowTemplateInput input)
    {
        EnsureAdmin();
        var steps = Validate(input, await employees.GetAllAsync());

        if (input.Id == 0)
        {
            var sameType = await templates.QueryAsync(t => t.DocumentType == input.DocumentType);
            var created = await templates.AddAsync(new WorkflowTemplate
            {
                DocumentType = input.DocumentType,
                Name = input.Name.Trim(),
                Description = Clean(input.Description),
                TemplateVersion = sameType.Select(t => t.TemplateVersion).DefaultIfEmpty(0).Max() + 1,
                IsActive = false,
                EffectiveFrom = clock.Today,
                Steps = steps,
                CreatedAt = clock.Now,
                CreatedById = currentUser.Id
            });
            await audit.LogAsync("Create", nameof(WorkflowTemplate), created.Id.ToString(),
                $"建立{created.DocumentType.ToDisplay()}流程 v{created.TemplateVersion}「{created.Name}」（{steps.Count} 關，尚未啟用）");
            return created;
        }

        var existing = await templates.GetByIdAsync(input.Id) ?? throw new NotFoundException("流程範本", input.Id);
        if (!await IsEditableAsync(existing))
            throw new BusinessException("此版本已啟用或已有單據使用，不可直接修改，請另存為新版本。");
        if (existing.DocumentType != input.DocumentType) throw new ValidationException("不可變更流程的單別。");
        existing.Name = input.Name.Trim();
        existing.Description = Clean(input.Description);
        existing.Steps = steps;
        existing.Version = input.Version;
        await templates.UpdateAsync(existing);
        await audit.LogAsync("Update", nameof(WorkflowTemplate), existing.Id.ToString(),
            $"修改{existing.DocumentType.ToDisplay()}流程 v{existing.TemplateVersion}「{existing.Name}」（{steps.Count} 關）");
        return existing;
    }

    /// <summary>啟用指定版本，同單別其他版本自動停用</summary>
    public async Task ActivateAsync(int id, int version)
    {
        EnsureAdmin();
        var target = await templates.GetByIdAsync(id) ?? throw new NotFoundException("流程範本", id);
        if (target.Version != version) throw new ConcurrencyException(nameof(WorkflowTemplate), id);
        if (target.IsActive) return;
        if (target.Steps.Count == 0) throw new BusinessException("流程至少需要一個關卡才能啟用。");

        foreach (var other in await templates.QueryAsync(t => t.DocumentType == target.DocumentType && t.IsActive && t.Id != id))
        {
            other.IsActive = false;
            await templates.UpdateAsync(other);
        }
        target.IsActive = true;
        target.EffectiveFrom = clock.Today;
        await templates.UpdateAsync(target);
        await audit.LogAsync("Activate", nameof(WorkflowTemplate), id.ToString(),
            $"啟用{target.DocumentType.ToDisplay()}流程 v{target.TemplateVersion}「{target.Name}」，之後送出的單據將使用此版本");
    }

    public async Task DeleteAsync(int id, int version)
    {
        EnsureAdmin();
        var t = await templates.GetByIdAsync(id) ?? throw new NotFoundException("流程範本", id);
        if (t.Version != version) throw new ConcurrencyException(nameof(WorkflowTemplate), id);
        if (!await IsEditableAsync(t)) throw new BusinessException("已啟用或已有單據使用的版本不可刪除（保留供歷史單據查詢）。");
        await templates.DeleteAsync(id);
        await audit.LogAsync("Delete", nameof(WorkflowTemplate), id.ToString(),
            $"刪除{t.DocumentType.ToDisplay()}流程 v{t.TemplateVersion}「{t.Name}」");
    }

    /// <summary>流程試算：以指定申請人與金額 / 時數展開關卡（不寫入任何資料）</summary>
    public async Task<List<DocumentStep>> PreviewAsync(WorkflowTemplate template, int applicantId, decimal? amount, decimal? hours)
    {
        var applicant = await employees.GetByIdAsync(applicantId) ?? throw new ValidationException("請選擇申請人。", "ApplicantId");
        var org = new OrgSnapshot(await employees.GetAllAsync(), await departments.GetAllAsync());
        return StepBuilder.Build(template, applicant, amount, hours, org, await settings.GetIntAsync(SettingKeys.SlaDefaultHours, 16));
    }

    /// <summary>把輸入轉成關卡定義並驗證（以關卡序號標示錯誤位置）</summary>
    public static List<WorkflowStepDefinition> Validate(WorkflowTemplateInput input, IReadOnlyList<Employee> allEmployees)
    {
        if (input.Steps.Count == 0) throw new ValidationException("流程至少需要一個關卡。");
        if (input.Steps.Count > 15) throw new ValidationException("流程最多 15 個關卡。");
        var active = allEmployees.Where(e => e.IsActive).Select(e => e.Id).ToHashSet();
        var result = new List<WorkflowStepDefinition>();

        for (var i = 0; i < input.Steps.Count; i++)
        {
            var s = input.Steps[i];
            var label = $"第 {i + 1} 關";
            var name = (s.Name ?? "").Trim();
            if (name.Length == 0) throw new ValidationException($"{label}：請輸入關卡名稱。");
            if (name.Length > 30) throw new ValidationException($"{label}：關卡名稱最多 30 個字。");

            var def = new WorkflowStepDefinition
            {
                StepNo = i + 1,
                Name = name,
                ApproverSource = s.ApproverSource,
                Mode = s.Mode,
                SlaHours = s.SlaHours,
                AllowWithdraw = s.AllowWithdraw,
                Levels = 1
            };
            if (s.SlaHours is < 1 or > 240) throw new ValidationException($"{label}：時效需介於 1~240 個工作小時。");

            switch (s.ApproverSource)
            {
                case ApproverSource.Role:
                    if (string.IsNullOrEmpty(s.RoleName) || !Roles.All.Contains(s.RoleName) || s.RoleName == Roles.Applicant)
                        throw new ValidationException($"{label}：請選擇簽核角色。");
                    def.RoleName = s.RoleName;
                    break;
                case ApproverSource.SpecificUser:
                    var ids = s.UserIds.Distinct().ToList();
                    if (ids.Count == 0) throw new ValidationException($"{label}：請至少選擇一位簽核人員。");
                    if (ids.Any(id => !active.Contains(id))) throw new ValidationException($"{label}：指定人員中有已停用或不存在的帳號。");
                    def.UserIds = ids;
                    break;
                case ApproverSource.ManagerLevelN:
                    if (s.Levels is < 1 or > 5) throw new ValidationException($"{label}：往上層級需介於 1~5。");
                    def.Levels = s.Levels;
                    break;
                case ApproverSource.DirectManager:
                case ApproverSource.DepartmentManager:
                    break;
                default:
                    throw new ValidationException($"{label}：簽核人來源不正確。");
            }

            // 只會解析出一人的來源，會簽 / 或簽沒有意義，統一為單簽
            if (s.ApproverSource is ApproverSource.DirectManager or ApproverSource.DepartmentManager or ApproverSource.ManagerLevelN)
                def.Mode = ApprovalMode.Single;

            if (s.HasCondition)
            {
                if (s.ConditionValue < 0) throw new ValidationException($"{label}：條件值不可為負數。");
                def.Condition = new StepCondition { Field = s.ConditionField, Operator = s.ConditionOperator, Value = s.ConditionValue };
            }
            result.Add(def);
        }

        if (result.All(d => d.Condition is not null))
            throw new ValidationException("至少需有一個不設條件的關卡，避免單據不經任何人簽核即核准。");
        return result;
    }

    private void EnsureAdmin()
    {
        if (!currentUser.IsInRole(Roles.Admin)) throw new ForbiddenException("只有系統管理員可以維護簽核流程。");
    }

    private static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
}
