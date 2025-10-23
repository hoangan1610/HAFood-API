using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;
using System.Data;
using System.Linq;
using System.Text.Json;

namespace HAShop.Api.Services;

public interface IOrderService
{
    Task<PlaceOrderResponse> PlaceFromCartAsync(long userId, PlaceOrderRequest req, CancellationToken ct);
    Task<OrderDetailDto?> GetAsync(long orderId, CancellationToken ct);
    Task<OrdersPageDto> ListByUserAsync(long userId, byte? status, int page, int pageSize, CancellationToken ct);
    Task<bool> UpdateStatusAsync(long orderId, byte newStatus, CancellationToken ct);
    Task<PaymentCreateResponse> CreatePaymentAsync(PaymentCreateRequest req, CancellationToken ct);
}

public class OrderService(ISqlConnectionFactory db) : IOrderService
{
    public async Task<PlaceOrderResponse> PlaceFromCartAsync(long userId, PlaceOrderRequest req, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@user_info_id", userId);
        p.Add("@device_id", req.Device_Id);
        p.Add("@cart_id", req.Cart_Id);
        p.Add("@ship_name", req.Ship_Name);
        p.Add("@ship_full_addr", req.Ship_Full_Address);
        p.Add("@ship_phone", req.Ship_Phone);
        p.Add("@payment_method", req.Payment_Method);
        p.Add("@ip", string.IsNullOrWhiteSpace(req.Ip) ? "" : req.Ip);
        p.Add("@note", req.Note);
        p.Add("@address_id", req.Address_Id);

        // 🔹 NEW: truyền mã khuyến mãi nếu có
        p.Add("@promo_code", req.Promo_Code);

        // 🔹 NEW: selected_line_ids_json
        if (req.Selected_Line_Ids is { Length: > 0 })
        {
            // mảng số => không bị ảnh hưởng bởi naming policy
            var jsonSel = JsonSerializer.Serialize(req.Selected_Line_Ids);
            p.Add("@selected_line_ids_json", jsonSel);
        }

        // 🔹 NEW: items_json (chú ý giữ đúng tên trường Variant_Id/Quantity cho OPENJSON)
        if (req.Items is { Count: > 0 })
        {
            var jsonItems = JsonSerializer.Serialize(
                req.Items.Select(i => new { Variant_Id = i.Variant_Id, Quantity = i.Quantity }),
                new JsonSerializerOptions { PropertyNamingPolicy = null }  // giữ nguyên PascalCase
            );
            p.Add("@items_json", jsonItems);
        }

        p.Add("@order_id", dbType: DbType.Int64, direction: ParameterDirection.Output);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_order_place_from_cart", p,
                commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 50201) { throw new InvalidOperationException("CART_NOT_FOUND"); }
        catch (SqlException ex) when (ex.Number == 50202) { throw new InvalidOperationException("CART_EMPTY"); }
        catch (SqlException ex) when (ex.Number == 50203) { throw new InvalidOperationException("OUT_OF_STOCK"); }

        var orderId = p.Get<long>("@order_id");
        var code = await con.ExecuteScalarAsync<string>(new CommandDefinition(
            "SELECT order_code FROM dbo.tbl_orders WHERE id=@id",
            new { id = orderId }, cancellationToken: ct)) ?? "";

        return new PlaceOrderResponse(orderId, code);
    }


    public async Task<OrderDetailDto?> GetAsync(long orderId, CancellationToken ct)
    {
        using var con = db.Create();
        using var multi = await con.QueryMultipleAsync(new CommandDefinition(
          "dbo.usp_order_get", new { order_id = orderId }, commandType: CommandType.StoredProcedure, cancellationToken: ct));

        var header = await multi.ReadFirstOrDefaultAsync<OrderHeaderDto>();
        if (header is null) return null;
        var items = (await multi.ReadAsync<OrderItemDto>()).AsList();
        return new OrderDetailDto(header, items);
    }

    public async Task<OrdersPageDto> ListByUserAsync(long userId, byte? status, int page, int pageSize, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@user_info_id", userId);
        p.Add("@status", status);
        p.Add("@page", page);
        p.Add("@page_size", pageSize);
        p.Add("@total_count", dbType: DbType.Int32, direction: ParameterDirection.Output);

        var items = (await con.QueryAsync<OrderHeaderDto>(new CommandDefinition(
          "dbo.usp_orders_list_by_user", p, commandType: CommandType.StoredProcedure, cancellationToken: ct))).AsList();

        var total = p.Get<int>("@total_count");
        return new OrdersPageDto(items, total, page, pageSize);
    }

    public async Task<bool> UpdateStatusAsync(long orderId, byte newStatus, CancellationToken ct)
    {
        using var con = db.Create();
        try
        {
            await con.ExecuteAsync(new CommandDefinition(
              "dbo.usp_order_update_status", new { order_id = orderId, new_status = newStatus },
              commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 50211)
        { return false; }
        return true;
    }

    public async Task<PaymentCreateResponse> CreatePaymentAsync(PaymentCreateRequest req, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@order_id", req.Order_Id);
        p.Add("@provider", req.Provider);
        p.Add("@method", req.Method);
        p.Add("@status", req.Status);
        p.Add("@amount", req.Amount);
        p.Add("@currency", req.Currency);
        p.Add("@transaction_id", req.Transaction_Id);
        p.Add("@merchant_ref", req.Merchant_Ref);
        p.Add("@error_code", req.Error_Code);
        p.Add("@error_message", req.Error_Message);
        p.Add("@paid_at", req.Paid_At);
        p.Add("@payment_id", dbType: DbType.Int64, direction: ParameterDirection.Output);

        await con.ExecuteAsync(new CommandDefinition(
          "dbo.usp_payment_tx_create", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));

        return new PaymentCreateResponse(p.Get<long>("@payment_id"));
    }


}
