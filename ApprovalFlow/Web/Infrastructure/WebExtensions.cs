using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace ApprovalFlow.Web.Infrastructure;

public static class WebExtensions
{
    public const string TempSuccess = "Flash.Success";
    public const string TempError = "Flash.Error";
    public const string TempInfo = "Flash.Info";

    public static void SetSuccess(this ITempDataDictionary t, string message) => t[TempSuccess] = message;
    public static void SetError(this ITempDataDictionary t, string message) => t[TempError] = message;
    public static void SetInfo(this ITempDataDictionary t, string message) => t[TempInfo] = message;

    public static bool IsAjax(this HttpRequest request) =>
        request.Headers.XRequestedWith == "XMLHttpRequest" ||
        request.Headers.Accept.ToString().Contains("application/json", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Content-Security-Policy：所有腳本、樣式、字型都來自本站（wwwroot/lib，不使用 CDN），
    /// 頁面沒有 inline script / style / 事件屬性，因此可以不開 'unsafe-inline'。
    /// img-src 允許 data:，因為 Bootstrap 的表單元件以 data URI 的 SVG 繪製圖示。
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; " +
        "connect-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    /// <summary>安全標頭；已登入頁面不允許瀏覽器或代理快取（避免共用電腦按上一頁看到資料）</summary>
    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var h = ctx.Response.Headers;
            h.XContentTypeOptions = "nosniff";
            h.XFrameOptions = "DENY";
            h.ContentSecurityPolicy = ContentSecurityPolicy;
            h["Referrer-Policy"] = "strict-origin-when-cross-origin";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            h["Cross-Origin-Opener-Policy"] = "same-origin";
            h["Cross-Origin-Resource-Policy"] = "same-origin";
            h["X-Permitted-Cross-Domain-Policies"] = "none";
            h["X-Robots-Tag"] = "noindex, nofollow";
            ctx.Response.OnStarting(() =>
            {
                if (ctx.User.Identity?.IsAuthenticated == true && !ctx.Response.Headers.ContainsKey("Cache-Control"))
                {
                    ctx.Response.Headers.CacheControl = "no-store, no-cache";
                    ctx.Response.Headers.Pragma = "no-cache";
                }
                return Task.CompletedTask;
            });
            await next();
        });
}
