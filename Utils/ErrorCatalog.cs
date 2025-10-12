// Utils/ErrorCatalog.cs
namespace HAShop.Api.Utils;
public static class ErrorCatalog
{
    // Có thể thay bằng IStringLocalizer nếu muốn đa ngôn ngữ runtime
    private static readonly Dictionary<string, string> Vi = new(StringComparer.OrdinalIgnoreCase)
    {
        ["UNAUTHENTICATED_OR_NO_SESSION_USER"] = "Bạn cần đăng nhập để tiếp tục.",
        ["USER_INFO_NOT_FOUND"] = "Không tìm thấy hồ sơ người dùng.",
        ["PHONE_ALREADY_IN_USE"] = "Số điện thoại này đã được dùng. Vui lòng chọn số khác.",
        ["USER_UPDATE_PROFILE_FAILED"] = "Cập nhật hồ sơ không thành công. Vui lòng thử lại.",
        ["TOKEN_INVALID_OR_NO_USERID"] = "Thiếu hoặc sai thông tin người dùng trong token.",
        ["ERROR"] = "Đã xảy ra lỗi, vui lòng thử lại."
    };

    public static string Friendly(string? code, string? fallback = null, string locale = "vi")
    {
        // Sau này có thể switch(locale) để trả bản EN, v.v.
        if (!string.IsNullOrWhiteSpace(code) && Vi.TryGetValue(code!, out var msg))
            return msg;

        return fallback is { Length: > 0 } ? fallback! : Vi["ERROR"];
    }
}
