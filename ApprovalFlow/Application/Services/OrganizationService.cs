using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Application.Inputs;
using ApprovalFlow.Domain.Entities;

namespace ApprovalFlow.Application.Services;

/// <summary>組織架構維護：部門、職稱</summary>
public sealed class OrganizationService(
    IRepository<Department> departments,
    IRepository<JobTitle> titles,
    IRepository<Employee> employees,
    AuditService audit)
{
    // ---------------- 部門 ----------------

    public async Task<Department> SaveDepartmentAsync(DepartmentInput input)
    {
        var code = input.Code.Trim().ToUpperInvariant();
        var name = input.Name.Trim();
        var all = await departments.GetAllAsync();

        if (all.Any(d => d.Id != input.Id && d.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException($"部門代碼「{code}」已被使用。", nameof(input.Code));
        if (all.Any(d => d.Id != input.Id && d.IsActive && d.Name == name))
            throw new ValidationException($"部門名稱「{name}」已存在。", nameof(input.Name));

        var existing = input.Id == 0 ? null : all.FirstOrDefault(d => d.Id == input.Id) ?? throw new NotFoundException("部門", input.Id);

        if (input.ParentId is int pid)
        {
            if (pid == input.Id) throw new ValidationException("上層部門不可為自己。", nameof(input.ParentId));
            var parent = all.FirstOrDefault(d => d.Id == pid) ?? throw new ValidationException("上層部門不存在。", nameof(input.ParentId));
            if (!parent.IsActive && existing?.ParentId != pid)
                throw new ValidationException($"上層部門「{parent.Name}」已停用。", nameof(input.ParentId));
            // 防止循環：上層部門不可是自己的下層
            var cursor = parent;
            for (var guard = 0; cursor is not null && guard < 100; guard++)
            {
                if (cursor.Id == input.Id)
                    throw new ValidationException("不可將部門移到自己的下層部門之下。", nameof(input.ParentId));
                cursor = cursor.ParentId is int up ? all.FirstOrDefault(d => d.Id == up) : null;
            }
        }

        if (input.ManagerId is int mid)
        {
            var mgr = await employees.GetByIdAsync(mid) ?? throw new ValidationException("部門主管不存在。", nameof(input.ManagerId));
            if (!mgr.IsActive) throw new ValidationException($"「{mgr.Name}」已停用，不可擔任部門主管。", nameof(input.ManagerId));
        }

        if (existing is null)
        {
            var created = await departments.AddAsync(new Department
            {
                Code = code, Name = name, ParentId = input.ParentId, ManagerId = input.ManagerId,
                SortOrder = input.SortOrder, IsActive = true
            });
            await audit.LogAsync("Create", nameof(Department), created.Id.ToString(), $"新增部門「{name}」",
                AuditService.Diff<Department>(null, created));
            return created;
        }

        var before = (await departments.GetByIdAsync(existing.Id))!;
        existing.Code = code;
        existing.Name = name;
        existing.ParentId = input.ParentId;
        existing.ManagerId = input.ManagerId;
        existing.SortOrder = input.SortOrder;
        existing.Version = input.Version;
        await departments.UpdateAsync(existing);
        await audit.LogAsync("Update", nameof(Department), existing.Id.ToString(), $"修改部門「{name}」",
            AuditService.Diff(before, existing));
        return existing;
    }

    public async Task SetDepartmentActiveAsync(int id, int version, bool active)
    {
        var dept = await departments.GetByIdAsync(id) ?? throw new NotFoundException("部門", id);
        if (dept.IsActive == active) return;

        if (!active)
        {
            var staff = (await employees.QueryAsync(e => e.IsActive && e.DepartmentId == id)).Count;
            if (staff > 0) throw new BusinessException($"「{dept.Name}」尚有 {staff} 位在職員工，請先將員工調至其他部門。");
            var children = (await departments.QueryAsync(d => d.IsActive && d.ParentId == id)).Select(d => d.Name).ToList();
            if (children.Count > 0) throw new BusinessException($"「{dept.Name}」尚有下層部門（{string.Join("、", children)}），請先停用或移動下層部門。");
        }
        else if (dept.ParentId is int pid && await departments.GetByIdAsync(pid) is { IsActive: false } parent)
        {
            throw new BusinessException($"上層部門「{parent.Name}」已停用，請先啟用上層部門。");
        }

        dept.IsActive = active;
        dept.Version = version;
        await departments.UpdateAsync(dept);
        await audit.LogAsync(active ? "Enable" : "Disable", nameof(Department), id.ToString(),
            $"{(active ? "啟用" : "停用")}部門「{dept.Name}」");
    }

    // ---------------- 職稱 ----------------

    public async Task<JobTitle> SaveJobTitleAsync(JobTitleInput input)
    {
        var code = input.Code.Trim().ToUpperInvariant();
        var name = input.Name.Trim();
        var all = await titles.GetAllAsync();
        if (all.Any(t => t.Id != input.Id && t.Code.Equals(code, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException($"職稱代碼「{code}」已被使用。", nameof(input.Code));
        if (all.Any(t => t.Id != input.Id && t.IsActive && t.Name == name))
            throw new ValidationException($"職稱「{name}」已存在。", nameof(input.Name));

        if (input.Id == 0)
        {
            var created = await titles.AddAsync(new JobTitle
            {
                Code = code, Name = name, Rank = input.Rank, SortOrder = input.SortOrder, IsActive = true
            });
            await audit.LogAsync("Create", nameof(JobTitle), created.Id.ToString(), $"新增職稱「{name}」",
                AuditService.Diff<JobTitle>(null, created));
            return created;
        }

        var before = await titles.GetByIdAsync(input.Id) ?? throw new NotFoundException("職稱", input.Id);
        var t = (await titles.GetByIdAsync(input.Id))!;
        t.Code = code;
        t.Name = name;
        t.Rank = input.Rank;
        t.SortOrder = input.SortOrder;
        t.Version = input.Version;
        await titles.UpdateAsync(t);
        await audit.LogAsync("Update", nameof(JobTitle), t.Id.ToString(), $"修改職稱「{name}」", AuditService.Diff(before, t));
        return t;
    }

    public async Task SetJobTitleActiveAsync(int id, int version, bool active)
    {
        var t = await titles.GetByIdAsync(id) ?? throw new NotFoundException("職稱", id);
        if (t.IsActive == active) return;
        if (!active)
        {
            var count = (await employees.QueryAsync(e => e.IsActive && e.JobTitleId == id)).Count;
            if (count > 0) throw new BusinessException($"尚有 {count} 位在職員工使用職稱「{t.Name}」，無法停用。");
        }
        t.IsActive = active;
        t.Version = version;
        await titles.UpdateAsync(t);
        await audit.LogAsync(active ? "Enable" : "Disable", nameof(JobTitle), id.ToString(),
            $"{(active ? "啟用" : "停用")}職稱「{t.Name}」");
    }
}
