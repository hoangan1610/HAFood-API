// Utils/AppException.cs
namespace HAShop.Api.Utils;

public sealed class AppException : Exception
{
    public string Code { get; }

    public AppException(string code, string? message = null, Exception? inner = null)
        : base(message, inner)
    {
        Code = string.IsNullOrWhiteSpace(code) ? "ERROR" : code;
    }
}
