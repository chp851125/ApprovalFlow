using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using System.Threading.RateLimiting;
using Microsoft.Extensions.WebEncoders;
using ApprovalFlow.Application.Abstractions;
using ApprovalFlow.Domain.Constants;
using ApprovalFlow.Infrastructure;
using ApprovalFlow.Infrastructure.Persistence;
using ApprovalFlow.Infrastructure.Security;
using ApprovalFlow.Infrastructure.Seeding;
using ApprovalFlow.Infrastructure.Time;
using ApprovalFlow.Web.Infrastructure;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    // ---------------------------------------------------------------
    // 範例資料產生（僅 Development）：dotnet run -- --generate-seed [--anchor 2026-11-23]
    // ---------------------------------------------------------------
    if (args.Contains("--generate-seed"))
    {
        if (!builder.Environment.IsDevelopment())
        {
            Console.Error.WriteLine("範例資料產生腳本僅能在 Development 環境執行。");
            Environment.ExitCode = 1;
            return;
        }
        var anchorIndex = Array.IndexOf(args, "--anchor");
        var anchor = anchorIndex >= 0 && anchorIndex + 1 < args.Length
            ? DateOnly.ParseExact(args[anchorIndex + 1], "yyyy-MM-dd", CultureInfo.InvariantCulture)
            : new TaipeiClock().Today;
        var folder = Path.Combine(builder.Environment.ContentRootPath, "SeedData");
        var generator = new MockDataGenerator(new PasswordService(), anchor);
        generator.WriteTo(folder);
        Console.WriteLine($"已產生範例資料至 {folder}（基準日 {anchor:yyyy-MM-dd}）");
        Console.WriteLine(generator.Summary);
        Console.WriteLine("若要讓執行中的資料套用新範例，請刪除 App_Data/data 後重新啟動。");
        return;
    }

    builder.Host.UseSerilog((ctx, services, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(services)
        .Enrich.FromLogContext());

    // ---------------- 服務註冊 ----------------
    builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.ContentRootPath);
    builder.Services.AddApplicationServices();
    builder.Services.AddHttpContextAccessor();
    builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
    builder.Services.AddScoped<LayoutService>();
    builder.Services.AddScoped<CookieValidator>();

    // 背景排程：簽核時效（提醒 / 逾時 / 升級）與每日待辦摘要
    builder.Services.Configure<BackgroundJobOptions>(builder.Configuration.GetSection(BackgroundJobOptions.Section));
    builder.Services.AddSingleton<BackgroundJobStatus>();
    builder.Services.AddSingleton<BackgroundJobRunner>();
    builder.Services.AddHostedService<WorkflowBackgroundService>();

    builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(o =>
        {
            o.LoginPath = "/Account/Login";
            o.LogoutPath = "/Account/Logout";
            o.AccessDeniedPath = "/Account/AccessDenied";
            o.Cookie.Name = "ApprovalFlow.Auth";
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            // 正式環境一律只透過 HTTPS 傳送；開發環境允許 http://localhost
            o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.SlidingExpiration = true;
            o.EventsType = typeof(CookieValidator);
        });

    builder.Services.AddAuthorizationBuilder()
        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())
        .AddPolicy(Policies.Admin, p => p.RequireRole(Roles.Admin))
        .AddPolicy(Policies.HrOrAdmin, p => p.RequireRole(Roles.HR, Roles.Admin))
        .AddPolicy(Policies.FinanceOrAdmin, p => p.RequireRole(Roles.Finance, Roles.Admin))
        .AddPolicy(Policies.ManagerOrAbove, p => p.RequireRole(Roles.Manager, Roles.DeptManager, Roles.Admin));

    builder.Services.AddAntiforgery(o =>
    {
        o.Cookie.Name = "ApprovalFlow.Csrf";
        o.HeaderName = "X-CSRF-TOKEN";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    });

    // 加密金鑰（登入 Cookie、防偽權杖）保存在 App_Data/keys，重新啟動後使用者不必重新登入
    builder.Services.AddDataProtection()
        .SetApplicationName("ApprovalFlow")
        .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath,
            builder.Configuration["DataProtection:KeysPath"] ?? "App_Data/keys")));

    // 不回傳 Server 標頭（避免透露伺服器版本）
    builder.WebHost.ConfigureKestrel(k => k.AddServerHeader = false);

    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.AddPolicy("login", ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1) }));
        // 全站：每個 IP 每分鐘 600 次請求（防止腳本大量請求）
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx => RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
    });

    builder.Services.AddControllersWithViews(o =>
        {
            o.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
            o.Filters.Add<BusinessExceptionFilter>();
            o.Filters.Add<MustChangePasswordFilter>();

            // 模型繫結錯誤訊息中文化
            var m = o.ModelBindingMessageProvider;
            m.SetValueIsInvalidAccessor(v => $"「{v}」格式不正確");
            m.SetAttemptedValueIsInvalidAccessor((v, f) => $"「{v}」不是有效的{f}");
            m.SetNonPropertyAttemptedValueIsInvalidAccessor(v => $"「{v}」格式不正確");
            m.SetUnknownValueIsInvalidAccessor(f => $"{f}的值不正確");
            m.SetNonPropertyUnknownValueIsInvalidAccessor(() => "輸入的值不正確");
            m.SetValueMustBeANumberAccessor(f => $"{f}必須是數字");
            m.SetNonPropertyValueMustBeANumberAccessor(() => "必須是數字");
            m.SetValueMustNotBeNullAccessor(f => $"請輸入{f}");
            m.SetMissingBindRequiredValueAccessor(f => $"缺少{f}");
            m.SetMissingKeyOrValueAccessor(() => "缺少必要的值");
            m.SetMissingRequestBodyRequiredValueAccessor(() => "缺少請求內容");
        })
        .AddRazorOptions(o =>
        {
            // 分層資料夾：Views 位於 Web/Views
            o.ViewLocationFormats.Clear();
            o.ViewLocationFormats.Add("/Web/Views/{1}/{0}.cshtml");
            o.ViewLocationFormats.Add("/Web/Views/Shared/{0}.cshtml");
        });

    // Razor 輸出中文不轉成 &#x...; 實體（仍會編碼 HTML 特殊字元，XSS 防護不受影響）
    builder.Services.Configure<WebEncoderOptions>(o => o.TextEncoderSettings = new TextEncoderSettings(UnicodeRanges.All));

    builder.Services.AddHealthChecks().AddCheck<DataStoreHealthCheck>("datastore");

    var app = builder.Build();

    // 首次啟動：SeedData → App_Data
    app.Services.GetRequiredService<DataStoreInitializer>().EnsureInitialized();

    // ---------------- 管線 ----------------
    var zhTw = new CultureInfo("zh-TW");
    app.UseRequestLocalization(new RequestLocalizationOptions
    {
        DefaultRequestCulture = new RequestCulture(zhTw),
        SupportedCultures = [zhTw],
        SupportedUICultures = [zhTw]
    });

    app.UseExceptionHandler("/Home/Error");
    if (!app.Environment.IsDevelopment()) app.UseHsts();
    app.UseStatusCodePagesWithReExecute("/Home/StatusCode/{0}");

    app.UseSerilogRequestLogging();
    if (!app.Environment.IsDevelopment()) app.UseHttpsRedirection();
    app.UseSecurityHeaders();
    app.UseRouting();
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    app.MapStaticAssets().AllowAnonymous();
    app.MapHealthChecks("/health").AllowAnonymous();
    app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}")
        .WithStaticAssets();

    app.Run();
}
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "應用程式啟動失敗");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
