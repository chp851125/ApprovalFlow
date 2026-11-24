using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Common;

/// <summary>工作日曆：週末 + 假日表（放假 / 補班）</summary>
public sealed class WorkCalendar(IEnumerable<Holiday> holidays)
{
    private readonly Dictionary<DateOnly, Holiday> _special = holidays
        .GroupBy(h => h.Date).ToDictionary(g => g.Key, g => g.First());

    public const decimal HoursPerDay = 8m;

    public bool IsWorkday(DateOnly date)
    {
        if (_special.TryGetValue(date, out var h))
            return h.Type == HolidayType.MakeupWorkday;
        return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }

    public Holiday? GetSpecial(DateOnly date) => _special.GetValueOrDefault(date);

    public DateOnly NextWorkday(DateOnly date)
    {
        var d = date;
        while (!IsWorkday(d)) d = d.AddDays(1);
        return d;
    }

    /// <summary>從 start 起算第 n 個工作日（start 本身若為工作日算第 1 天）</summary>
    public DateOnly AddWorkdays(DateOnly start, int n)
    {
        var d = NextWorkday(start);
        for (var i = 1; i < n; i++) d = NextWorkday(d.AddDays(1));
        return d;
    }

    public int CountWorkdays(DateOnly from, DateOnly to)
    {
        var count = 0;
        for (var d = from; d <= to; d = d.AddDays(1))
            if (IsWorkday(d)) count++;
        return count;
    }

    public DateOnly PreviousWorkday(DateOnly date)
    {
        var d = date;
        while (!IsWorkday(d)) d = d.AddDays(-1);
        return d;
    }

    /// <summary>上班時段（SLA 與簽核時長皆以此計算工作時數，每日 8 小時）</summary>
    public static readonly (TimeOnly Start, TimeOnly End)[] WorkSegments =
        [(new TimeOnly(9, 0), new TimeOnly(12, 0)), (new TimeOnly(13, 0), new TimeOnly(18, 0))];

    /// <summary>從 start 起加上 N 個工作小時（跳過下班時間、週末與假日）</summary>
    public DateTimeOffset AddWorkingHours(DateTimeOffset start, double hours)
    {
        var offset = start.Offset;
        var t = start.DateTime;
        var remaining = TimeSpan.FromHours(Math.Max(0, hours));
        for (var guard = 0; guard < 3700; guard++)
        {
            var d = DateOnly.FromDateTime(t);
            if (IsWorkday(d))
            {
                foreach (var (s, e) in WorkSegments)
                {
                    var segStart = d.ToDateTime(s);
                    var segEnd = d.ToDateTime(e);
                    if (t >= segEnd) continue;
                    var from = t < segStart ? segStart : t;
                    var avail = segEnd - from;
                    if (remaining <= avail) return new DateTimeOffset(from + remaining, offset);
                    remaining -= avail;
                    t = segEnd;
                }
            }
            t = d.AddDays(1).ToDateTime(TimeOnly.MinValue);
        }
        return new DateTimeOffset(t, offset);
    }

    /// <summary>兩個時間點之間的工作小時數</summary>
    public double WorkingHoursBetween(DateTimeOffset from, DateTimeOffset to)
    {
        if (to <= from) return 0;
        var f = from.DateTime;
        var tt = to.ToOffset(from.Offset).DateTime;
        double total = 0;
        for (var d = DateOnly.FromDateTime(f); d <= DateOnly.FromDateTime(tt); d = d.AddDays(1))
        {
            if (!IsWorkday(d)) continue;
            foreach (var (s, e) in WorkSegments)
            {
                var a = d.ToDateTime(s) > f ? d.ToDateTime(s) : f;
                var b = d.ToDateTime(e) < tt ? d.ToDateTime(e) : tt;
                if (b > a) total += (b - a).TotalHours;
            }
        }
        return total;
    }
}
