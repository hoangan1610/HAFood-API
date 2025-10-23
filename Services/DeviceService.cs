using System.Data;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.AspNetCore.Http;

namespace HAShop.Api.Services
{
    public class DeviceService : IDeviceService
    {
        private readonly ISqlConnectionFactory _db;

        public DeviceService(ISqlConnectionFactory db)
        {
            _db = db;
        }

        public async Task<long?> ResolveDevicePkAsync(Guid? deviceUuid, long? deviceId, long? userInfoId, HttpContext http, CancellationToken ct)
        {
            if (deviceId is > 0) return deviceId;

            if (deviceUuid is { } uuid && uuid != Guid.Empty)
            {
                using var con = _db.Create();

                // 1) tìm device theo UUID
                var existing = await con.QueryFirstOrDefaultAsync<long?>(
                    new CommandDefinition(
                        "SELECT TOP 1 id FROM dbo.tbl_device WHERE device_id = @uuid",
                        new { uuid },
                        cancellationToken: ct));

                if (existing is > 0) return existing;

                // 2) chưa có thì upsert (tạo mới)
                var model = http.Request.Headers.UserAgent.ToString();
                var ip = http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                var p = new DynamicParameters();
                p.Add("@user_info_id", userInfoId);
                p.Add("@device_uuid", uuid);
                p.Add("@device_model", model);
                p.Add("@ip", ip);
                p.Add("@device_pk", dbType: DbType.Int64, direction: ParameterDirection.Output);

                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_device_upsert",
                    p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

                var pk = p.Get<long>("@device_pk");
                return pk > 0 ? pk : null;
            }

            return null;
        }

        // ✅ Implement UpsertDeviceAsync cho endpoint /api/Device/upsert
        public async Task<DeviceUpsertResponse> UpsertDeviceAsync(
     DeviceUpsertRequest req, string? ipFromContext, CancellationToken ct)
        {
            using var con = _db.Create();

            var ip = string.IsNullOrWhiteSpace(req.Ip) ? (ipFromContext ?? "unknown") : req.Ip;

            var p = new DynamicParameters();
            p.Add("@user_info_id", req.UserInfoId);
            p.Add("@device_uuid", req.DeviceUuid);
            p.Add("@device_model", req.DeviceModel);
            p.Add("@ip", ip);
            p.Add("@device_pk", dbType: DbType.Int64, direction: ParameterDirection.Output);

            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_device_upsert",
                p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

            var pk = p.Get<long>("@device_pk");

            // ✅ ĐÚNG named-args & đồng bộ Code
            return new DeviceUpsertResponse(
                Success: pk > 0,
                DevicePk: pk,
                Code: pk > 0 ? null : "DEVICE_UPSERT_FAILED",
                Message: pk > 0 ? null : "Upsert device failed"
            );
        }
    }
}
