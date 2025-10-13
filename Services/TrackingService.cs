using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using System.Data;

namespace HAShop.Api.Services;

public interface ITrackingService
{
    Task<long> LogAsync(TrackEventRequest req, CancellationToken ct);
    Task<IReadOnlyList<TopProductViewDto>> TopProductsAsync(DateTime? from, DateTime? to, int top, CancellationToken ct);
    Task<IReadOnlyList<TopCategoryViewDto>> TopCategoriesAsync(DateTime? from, DateTime? to, int top, CancellationToken ct);
    Task<IReadOnlyList<RecentEventDto>> RecentByDeviceAsync(long deviceId, int limit, CancellationToken ct);
}

public class TrackingService(ISqlConnectionFactory db) : ITrackingService
{
    public async Task<long> LogAsync(TrackEventRequest req, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@device_id", req.Device_Id);
        p.Add("@product_id", req.Product_Id);
        p.Add("@variant_id", req.Variant_Id);
        p.Add("@category_id", req.Category_Id);
        p.Add("@ip", req.Ip);
        p.Add("@user_agent", req.User_Agent);
        p.Add("@device_type", req.Device_Type);
        p.Add("@data", req.Data);
        p.Add("@id", dbType: DbType.Int64, direction: ParameterDirection.Output);

        await con.ExecuteAsync(new CommandDefinition(
            "dbo.usp_tracking_log", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

        return p.Get<long>("@id");
    }

    public async Task<IReadOnlyList<TopProductViewDto>> TopProductsAsync(DateTime? from, DateTime? to, int top, CancellationToken ct)
    {
        using var con = db.Create();
        var list = await con.QueryAsync<TopProductViewDto>(new CommandDefinition(
            "dbo.usp_tracking_top_products", new { from, to, top }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return list.AsList();
    }

    public async Task<IReadOnlyList<TopCategoryViewDto>> TopCategoriesAsync(DateTime? from, DateTime? to, int top, CancellationToken ct)
    {
        using var con = db.Create();
        var list = await con.QueryAsync<TopCategoryViewDto>(new CommandDefinition(
            "dbo.usp_tracking_top_categories", new { from, to, top }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return list.AsList();
    }

    public async Task<IReadOnlyList<RecentEventDto>> RecentByDeviceAsync(long deviceId, int limit, CancellationToken ct)
    {
        using var con = db.Create();
        var list = await con.QueryAsync<RecentEventDto>(new CommandDefinition(
            "dbo.usp_tracking_recent_by_device", new { device_id = deviceId, limit }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return list.AsList();
    }
}
