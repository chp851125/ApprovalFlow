using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Documents;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Domain.Entities;

namespace ApprovalFlow.Web.Infrastructure;

public sealed record LayoutUser(int Id, string Name, string Account, string DepartmentName, string TitleName, IReadOnlyList<string> Roles)
{
    public string Initial => string.IsNullOrEmpty(Name) ? "?" : Name[..1];
    public string RoleText => string.Join("、", Roles.Where(r => r != Domain.Constants.Roles.Applicant).Select(Domain.Constants.Roles.ToDisplay)) is { Length: > 0 } s ? s : "一般同仁";
}

/// <summary>版型所需資料（每個請求只查一次）</summary>
public sealed class LayoutService(
    ICurrentUser currentUser,
    IRepository<Employee> employees,
    IRepository<Department> departments,
    IRepository<JobTitle> titles,
    SettingsService settings,
    DocumentQueryService documentQueries)
{
    private LayoutUser? _user;
    private bool _loaded;
    private string? _company;
    private BoxCounts? _counts;

    /// <summary>待辦 / 草稿數（側欄與底部導覽的紅點）</summary>
    public async Task<BoxCounts> GetCountsAsync() =>
        _counts ??= currentUser.Id is null ? new BoxCounts(0, 0, 0) : await documentQueries.CountsAsync();

    public async Task<LayoutUser?> GetUserAsync()
    {
        if (_loaded) return _user;
        _loaded = true;
        if (currentUser.Id is not int id) return null;
        var e = await employees.GetByIdAsync(id);
        if (e is null) return null;
        var d = await departments.GetByIdAsync(e.DepartmentId);
        var t = await titles.GetByIdAsync(e.JobTitleId);
        _user = new LayoutUser(e.Id, e.Name, e.Account, d?.Name ?? "", t?.Name ?? "", e.Roles);
        return _user;
    }

    public async Task<string> GetCompanyNameAsync() =>
        _company ??= await settings.GetStringAsync(SettingKeys.CompanyName, "簽核系統");
}
