using System.Data;
using System.Linq;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;

namespace HAShop.Api.Services;

public interface ICartService
{
    Task<long> GetOrCreateCartIdAsync(long? userInfoId, long? deviceId, CancellationToken ct);

    Task<CartViewDto> ViewAsync(long cartId, int channel = 1, DateTime? now = null, CancellationToken ct = default);
    Task<CartViewDto> GetOrCreateAndViewAsync(long? userInfoId, long? deviceId, int channel = 1, DateTime? now = null, CancellationToken ct = default);

    Task AddOrIncrementAsync(long cartId, long variantId, int quantity,
                             string? nameVariant, decimal? priceVariant, string? imageVariant,
                             CancellationToken ct);
    Task UpdateQuantityAsync(long cartId, long variantId, int quantity, CancellationToken ct);
    Task RemoveItemAsync(long cartId, long variantId, CancellationToken ct);
    Task ClearAsync(long cartId, CancellationToken ct);

    Task BatchSetQuantitiesByLineAsync(long cartId, IEnumerable<CartQtyChangeDto> changes, CancellationToken ct);

    // === COMPACT (dùng SP mới)
    Task<CartCompactResponse> ViewCompactAsync(long cartId, long[] lineIds, int channel = 1, DateTime? now = null, CancellationToken ct = default);

    Task RemoveLineAsync(long cartId, long lineId, CancellationToken ct);

    // Fallback cũ (giữ nguyên để tương thích nếu nơi nào còn gọi)
    Task<CartCompactDto> ViewCompactFromViewAsync(long cartId, long[] lineIds, int channel = 1, DateTime? now = null, CancellationToken ct = default);
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

        await con.ExecuteAsync(new CommandDefinition(
            "dbo.usp_cart_get_or_create",
            p,
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        return p.Get<long>("@cart_id");
    }

    public async Task<CartViewDto> ViewAsync(long cartId, int channel = 1, DateTime? now = null, CancellationToken ct = default)
    {
        using var con = db.Create();
        using var multi = await con.QueryMultipleAsync(new CommandDefinition(
            "dbo.usp_cart_view",
            new { cart_id = cartId, channel },   // CHANGED: bỏ now
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        var header = await multi.ReadFirstOrDefaultAsync<CartHeaderDto>()
                    ?? throw new KeyNotFoundException("CART_NOT_FOUND");

        var items = (await multi.ReadAsync<CartItemDto>()).AsList();
        return new CartViewDto(header, items);
    }


    public async Task<CartViewDto> GetOrCreateAndViewAsync(long? userInfoId, long? deviceId, int channel = 1, DateTime? now = null, CancellationToken ct = default)
    {
        var id = await GetOrCreateCartIdAsync(userInfoId, deviceId, ct);
        return await ViewAsync(id, channel, null, ct);   // CHANGED: bỏ now
    }


    public async Task<CartCompactResponse> ViewCompactAsync(
     long cartId, long[] lineIds, int channel = 1, DateTime? now = null, CancellationToken ct = default)
    {
        using var con = db.Create();

        using var multi = await con.QueryMultipleAsync(new CommandDefinition(
            "dbo.usp_cart_view_compact",
            new { cart_id = cartId, channel }, // KHÔNG truyền now
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        // RS1: LINES (Line_Id, Quantity, Price_Variant)
        var allLines = (await multi.ReadAsync<CartLineState>()).AsList();

        // Lọc nếu có lineIds
        IReadOnlyList<CartLineState> lines = allLines;
        if (lineIds != null && lineIds.Length > 0)
        {
            var set = new HashSet<long>(lineIds);
            lines = allLines.Where(l => set.Contains(l.Line_Id)).ToList();
        }

        // RS2: TOTALS (Subtotal, Vat, Shipping, Grand)
        var totals = await multi.ReadFirstAsync<CartTotals>();

        return new CartCompactResponse(lines, totals);
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
                "dbo.usp_cart_add_or_increment",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is 50111)
        { throw new InvalidOperationException("INVALID_QUANTITY"); }
        catch (SqlException ex) when (ex.Number is 50110)
        { throw new KeyNotFoundException("VARIANT_NOT_FOUND"); }
    }

    public async Task UpdateQuantityAsync(long cartId, long variantId, int quantity, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new { cart_id = cartId, variant_id = variantId, quantity };
        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_cart_update_quantity",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is 50112)
        { throw new InvalidOperationException("INVALID_QUANTITY"); }
        catch (SqlException ex) when (ex.Number is 50113)
        { throw new KeyNotFoundException("CART_ITEM_NOT_FOUND"); }
    }

    public async Task RemoveItemAsync(long cartId, long variantId, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new { cart_id = cartId, variant_id = variantId };
        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_cart_remove_item",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is 50114)
        { throw new KeyNotFoundException("CART_ITEM_NOT_FOUND"); }
    }

    public async Task ClearAsync(long cartId, CancellationToken ct)
    {
        using var con = db.Create();
        await con.ExecuteAsync(new CommandDefinition(
            "dbo.usp_cart_clear",
            new { cart_id = cartId },
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));
    }

    public async Task BatchSetQuantitiesByLineAsync(long cartId, IEnumerable<CartQtyChangeDto> changes, CancellationToken ct)
    {
        using var con = db.Create();

        var changesJson = System.Text.Json.JsonSerializer.Serialize(
            changes.Select(x => new { line_id = x.Line_Id, quantity = x.Quantity })
        );

        var p = new DynamicParameters();
        p.Add("@cart_id", cartId);
        p.Add("@changes_json", changesJson);
        p.Add("@response_json", dbType: DbType.String, direction: ParameterDirection.Output, size: -1);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_cart_lines_batch_set_quantity_json",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

            // var respJson = p.Get<string>("@response_json"); // nếu cần
        }
        catch (SqlException ex) when (ex.Number is 50120)
        { throw new InvalidOperationException("INVALID_CHANGES_JSON"); }
        catch (SqlException ex) when (ex.Number is 50121)
        { throw new KeyNotFoundException("CART_LINE_NOT_FOUND"); }
    }

    public async Task RemoveLineAsync(long cartId, long lineId, CancellationToken ct)
    {
        using var con = db.Create();
        var affected = await con.ExecuteAsync(new CommandDefinition(@"
            UPDATE dbo.tbl_cart_item
            SET status = 0, updated_at = SYSDATETIME()
            WHERE cart_id = @cart_id AND id = @line_id AND status = 1",
            new { cart_id = cartId, line_id = lineId },
            cancellationToken: ct));

        if (affected == 0)
            throw new KeyNotFoundException("CART_LINE_NOT_FOUND");
    }

    // ===== Fallback từ ViewAsync (giữ tương thích, vẫn ưu tiên effective nếu có)
    public async Task<CartCompactDto> ViewCompactFromViewAsync(long cartId, long[] lineIds, int channel = 1, DateTime? now = null, CancellationToken ct = default)
    {
        var view = await ViewAsync(cartId, channel, now, ct);

        var filtered = view.Items
            .Where(i => lineIds == null || lineIds.Contains(i.Id))
            .Select(i => new CartLineCompactDto(
                Line_Id: i.Id,
                Variant_Id: i.Variant_Id,
                Quantity: i.Quantity,
                Price_Variant: (i.Price_Effective > 0 ? i.Price_Effective : i.Price_Variant)
            ))
            .ToList();

        var subtotal = filtered.Sum(i => i.Price_Variant * i.Quantity);
        var shipping = 0m;
        var vat = Math.Round(subtotal * 0.08m, 0, MidpointRounding.AwayFromZero);
        var grand = subtotal + shipping + vat;

        return new CartCompactDto(filtered, new CartTotalsDto(subtotal, vat, shipping, grand));
    }
}
