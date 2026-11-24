using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Documents;
using ApprovalFlow.Application.Notifications;
using ApprovalFlow.Application.Reports;
using ApprovalFlow.Application.Services;
using ApprovalFlow.Application.Workflow;
using ApprovalFlow.Domain.Common;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Infrastructure.Email;
using ApprovalFlow.Infrastructure.Persistence;
using ApprovalFlow.Infrastructure.Reports;
using ApprovalFlow.Infrastructure.Security;
using ApprovalFlow.Infrastructure.Storage;
using ApprovalFlow.Infrastructure.Time;

namespace ApprovalFlow.Infrastructure;

public static class DependencyInjection
{
    /// <summary>所有以 JSON 儲存的實體（每個實體一個檔案）</summary>
    public static readonly Type[] EntityTypes =
    [
        typeof(Department), typeof(JobTitle), typeof(Employee), typeof(Delegation),
        typeof(LeaveType), typeof(LeaveBalance), typeof(ExpenseCategory), typeof(BudgetSubject),
        typeof(Supplier), typeof(Holiday), typeof(SystemSetting), typeof(DocumentTypeConfig),
        typeof(DocumentSequence), typeof(WorkflowTemplate), typeof(Document), typeof(Attachment),
        typeof(Notification), typeof(EmailMessage), typeof(AuditLog)
    ];

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config, string contentRoot)
    {
        services.AddOptions<DataStoreOptions>()
            .Bind(config.GetSection(DataStoreOptions.Section))
            .PostConfigure(o => o.Resolve(contentRoot));

        services.AddSingleton<DataStoreInitializer>();
        services.AddSingleton<IDataStoreAdmin, JsonDataStoreAdmin>();
        services.AddSingleton<IClock, TaipeiClock>();
        services.AddSingleton<IPasswordService, PasswordService>();
        services.AddSingleton<IFileStorage, LocalFileStorage>();
        services.AddSingleton<IEmailOutbox, EmlFileOutbox>();
        services.AddSingleton<IExcelExporter, ClosedXmlExporter>();

        // 換成資料庫時，只需把這裡改註冊為 DbRepository<T>
        foreach (var t in EntityTypes)
        {
            var repoType = typeof(JsonFileRepository<>).MakeGenericType(t);
            var iface = typeof(IRepository<>).MakeGenericType(t);
            services.AddSingleton(repoType);
            services.AddSingleton(iface, sp => sp.GetRequiredService(repoType));
            services.AddSingleton(typeof(IReloadableStore), sp => sp.GetRequiredService(repoType));
        }
        return services;
    }

    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        services.AddScoped<SettingsService>();
        services.AddScoped<AuditService>();
        services.AddScoped<AuthService>();
        services.AddScoped<DocumentNumberService>();
        services.AddScoped<OrganizationService>();
        services.AddScoped<EmployeeService>();
        services.AddScoped<DelegationService>();
        services.AddScoped<MasterDataService>();
        services.AddScoped<WorkflowEngine>();
        services.AddScoped<WorkflowTemplateService>();
        services.AddScoped<IWorkflowEventSink, WorkflowNotificationSink>();
        services.AddScoped<LeaveBalanceService>();
        services.AddScoped<AttachmentService>();
        services.AddScoped<DocumentFormService>();
        services.AddScoped<DocumentQueryService>();
        services.AddScoped<NotificationService>();
        services.AddScoped<EmailService>();
        services.AddScoped<IWorkflowEventSink, WorkflowEmailSink>();
        services.AddScoped<SlaMonitorService>();
        services.AddScoped<DigestService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<ReportService>();
        services.AddScoped<AuditQueryService>();
        return services;
    }
}
