using System.Data;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;

namespace HAShop.Api.Services;

public interface IFlashSaleService
{
    Task<FlashSalePriceDto?> GetVariantPriceAsync(long variantId, byte? channel, CancellationToken ct);
    Task<IReadOnlyList<FlashSaleActiveItemDto>> GetActiveAsync(byte? channel, CancellationToken ct);
    Task<FlashSaleReserveResponse> ReserveAsync(long vpoId, int qty, CancellationToken ct);
    Task<FlashSaleReleaseResponse> ReleaseAsync(long vpoId, int qty, CancellationToken ct);
}

public sealed class FlashSaleService(ISqlConnectionFactory db) : IFlashSaleService
{
    public async Task<FlashSalePriceDto?> GetVariantPriceAsync(long variantId, byte? channel, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@variant_id", variantId);
        p.Add("@channel", channel);

        // Cột trả về khớp SP usp_flashsale_get_price
        var row = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
            "dbo.usp_flashsale_get_price", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

        if (row is null) return null;

        return new FlashSalePriceDto(
            Server_Now: (DateTime)row.server_now,
            Variant_Id: (long)row.variant_id,
            Base_Price: (decimal)row.base_price,
            Campaign_Price: (decimal?)row.campaign_price,
            Effective_Price: (decimal)row.effective_price,
            Vpo_Id: (long?)row.vpo_id,
            End_At: (DateTime?)row.end_at,
            Sold_Count: (int)(row.sold_count ?? 0),
            Qty_Cap_Total: (int?)row.qty_cap_total
        );
    }

    public async Task<IReadOnlyList<FlashSaleActiveItemDto>> GetActiveAsync(byte? channel, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@channel", channel);

        var list = await con.QueryAsync<FlashSaleActiveItemDto>(new CommandDefinition(
            "dbo.usp_flashsale_active", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return list.AsList();
    }

    public async Task<FlashSaleReserveResponse> ReserveAsync(long vpoId, int qty, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@vpo_id", vpoId);
        p.Add("@qty", qty);

        try
        {
            var row = await con.QueryFirstAsync(new CommandDefinition(
                "dbo.usp_flashsale_reserve", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

            return new FlashSaleReserveResponse(
                Vpo_Id: (long)row.vpo_id,
                Sold_Count: (int)row.sold_count,
                Qty_Cap_Total: (int?)row.qty_cap_total,
                Remaining: (int?)row.remaining
            );
        }
        catch (SqlException ex) when (ex.Number is 50001 or 50000)
        {
            // 50001 = Sold out / out of window; 50000 = invalid qty
            throw new InvalidOperationException(ex.Message);
        }
    }

    public async Task<FlashSaleReleaseResponse> ReleaseAsync(long vpoId, int qty, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@vpo_id", vpoId);
        p.Add("@qty", qty);

        try
        {
            var row = await con.QueryFirstAsync(new CommandDefinition(
                "dbo.usp_flashsale_release", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

            return new FlashSaleReleaseResponse(
                Vpo_Id: (long)row.vpo_id,
                Sold_Count: (int)row.sold_count,
                Qty_Cap_Total: (int?)row.qty_cap_total,
                Remaining: (int?)row.remaining
            );
        }
        catch (SqlException ex) when (ex.Number is 50001 or 50000)
        {
            // 50011 = VPO not found; 50010 = invalid qty
            throw new InvalidOperationException(ex.Message);
        }
    }
}
