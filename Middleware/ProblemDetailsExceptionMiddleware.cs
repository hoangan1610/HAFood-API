// Middleware/ProblemDetailsExceptionMiddleware.cs
using HAShop.Api.Utils;
using Microsoft.Data.SqlClient;

namespace HAShop.Api.Middleware;

public sealed class ProblemDetailsExceptionMiddleware : IMiddleware
{
    private readonly ILogger<ProblemDetailsExceptionMiddleware> _log;
    private readonly IApiProblemWriter _writer;

    public ProblemDetailsExceptionMiddleware(
        ILogger<ProblemDetailsExceptionMiddleware> log,
        IApiProblemWriter writer)
    {
        _log = log;
        _writer = writer;
    }

    public async Task InvokeAsync(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context);
        }
        catch (AppException ex)
        {
            await _writer.WriteAsync(context, ex.Code, StatusFromCode(ex.Code), ex.Message);
        }
        catch (SqlException ex)
        {
            var code = ex.Number switch
            {
                50401 => "UNAUTHENTICATED_OR_NO_SESSION_USER",
                50402 => "USER_INFO_NOT_FOUND",
                50403 => "USER_UPDATE_PROFILE_FAILED",
                50404 => "PHONE_ALREADY_IN_USE",
                _ => "ERROR"
            };
            _log.LogWarning(ex, "SQL error mapped to code {Code}", code);
            await _writer.WriteAsync(context, code, StatusFromCode(code), ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            await _writer.WriteAsync(context, "UNAUTHENTICATED", StatusCodes.Status401Unauthorized, ex.Message);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            await _writer.WriteAsync(context, "CLIENT_CANCELLED", 499, null);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unhandled exception");
            await _writer.WriteAsync(context, "ERROR", StatusCodes.Status500InternalServerError, ex.Message);
        }
    }

    private static int StatusFromCode(string code) => code switch
    {
        "UNAUTHENTICATED" => StatusCodes.Status401Unauthorized,
        "UNAUTHENTICATED_OR_NO_SESSION_USER" => StatusCodes.Status401Unauthorized,
        "USER_INFO_NOT_FOUND" => StatusCodes.Status404NotFound,
        "PHONE_ALREADY_IN_USE" => StatusCodes.Status409Conflict,
        "USER_UPDATE_PROFILE_FAILED" => StatusCodes.Status500InternalServerError,
        "VALIDATION_FAILED" => StatusCodes.Status400BadRequest,
        "CART_NOT_FOUND" => StatusCodes.Status404NotFound,
        "CART_EMPTY" => StatusCodes.Status409Conflict,
        "OUT_OF_STOCK" => StatusCodes.Status409Conflict,
        "ORDER_NOT_FOUND" => StatusCodes.Status404NotFound,
        "ORDER_ALREADY_PAID" => StatusCodes.Status409Conflict,
        "ZALOPAY_BUILD_FAILED" => StatusCodes.Status502BadGateway,
        "VNPAY_BUILD_FAILED" => StatusCodes.Status502BadGateway,
        "PAYLINK_CREATE_FAILED" => StatusCodes.Status502BadGateway,
        "UNSUPPORTED_METHOD" => StatusCodes.Status400BadRequest,


        _ => StatusCodes.Status500InternalServerError
    };
}
