// Utils/ApiProblemWriter.cs
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;          // <-- THÊM
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;       // <-- THÊM

namespace HAShop.Api.Utils;

// Tránh đụng Microsoft.AspNetCore.Http.IProblemDetailsWriter
public interface IApiProblemWriter
{
    Task WriteAsync(HttpContext ctx, string code, int status, string? techMessage);
}

public sealed class ApiProblemWriter : IApiProblemWriter
{
    private readonly ILogger<ApiProblemWriter> _log;
    public ApiProblemWriter(ILogger<ApiProblemWriter> log) => _log = log;

    public Task WriteAsync(HttpContext ctx, string code, int status, string? techMessage)
    {
        var lang = ctx.Request.Headers["Accept-Language"].ToString();
        var detail = ErrorCatalog.Friendly(code, fallback: null, locale: lang);

        var pd = new ProblemDetails
        {
            Title = code,
            Detail = detail,
            Status = status,
            Type = "about:blank",
            Instance = ctx.Request.Path.ToString()   // <-- .ToString()
        };

        pd.Extensions["traceId"] = ctx.TraceIdentifier;
        pd.Extensions["code"] = code;

        if (!string.IsNullOrEmpty(techMessage))
            _log.LogDebug("TechMessage {Code}: {Msg}", code, techMessage);

        ctx.Response.ContentType = "application/problem+json";
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(pd);
    }
}
