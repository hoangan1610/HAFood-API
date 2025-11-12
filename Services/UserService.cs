// Services/UserService.cs
using System;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace HAShop.Api.Services
{
    public interface IUserService
    {
        Task<UserProfileUpdateResponse> UpdateProfileAsync(
            long userId,
            UserProfileUpdateRequest req,
            CancellationToken ct = default);
    }

    public sealed class UserService : IUserService
    {
        private readonly ISqlConnectionFactory _dbFactory;
        private readonly ILogger<UserService> _logger;

        public UserService(ISqlConnectionFactory dbFactory, ILogger<UserService> logger)
        {
            _dbFactory = dbFactory;
            _logger = logger;
        }

        public async Task<UserProfileUpdateResponse> UpdateProfileAsync(
            long userId,
            UserProfileUpdateRequest req,
            CancellationToken ct = default)
        {
            if (req is null)
                return new UserProfileUpdateResponse(false, "VALIDATION_FAILED", "Dữ liệu không hợp lệ.");

            using var con = _dbFactory.Create();

            // Mở kết nối (IDbConnection không có OpenAsync, cast sang DbConnection)
            await ((DbConnection)con).OpenAsync(ct);

            // Set session context để SP đọc user_id
            await con.ExecuteAsync(new CommandDefinition(
                "EXEC sys.sp_set_session_context @key=N'user_id', @value=@v",
                new { v = userId },
                commandType: CommandType.Text,
                cancellationToken: ct,
                commandTimeout: 15
            ));

            static string? Norm(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

            var p = new DynamicParameters();
            p.Add("@full_name", Norm(req.FullName), DbType.String);
            p.Add("@phone", Norm(req.Phone), DbType.String);
            p.Add("@avatar", Norm(req.Avatar), DbType.String);

            try
            {
                // SP trả cột đầu 'status' = UPDATED | NO_CHANGES
                var cmd = new CommandDefinition(
                    "dbo.usp_user_update_profile",
                    p,
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct,
                    commandTimeout: 30
                );

                var status = await con.QuerySingleAsync<string>(cmd);

                return string.Equals(status, "UPDATED", StringComparison.OrdinalIgnoreCase)
                    ? new UserProfileUpdateResponse(true, null, "Đã cập nhật hồ sơ.")
                    : new UserProfileUpdateResponse(true, "NO_CHANGES", "Không có gì để cập nhật.");
            }
            // ===== Các lỗi nghiệp vụ map từ SP =====
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
            // ===== Phòng trường hợp ràng buộc UNIQUE ở DB (không qua nhánh kiểm tra trong SP) =====
            catch (SqlException ex) when (ex.Number is 2601 or 2627) // duplicate key / unique constraint
            {
                return new UserProfileUpdateResponse(false, "PHONE_ALREADY_IN_USE", "Số điện thoại đã được sử dụng.");
            }
            // ===== Lỗi hệ thống chung =====
            catch (SqlException ex) when (ex.Number == 50403)
            {
                _logger.LogError(ex, "Update profile failed for {UserId}", userId);
                return new UserProfileUpdateResponse(false, "USER_UPDATE_PROFILE_FAILED", "Không thể cập nhật hồ sơ. Vui lòng thử lại.");
            }
        }
    }
}
