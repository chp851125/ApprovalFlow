using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Workflow;

/// <summary>依流程範本把單據展開為流程實例關卡（純函式）</summary>
public static class StepBuilder
{
    public static List<DocumentStep> Build(
        WorkflowTemplate template, Employee applicant, decimal? amount, decimal? hours,
        OrgSnapshot org, int defaultSlaHours)
    {
        var steps = new List<DocumentStep>();
        HashSet<int>? previousApprovers = null;

        foreach (var def in template.Steps.OrderBy(s => s.StepNo))
        {
            var step = new DocumentStep
            {
                TemplateStepNo = def.StepNo,
                Name = def.Name,
                Mode = def.Mode,
                SlaHours = def.SlaHours ?? defaultSlaHours
            };

            if (def.Condition != null && !def.Condition.IsSatisfied(amount, hours))
            {
                step.Status = StepStatus.Skipped;
                step.SkipReason = $"條件不符（{def.Condition.Describe()}）";
            }
            else
            {
                var ids = ApproverResolver.Resolve(def, applicant, org);
                if (ids.Count == 0)
                {
                    step.Status = StepStatus.Skipped;
                    step.SkipReason = "無適用簽核人";
                }
                else if (previousApprovers != null && def.Mode != ApprovalMode.All &&
                         ids.Count == 1 && previousApprovers.SetEquals(ids))
                {
                    step.Status = StepStatus.Skipped;
                    step.SkipReason = "與前一關簽核人相同，自動略過";
                }
                else
                {
                    // 單簽卻解析出多人（例如角色）時視為或簽
                    if (def.Mode == ApprovalMode.Single && ids.Count > 1) step.Mode = ApprovalMode.Any;
                    step.Assignees = ids.Select(id => new StepAssignee { EmployeeId = id }).ToList();
                    previousApprovers = ids.ToHashSet();
                }
            }
            steps.Add(step);
        }

        Renumber(steps);
        return steps;
    }

    public static void Renumber(List<DocumentStep> steps)
    {
        for (var i = 0; i < steps.Count; i++) steps[i].StepNo = i + 1;
    }
}
