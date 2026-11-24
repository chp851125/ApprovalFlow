namespace ApprovalFlow.Application.Common;

/// <summary>假別額度計算規則</summary>
public static class LeaveQuota
{
    /// <summary>
    /// 特別休假日數（勞基法第 38 條，以到職日計算至指定日的年資）：
    /// 6 個月以上未滿 1 年 3 日；1 年 7 日；2 年 10 日；3 年 14 日；5 年 15 日；10 年以上每年加 1 日，最多 30 日。
    /// </summary>
    public static int AnnualLeaveDays(DateOnly hire, DateOnly asOf)
    {
        var months = (asOf.Year - hire.Year) * 12 + asOf.Month - hire.Month;
        var years = months / 12.0;
        if (years < 0.5) return 0;
        if (years < 1) return 3;
        if (years < 2) return 7;
        if (years < 3) return 10;
        if (years < 5) return 14;
        if (years < 10) return 15;
        return Math.Min(30, 15 + (int)years - 9);
    }

    public static string FormatHours(decimal hours) =>
        hours % 8 == 0 && hours > 0 ? $"{hours / 8:0} 天" : $"{hours:0.#} 小時";
}
