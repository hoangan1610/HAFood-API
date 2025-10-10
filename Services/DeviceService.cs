using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;
using System.Data;

namespace HAShop.Api.Services;

public interface IDeviceService
{
    Task<DeviceUpsertResponse> UpsertDeviceAsync(DeviceUpsertRequest req, CancellationToken ct = default);
}


public class DeviceService(ISqlConnectionFactory dbFactory, ILogger<DeviceService> logger) : IDeviceService
{
    public async Task<DeviceUpsertResponse> UpsertDeviceAsync(DeviceUpsertRequest req, CancellationToken ct = default)
    {
        using var con = dbFactory.Create();

        var p = new DynamicParameters();
        p.Add("@user_info_id", req.UserInfoId, DbType.Int64);
        p.Add("@device_uuid", req.DeviceUuid, DbType.Guid);
        p.Add("@device_model", req.DeviceModel, DbType.String);
        p.Add("@ip", req.Ip, DbType.String);
        p.Add("@device_pk", dbType: DbType.Int64, direction: ParameterDirection.Output);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_device_upsert",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 50001)
        {
            logger.LogWarning(ex, "DEVICE_UPSERT_FAILED: {Message}", ex.Message);
            return new DeviceUpsertResponse(false, 0, "DEVICE_UPSERT_FAILED", ex.Message);
        }

        var devicePk = p.Get<long>("@device_pk");
        return new DeviceUpsertResponse(true, devicePk);
    }
}
