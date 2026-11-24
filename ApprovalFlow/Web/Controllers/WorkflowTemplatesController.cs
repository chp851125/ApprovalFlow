using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Application.Workflow;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;
using ApprovalFlow.Web.Infrastructure;
using ApprovalFlow.Web.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace ApprovalFlow.Web.Controllers;

/// <summary>簽核流程範本（系統管理員）</summary>
[Authorize(Policy = Policies.Admin)]
public class WorkflowTemplatesController(
    WorkflowTemplateService service,
    IRepository<WorkflowTemplate> templates,
    IRepository<Employee> employees,
    IRepository<Department> departments) : Controller
{
    public async Task<IActionResult> Index()
    {
        var all = await templates.GetAllAsync();
        var usage = await service.UsageByTemplateAsync();
        var groups = Enum.GetValues<DocumentType>()
            .Select(type => (type, (IReadOnlyList<TemplateVersionRow>)all.Where(t => t.DocumentType == type)
                .OrderByDescending(t => t.TemplateVersion)
                .Select(t => new TemplateVersionRow(t, usage.GetValueOrDefault(t.Id), !t.IsActive && usage.GetValueOrDefault(t.Id) == 0))
                .ToList()))
            .ToList();
        return View(new WorkflowTemplateIndexViewModel { Groups = groups, Names = (await employees.GetAllAsync()).ToDictionary(e => e.Id, e => e.Name) });
    }

    public async Task<IActionResult> Details(int id, [FromQuery] WorkflowPreviewInput? preview)
    {
        var t = await templates.GetByIdAsync(id) ?? throw new NotFoundException("流程範本", id);
        var usage = await service.UsageByTemplateAsync();
        var emps = await employees.GetAllAsync();
        var depts = (await departments.GetAllAsync()).ToDictionary(d => d.Id);
        var vm = new WorkflowTemplateDetailsViewModel
        {
            Template = t,
            Usage = usage.GetValueOrDefault(t.Id),
            Editable = !t.IsActive && usage.GetValueOrDefault(t.Id) == 0,
            Names = emps.ToDictionary(e => e.Id, e => e.Name),
            Preview = preview ?? new WorkflowPreviewInput(),
            Applicants = emps.Where(e => e.IsActive)
                .OrderBy(e => depts.GetValueOrDefault(e.DepartmentId)?.SortOrder).ThenBy(e => e.EmployeeNo)
                .Select(e => new SelectListItem($"{e.Name}・{depts.GetValueOrDefault(e.DepartmentId)?.Name}", e.Id.ToString(), e.Id == preview?.ApplicantId)),
            OtherVersions = (await templates.QueryAsync(x => x.DocumentType == t.DocumentType && x.Id != t.Id))
                .OrderByDescending(x => x.TemplateVersion)
                .Select(x => new TemplateVersionRow(x, usage.GetValueOrDefault(x.Id), false)).ToList()
        };
        if (preview?.ApplicantId is int applicantId)
        {
            try { vm.PreviewSteps = await service.PreviewAsync(t, applicantId, preview.Amount, preview.Hours); }
            catch (BusinessException ex) { vm.PreviewError = ex.Message; }
        }
        return View(vm);
    }

    /// <summary>以既有版本為基礎建立新版本</summary>
    [HttpGet]
    public async Task<IActionResult> NewVersion(int id)
    {
        var t = await templates.GetByIdAsync(id) ?? throw new NotFoundException("流程範本", id);
        var input = ToInput(t);
        input.Id = 0;
        input.Version = 0;
        input.SourceId = t.Id;
        input.Description = null;
        return View("Edit", await BuildEditVm(input, null));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(int id)
    {
        var t = await templates.GetByIdAsync(id) ?? throw new NotFoundException("流程範本", id);
        if (!await service.IsEditableAsync(t))
        {
            TempData.SetInfo("此版本已啟用或已有單據使用，已改為以它為基礎建立新版本。");
            return RedirectToAction(nameof(NewVersion), new { id });
        }
        return View(await BuildEditVm(ToInput(t), t.TemplateVersion));
    }

    [HttpPost]
    public async Task<IActionResult> Save(WorkflowTemplateInput input)
    {
        input.Steps ??= [];
        if (ModelState.IsValid)
        {
            try
            {
                var saved = await service.SaveAsync(input);
                TempData.SetSuccess(input.Id == 0
                    ? $"已建立 v{saved.TemplateVersion}（尚未啟用）。確認無誤後請按「啟用此版本」。"
                    : $"已儲存 v{saved.TemplateVersion}。");
                return RedirectToAction(nameof(Details), new { id = saved.Id });
            }
            catch (BusinessException ex) when (ex is not ForbiddenException)
            {
                ModelState.AddBusinessError(ex, null);
            }
        }
        int? tv = input.Id == 0 ? null : (await templates.GetByIdAsync(input.Id))?.TemplateVersion;
        return View("Edit", await BuildEditVm(input, tv));
    }

    [HttpPost]
    public async Task<IActionResult> Activate(int id, int version)
    {
        await service.ActivateAsync(id, version);
        TempData.SetSuccess("已啟用此版本。之後送出的單據會使用此版本；已送出的單據不受影響。");
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost]
    public async Task<IActionResult> Delete(int id, int version)
    {
        await service.DeleteAsync(id, version);
        TempData.SetSuccess("已刪除此版本。");
        return RedirectToAction(nameof(Index));
    }

    private static WorkflowTemplateInput ToInput(WorkflowTemplate t) => new()
    {
        Id = t.Id,
        Version = t.Version,
        DocumentType = t.DocumentType,
        Name = t.Name,
        Description = t.Description,
        Steps = t.Steps.OrderBy(s => s.StepNo).Select(s => new WorkflowStepInput
        {
            Name = s.Name,
            ApproverSource = s.ApproverSource,
            RoleName = s.RoleName,
            UserIds = [.. s.UserIds],
            Levels = s.Levels,
            Mode = s.Mode,
            HasCondition = s.Condition is not null,
            ConditionField = s.Condition?.Field ?? ConditionField.Amount,
            ConditionOperator = s.Condition?.Operator ?? ConditionOperator.GreaterThanOrEqual,
            ConditionValue = s.Condition?.Value ?? 0,
            SlaHours = s.SlaHours,
            AllowWithdraw = s.AllowWithdraw
        }).ToList()
    };

    private async Task<WorkflowTemplateEditViewModel> BuildEditVm(WorkflowTemplateInput input, int? templateVersion)
    {
        var depts = (await departments.GetAllAsync()).ToDictionary(d => d.Id);
        return new WorkflowTemplateEditViewModel
        {
            Input = input,
            TemplateVersion = templateVersion,
            Employees = (await employees.QueryAsync(e => e.IsActive))
                .OrderBy(e => depts.GetValueOrDefault(e.DepartmentId)?.SortOrder).ThenBy(e => e.EmployeeNo)
                .Select(e => new SelectListItem($"{e.Name}・{depts.GetValueOrDefault(e.DepartmentId)?.Name}", e.Id.ToString()))
                .ToList()
        };
    }
}
