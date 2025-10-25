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

    Task BatchSetQuantitiesByLineAsync(long cartId, IEnumerable<CartQtyChangeDto> changes, CancellationToken ct);

    // Chỉ còn 1 method compact chính thức
    Task<CartCompactResponse> ViewCompactAsync(long cartId, long[] lineIds, CancellationToken ct);

    Task RemoveLineAsync(long cartId, long lineId, CancellationToken ct);

    // Fallback: nếu muốn dùng phiên bản tính từ ViewAsync, ĐỔI TÊN để tránh trùng
    Task<CartCompactDto> ViewCompactFromViewAsync(long cartId, long[] lineIds, CancellationToken ct);
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

    // Batch: nhớ truyền OUTPUT param @response_json như SP yêu cầu
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
                p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

            // var respJson = p.Get<string>("@response_json"); // nếu cần đọc
        }
        catch (SqlException ex) when (ex.Number is 50120)
        { throw new InvalidOperationException("INVALID_CHANGES_JSON"); }
        catch (SqlException ex) when (ex.Number is 50121)
        { throw new KeyNotFoundException("CART_LINE_NOT_FOUND"); }
    }

    // ===== Compact CHÍNH: lấy trực tiếp từ DB
    public async Task<CartCompactResponse> ViewCompactAsync(long cartId, long[] lineIds, CancellationToken ct)
    {
        using var con = db.Create();

        var lines = (await con.QueryAsync<CartLineState>(new CommandDefinition(@"
            SELECT i.id AS Line_Id, i.quantity AS Quantity, i.price_variant AS Price_Variant
            FROM dbo.tbl_cart_item i
            WHERE i.cart_id = @cart_id AND i.status = 1 AND (@ids IS NULL OR i.id IN @ids)
        ", new { cart_id = cartId, ids = (lineIds == null || lineIds.Length == 0) ? null : lineIds }, cancellationToken: ct))).AsList();

        var totals = await con.QueryFirstAsync<CartTotals>(new CommandDefinition(@"
            WITH s AS (
              SELECT CAST(SUM(price_variant * quantity) AS decimal(18,2)) AS subtotal
              FROM dbo.tbl_cart_item
              WHERE cart_id=@cart_id AND status=1
            )
            SELECT
              s.subtotal AS Subtotal,
              ROUND(s.subtotal * 0.08, 0) AS Vat,
              CAST(0 AS decimal(18,2)) AS Shipping,
              s.subtotal + ROUND(s.subtotal * 0.08, 0) AS Grand
            FROM s
        ", new { cart_id = cartId }, cancellationToken: ct));

        return new CartCompactResponse(lines, totals);
    }

    // Xoá theo line_id
    public async Task RemoveLineAsync(long cartId, long lineId, CancellationToken ct)
    {
        using var con = db.Create();
        var affected = await con.ExecuteAsync(new CommandDefinition(@"
            UPDATE dbo.tbl_cart_item
            SET status = 0, updated_at = SYSDATETIME()
            WHERE cart_id = @cart_id AND id = @line_id AND status = 1
        ", new { cart_id = cartId, line_id = lineId }, cancellationToken: ct));

        if (affected == 0)
            throw new KeyNotFoundException("CART_LINE_NOT_FOUND");
    }

    // ===== Compact Fallback: ĐỔI TÊN để tránh trùng với method chính
    public async Task<CartCompactDto> ViewCompactFromViewAsync(long cartId, long[] lineIds, CancellationToken ct)
    {
        var view = await ViewAsync(cartId, ct);

        var lines = view.Items
            .Where(i => lineIds == null || lineIds.Contains(i.Id))
            .Select(i => new CartLineCompactDto(
                Line_Id: i.Id,
                Variant_Id: i.Variant_Id,
                Quantity: i.Quantity,
                Price_Variant: i.Price_Variant
            ))
            .ToList();

        var subtotal = view.Items.Sum(i => i.Price_Variant * i.Quantity);
        var shipping = 0m;
        var vat = Math.Round(subtotal * 0.08m, 0, MidpointRounding.AwayFromZero);
        var grand = subtotal + shipping + vat;

        return new CartCompactDto(lines, new CartTotalsDto(subtotal, vat, shipping, grand));
    }
}