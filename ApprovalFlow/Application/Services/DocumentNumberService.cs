using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Application.Exceptions;
using ApprovalFlow.Domain.Entities;
using ApprovalFlow.Domain.Enums;

namespace ApprovalFlow.Application.Services;

/// <summary>
/// 單號產生器（例 LV-202611-0001）。
/// 以行程內鎖序列化取號，並以 Version 樂觀並行再保護一層；
/// 日後改用資料庫時，可在 Repository 實作中改用交易或 Sequence。
/// </summary>
public sealed class DocumentNumberService(
    IRepository<DocumentSequence> sequences,
    IRepository<DocumentTypeConfig> typeConfigs)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public async Task<string> NextAsync(DocumentType type, DateTimeOffset at, CancellationToken ct = default)
    {
        var cfg = (await typeConfigs.QueryAsync(c => c.DocumentType == type, ct)).FirstOrDefault()
                  ?? throw new BusinessException($"尚未設定單別「{type.ToDisplay()}」的單號規則。");

        var period = string.Equals(cfg.ResetPeriod, "Yearly", StringComparison.OrdinalIgnoreCase)
            ? at.ToString("yyyy")
            : at.ToString("yyyyMM");

        await Gate.WaitAsync(ct);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    var seq = (await sequences.QueryAsync(s => s.Prefix == cfg.Prefix && s.Period == period, ct)).FirstOrDefault();
                    int number;
                    if (seq is null)
                    {
                        number = 1;
                        await sequences.AddAsync(new DocumentSequence { Prefix = cfg.Prefix, Period = period, LastNumber = 1 }, ct);
                    }
                    else
                    {
                        number = seq.LastNumber + 1;
                        seq.LastNumber = number;
                        await sequences.UpdateAsync(seq, ct);
                    }
                    return Format(cfg, at, number);
                }
                catch (ConcurrencyException) when (attempt < 5)
                {
                    // 其他程序已取號，重新讀取再試
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    public static string Format(DocumentTypeConfig cfg, DateTimeOffset at, int number)
    {
        var s = cfg.NumberFormat
            .Replace("{prefix}", cfg.Prefix)
            .Replace("{yyyyMM}", at.ToString("yyyyMM"))
            .Replace("{yyyy}", at.ToString("yyyy"))
            .Replace("{MM}", at.ToString("MM"));
        // {seq:0000}
        var start = s.IndexOf("{seq", StringComparison.Ordinal);
        if (start >= 0)
        {
            var end = s.IndexOf('}', start);
            var token = s[start..(end + 1)];
            var fmt = token.Contains(':') ? token[(token.IndexOf(':') + 1)..^1] : "0000";
            s = s.Replace(token, number.ToString(fmt));
        }
        return s;
    }
}
