using ApprovalFlow.Application.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace ApprovalFlow.Web.Infrastructure;

/// <summary>
/// 將商業例外（含並行衝突、找不到資料、無權限）轉為友善訊息：
/// AJAX 回傳 JSON；一般請求以 TempData 帶訊息並導回原頁。
/// </summary>
public sealed class BusinessExceptionFilter(ITempDataDictionaryFactory tempDataFactory, ILogger<BusinessExceptionFilter> logger)
    : IExceptionFilter
{
    public void OnException(ExceptionContext context)
    {
        if (context.Exception is not BusinessException ex) return;

        var status = ex switch
        {
            ConcurrencyException => StatusCodes.Status409Conflict,
            NotFoundException => StatusCodes.Status404NotFound,
            ForbiddenException => StatusCodes.Status403Forbidden,
            _ => StatusCodes.Status400BadRequest
        };
        logger.LogWarning("商業規則例外 {Type}：{Message}", ex.GetType().Name, ex.Message);

        var http = context.HttpContext;
        if (http.Request.IsAjax())
        {
            context.Result = new JsonResult(new { success = false, message = ex.Message, conflict = ex is ConcurrencyException })
            { StatusCode = status };
        }
        else if (ex is ForbiddenException)
        {
            context.Result = new RedirectToActionResult("AccessDenied", "Account", null);
        }
        else
        {
            var tempData = tempDataFactory.GetTempData(http);
            tempData.SetError(ex.Message);
            var referer = http.Request.Headers.Referer.ToString();
            var target = Uri.TryCreate(referer, UriKind.Absolute, out var uri) && uri.Host == http.Request.Host.Host
                ? uri.PathAndQuery
                : "/";
            context.Result = new LocalRedirectResult(target);
        }
        context.ExceptionHandled = true;
    }
}
