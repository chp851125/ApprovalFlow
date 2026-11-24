using ApprovalFlow.Application.Abstractions;

namespace ApprovalFlow.Infrastructure.Time;

/// <summary>台北時區時鐘（Asia/Taipei，UTC+8，無日光節約）</summary>
public sealed class TaipeiClock : IClock
{
    public static readonly TimeZoneInfo Zone = FindZone();

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Asia/Taipei", "Taipei Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Asia/Taipei", TimeSpan.FromHours(8), "Asia/Taipei", "台北標準時間");
    }

    public DateTimeOffset Now => TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Zone);
    public DateOnly Today => DateOnly.FromDateTime(Now.DateTime);
    public DateTimeOffset ToLocal(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Zone);
}
