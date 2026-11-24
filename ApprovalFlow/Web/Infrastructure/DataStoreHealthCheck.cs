using ApprovalFlow.Infrastructure.Persistence;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ApprovalFlow.Web.Infrastructure;

/// <summary>檢查資料夾存在且可寫入</summary>
public sealed class DataStoreHealthCheck(IOptions<DataStoreOptions> options) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var o = options.Value;
        try
        {
            foreach (var dir in new[] { o.DataFullPath, o.UploadFullPath, o.OutboxFullPath })
            {
                if (!Directory.Exists(dir))
                    return Task.FromResult(HealthCheckResult.Unhealthy($"資料夾不存在：{Path.GetFileName(dir)}"));
            }
            var probe = Path.Combine(o.DataFullPath, $".health-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            var files = Directory.GetFiles(o.DataFullPath, "*.json").Length;
            return Task.FromResult(HealthCheckResult.Healthy($"資料檔 {files} 個，可寫入",
                new Dictionary<string, object> { ["dataFiles"] = files }));
        }
        catch (Exception ex)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("資料夾無法寫入", ex));
        }
    }
}
