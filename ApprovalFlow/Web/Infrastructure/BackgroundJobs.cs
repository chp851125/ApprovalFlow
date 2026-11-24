using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Notifications;
using ApprovalFlow.Application.Workflow;

namespace ApprovalFlow.Web.Infrastructure;

public sealed class BackgroundJobOptions
{
    public const string Section = "BackgroundJobs";
    public bool Enabled { get; set; } = true;
    /// <summary>檢查間隔（秒）</summary>
    public int IntervalSeconds { get; set; } = 60;
    /// <summary>啟動後延遲多久開始第一次檢查（秒）</summary>
    public int StartupDelaySeconds { get; set; } = 10;
}

/// <summary>排程執行狀態（供管理頁顯示）</summary>
public sealed class BackgroundJobStatus
{
    private readonly object _lock = new();
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? LastRunAt { get; private set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public string? LastSlaResult { get; private set; }
    public string? LastDigestResult { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastErrorAt { get; private set; }
    public int RunCount { get; private set; }
    public bool Enabled { get; set; }
    public int IntervalSeconds { get; set; }

    public void MarkStarted(DateTimeOffset at) { lock (_lock) StartedAt = at; }

    public void Record(DateTimeOffset at, SlaRunResult? sla, DigestRunResult? digest)
    {
        lock (_lock)
        {
            LastRunAt = at;
            RunCount++;
            if (sla is not null) LastSlaResult = sla.ToString();
            if (digest is not null) LastDigestResult = digest.ToString();
        }
    }

    public void RecordError(DateTimeOffset at, string message)
    {
        lock (_lock) { LastError = message; LastErrorAt = at; }
    }
}

/// <summary>執行一次排程工作（背景服務與管理頁「立即執行」共用，確保不會同時執行）</summary>
public sealed class BackgroundJobRunner(IServiceScopeFactory scopes, BackgroundJobStatus status, ILogger<BackgroundJobRunner> logger)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<(SlaRunResult? Sla, DigestRunResult? Digest)> RunAsync(bool runSla, bool runDigest, bool forceDigest, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();
            SlaRunResult? sla = null;
            DigestRunResult? digest = null;
            try
            {
                if (runSla) sla = await scope.ServiceProvider.GetRequiredService<SlaMonitorService>().RunAsync(ct);
                if (runDigest) digest = await scope.ServiceProvider.GetRequiredService<DigestService>().RunAsync(forceDigest, ct);
                status.Record(clock.Now, sla, digest);
                if (sla is { Reminded: > 0 } or { MarkedOverdue: > 0 } or { Escalated: > 0 } || digest is { Sent: > 0 })
                    logger.LogInformation("排程執行：{Sla}；每日摘要：{Digest}", sla, digest);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status.RecordError(clock.Now, ex.Message);
                logger.LogError(ex, "排程執行失敗");
            }
            return (sla, digest);
        }
        finally
        {
            Gate.Release();
        }
    }
}

/// <summary>背景排程：定期檢查簽核時效（提醒、逾時、升級）並寄送每日待辦摘要</summary>
public sealed class WorkflowBackgroundService(
    BackgroundJobRunner runner,
    BackgroundJobStatus status,
    Microsoft.Extensions.Options.IOptions<BackgroundJobOptions> options,
    IClock clock,
    ILogger<WorkflowBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        status.Enabled = o.Enabled;
        status.IntervalSeconds = Math.Max(10, o.IntervalSeconds);
        if (!o.Enabled)
        {
            logger.LogInformation("背景排程已停用（BackgroundJobs:Enabled = false）");
            return;
        }
        status.MarkStarted(clock.Now);
        logger.LogInformation("背景排程啟動，每 {Seconds} 秒檢查一次", status.IntervalSeconds);

        try
        {
            status.NextRunAt = clock.Now.AddSeconds(o.StartupDelaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(0, o.StartupDelaySeconds)), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(status.IntervalSeconds));
            do
            {
                await runner.RunAsync(runSla: true, runDigest: true, forceDigest: false, stoppingToken);
                status.NextRunAt = clock.Now.AddSeconds(status.IntervalSeconds);
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // 應用程式關閉
        }
    }
}
