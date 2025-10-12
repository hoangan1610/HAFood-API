using System.Data;
using System.Data.Common; // <-- thêm
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;

public interface IUserService
{
    Task<UserProfileUpdateResponse> UpdateProfileAsync(
        long userId,
        UserProfileUpdateRequest req,
        CancellationToken ct = default);
}
public sealed class UserService(ISqlConnectionFactory dbFactory, ILogger<UserService> logger) : IUserService
{
    public async Task<UserProfileUpdateResponse> UpdateProfileAsync(long userId, UserProfileUpdateRequest req, CancellationToken ct = default)
    {
        using var con = dbFactory.Create();

        // Sửa ở đây:
        await ((DbConnection)con).OpenAsync(ct);

        await con.ExecuteAsync(
            new CommandDefinition(
                "EXEC sys.sp_set_session_context @key=N'user_id', @value=@v",
                new { v = userId },
                commandType: CommandType.Text,
                cancellationToken: ct));

        static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        var p = new DynamicParameters();
        p.Add("@full_name", Norm(req.FullName), DbType.String);
        p.Add("@phone", Norm(req.Phone), DbType.String);
        p.Add("@avatar", Norm(req.Avatar), DbType.String);

        try
        {
            // SP trả 1 dòng: UPDATED | NO_CHANGES
            var status = await con.QuerySingleAsync<string>(new CommandDefinition(
                "dbo.usp_user_update_profile",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

            return string.Equals(status, "UPDATED", StringComparison.OrdinalIgnoreCase)
                ? new UserProfileUpdateResponse(true, null, "Đã cập nhật hồ sơ.")
                : new UserProfileUpdateResponse(true, "NO_CHANGES", "Không có gì để cập nhật.");
        }
        catch (SqlException ex) when (ex.Number == 50401)
        {
            return new UserProfileUpdateResponse(false, "UNAUTHENTICATED_OR_NO_SESSION_USER", "Phiên đăng nhập không hợp lệ hoặc thiếu user_id.");
        }
        catch (SqlException ex) when (ex.Number == 50402)
        {
            return new UserProfileUpdateResponse(false, "USER_INFO_NOT_FOUND", "Không tìm thấy người dùng.");
        }
        catch (SqlException ex) when (ex.Number == 50404)
        {
            return new UserProfileUpdateResponse(false, "PHONE_ALREADY_IN_USE", "Số điện thoại đã được sử dụng.");
        }
        catch (SqlException ex) when (ex.Number == 50403)
        {
            logger.LogError(ex, "Update profile failed for {UserId}", userId);
            return new UserProfileUpdateResponse(false, "USER_UPDATE_PROFILE_FAILED", "Không thể cập nhật hồ sơ. Vui lòng thử lại.");
        }
    }
}
