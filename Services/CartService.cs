using System.Data;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;

namespace HAShop.Api.Services;

public interface ICartService
{
    Task<long> GetOrCreateCartIdAsync(long? userInfoId, long? deviceId, CancellationToken ct);
    Task<CartViewDto> ViewAsync(long cartId, CancellationToken ct);
    Task<CartViewDto> GetOrCreateAndViewAsync(long? userInfoId, long? deviceId, CancellationToken ct);

    Task AddOrIncrementAsync(long cartId, long variantId, int quantity,
                             string? nameVariant, decimal? priceVariant, string? imageVariant,
                             CancellationToken ct);
    Task UpdateQuantityAsync(long cartId, long variantId, int quantity, CancellationToken ct);
    Task RemoveItemAsync(long cartId, long variantId, CancellationToken ct);
    Task ClearAsync(long cartId, CancellationToken ct);
}

public class CartService(ISqlConnectionFactory db) : ICartService
{
    public async Task<long> GetOrCreateCartIdAsync(long? userInfoId, long? deviceId, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@user_info_id", userInfoId);
        p.Add("@device_id", deviceId);
        p.Add("@cart_id", dbType: DbType.Int64, direction: ParameterDirection.Output);

        await con.ExecuteAsync(new CommandDefinition("dbo.usp_cart_get_or_create", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return p.Get<long>("@cart_id");
    }

    public async Task<CartViewDto> ViewAsync(long cartId, CancellationToken ct)
    {
        using var con = db.Create();
        using var multi = await con.QueryMultipleAsync(new CommandDefinition(
            "dbo.usp_cart_view", new { cart_id = cartId }, commandType: CommandType.StoredProcedure, cancellationToken: ct));

        var header = await multi.ReadFirstOrDefaultAsync<CartHeaderDto>()
                    ?? throw new KeyNotFoundException("CART_NOT_FOUND");

        var items = (await multi.ReadAsync<CartItemDto>()).AsList();
        return new CartViewDto(header, items);
    }

    public async Task<CartViewDto> GetOrCreateAndViewAsync(long? userInfoId, long? deviceId, CancellationToken ct)
    {
        var id = await GetOrCreateCartIdAsync(userInfoId, deviceId, ct);
        return await ViewAsync(id, ct);
    }

    public async Task AddOrIncrementAsync(long cartId, long variantId, int quantity,
     string? nameVariant, decimal? priceVariant, string? imageVariant, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@cart_id", cartId);
        p.Add("@variant_id", variantId);
        p.Add("@quantity", quantity);
        p.Add("@name_variant", nameVariant);
        p.Add("@price_variant", priceVariant);
        p.Add("@image_variant", imageVariant);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_cart_add_or_increment", p,
                commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is 50111)
        {
            throw new InvalidOperationException("INVALID_QUANTITY");
        }
        catch (SqlException ex) when (ex.Number is 50110)
        {
            // Biến thể không tồn tại
            throw new KeyNotFoundException("VARIANT_NOT_FOUND");
        }
    }


    public async Task UpdateQuantityAsync(long cartId, long variantId, int quantity, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new { cart_id = cartId, variant_id = variantId, quantity };
        try
        {
            await con.ExecuteAsync(new CommandDefinition("dbo.usp_cart_update_quantity", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is 50112) { throw new InvalidOperationException("INVALID_QUANTITY"); }
        catch (SqlException ex) when (ex.Number is 50113) { throw new KeyNotFoundException("CART_ITEM_NOT_FOUND"); }
    }

    public async Task RemoveItemAsync(long cartId, long variantId, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new { cart_id = cartId, variant_id = variantId };
        try
        {
            await con.ExecuteAsync(new CommandDefinition("dbo.usp_cart_remove_item", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is 50114) { throw new KeyNotFoundException("CART_ITEM_NOT_FOUND"); }
    }

    public async Task ClearAsync(long cartId, CancellationToken ct)
    {
        using var con = db.Create();
        await con.ExecuteAsync(new CommandDefinition("dbo.usp_cart_clear", new { cart_id = cartId }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }
}
