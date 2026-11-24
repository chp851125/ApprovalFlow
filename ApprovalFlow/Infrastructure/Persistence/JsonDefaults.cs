using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace ApprovalFlow.Infrastructure.Persistence;

/// <summary>JSON 檔案格式：camelCase、縮排、列舉以字串表示、中文不跳脫，方便人工閱讀與修改。</summary>
public static class JsonDefaults
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.MakeReadOnly(populateMissingResolver: true);
        return o;
    }

    /// <summary>實體型別對應的檔名（camelCase 複數），例 Employee → employees.json</summary>
    public static string FileNameFor(Type t)
    {
        var name = t.Name;
        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        if (camel.EndsWith('y') && camel.Length > 1 && !"aeiou".Contains(camel[^2])) camel = camel[..^1] + "ies";
        else if (camel.EndsWith('s')) camel += "es";
        else camel += "s";
        return camel + ".json";
    }
}
