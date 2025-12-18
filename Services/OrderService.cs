using System;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using HAShop.Api.Utils;                 // AppException, NotificationTypes
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace HAShop.Api.Services
{
    public interface IOrderService
    {
        Task<PlaceOrderResponse> PlaceFromCartAsync(long userId, PlaceOrderRequest req, CancellationToken ct);
        Task<OrderDetailDto?> GetAsync(long orderId, CancellationToken ct);
        Task<OrdersPageDto> ListByUserAsync(long userId, byte? status, int page, int pageSize, CancellationToken ct);
        Task<bool> UpdateStatusAsync(long orderId, byte newStatus, CancellationToken ct);
        Task<PaymentCreateResponse> CreatePaymentAsync(PaymentCreateRequest req, CancellationToken ct);
        Task<SwitchPaymentResponse> SwitchPaymentAsync(string orderCode, byte newMethod, string? reason, CancellationToken ct);
    }

    public class OrderService : IOrderService
    {
        private readonly ISqlConnectionFactory _db;
        private readonly ILoyaltyService _loyalty;
        private readonly INotificationService _notifications;
        private readonly ILogger<OrderService> _logger;
        private readonly IAdminOrderNotifier _adminOrderNotifier;
        private readonly IMissionService _missions;


        public OrderService(
    ISqlConnectionFactory db,
    ILoyaltyService loyalty,
    INotificationService notifications,
    IAdminOrderNotifier adminOrderNotifier,
    IMissionService missions,
    ILogger<OrderService> logger)
        {
            _db = db;
            _loyalty = loyalty;
            _notifications = notifications;
            _adminOrderNotifier = adminOrderNotifier;
            _missions = missions;
            _logger = logger;
        }


        public async Task<PlaceOrderResponse> PlaceFromCartAsync(
            long userId,
            PlaceOrderRequest req,
            CancellationToken ct)
        {
            using var con = _db.Create();

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
            p.Add("@promo_code", req.Promo_Code);

            // City/Ward/Weight
            p.Add("@ship_city_code", req.Ship_City_Code);
            p.Add("@ship_ward_code", req.Ship_Ward_Code);
            p.Add("@total_weight_gram", req.Total_Weight_Gram);

            if (req.Selected_Line_Ids is { Length: > 0 })
            {
                var jsonSel = JsonSerializer.Serialize(req.Selected_Line_Ids);
                p.Add("@selected_line_ids_json", jsonSel);
            }

            if (req.Items is { Count: > 0 })
            {
                var jsonItems = JsonSerializer.Serialize(
                    req.Items.Select(i => new { Variant_Id = i.Variant_Id, Quantity = i.Quantity }),
                    new JsonSerializerOptions { PropertyNamingPolicy = null }
                );
                p.Add("@items_json", jsonItems);
            }

            p.Add("@order_id", dbType: DbType.Int64, direction: ParameterDirection.Output);

            try
            {
                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_order_place_from_cart",
                    p,
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct,
                    commandTimeout: 60
                ));
            }
            catch (SqlException ex) when (ex.Number == 50201)
            {
                throw new AppException("CART_NOT_FOUND", ex.Message, ex);
            }
            catch (SqlException ex) when (ex.Number == 50202)
            {
                throw new AppException("CART_EMPTY", ex.Message, ex);
            }
            catch (SqlException ex) when (ex.Number == 50203)
            {
                throw new AppException("OUT_OF_STOCK", ex.Message, ex);
            }

            var orderId = p.Get<long>("@order_id");

            // Đọc code + tổng tiền + status để vừa trả về vừa notify
            var row = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
                """
    SELECT order_code,
           pay_total,
           status,
           ship_full_address,
           payment_method,
           placed_at
    FROM dbo.tbl_orders
    WHERE id = @id
    """,
                new { id = orderId },
                cancellationToken: ct,
                commandTimeout: 15
            ));

            string code = row?.order_code ?? "";
            decimal payTotal = row?.pay_total ?? 0m;
            byte status = row?.status ?? (byte)0;
            string shipFullAddress = row?.ship_full_address ?? "";
            byte? paymentMethod = row?.payment_method;
            DateTime? placedAt = row?.placed_at;

            // Rút gọn địa chỉ để không quá dài trên Telegram
            string? shipAddressShort = null;
            if (!string.IsNullOrWhiteSpace(shipFullAddress))
            {
                shipAddressShort = shipFullAddress.Length <= 100
                    ? shipFullAddress
                    : shipFullAddress.Substring(0, 97) + "...";
            }

            // Tính điểm dự kiến (chưa cộng vào loyalty)
            int estPoints = 0;
            if (payTotal > 0)
            {
                estPoints = (int)Math.Floor(payTotal / 1000m);
            }

            // 🔔 Notify: ORDER_STATUS_CHANGED – Đơn mới tạo
            try
            {
                var dataObj = new
                {
                    order_id = orderId,
                    order_code = code,
                    new_status = status,   // thường là 0 (Mới tạo)
                    est_points = estPoints
                };
                var dataJson = JsonSerializer.Serialize(dataObj);

                var title = $"Đơn {code} đã được tạo thành công";
                string body;
                if (estPoints > 0)
                {
                    body =
                        $"Shop sẽ xác nhận đơn trong thời gian sớm nhất.\n" +
                        $"Nếu giao thành công, bạn sẽ nhận được khoảng +{estPoints} điểm HAFood.";
                }
                else
                {
                    body = "Shop sẽ xác nhận đơn trong thời gian sớm nhất.";
                }

                await _notifications.CreateInAppAsync(
                    userId,
                    NotificationTypes.ORDER_STATUS_CHANGED,
                    title,
                    body,
                    dataJson,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Send ORDER_STATUS_CHANGED (created) failed for order {OrderId}, user {UserId}",
                    orderId, userId);
            }

            // 🔔 Thông báo ADMIN: có đơn mới (Telegram)
            // 🔔 Thông báo ADMIN:
            // - COD: notify ngay
            // - Online (ZaloPay/Pay2S): notify sau khi IPN xác nhận PAID (tránh báo đơn "chưa thanh toán")
            // 🔔 Thông báo ADMIN: chỉ gửi ngay với COD
            try
            {
                var pm = paymentMethod ?? req.Payment_Method; // fallback
                if (pm == 0) // COD
                {
                    await _adminOrderNotifier.NotifyNewOrderAsync(
                        orderId,
                        code,
                        payTotal,
                        req.Ship_Name ?? "",
                        req.Ship_Phone ?? "",
                        shipAddressShort,
                        pm,
                        placedAt,
                        ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Notify admin new order failed. OrderId={OrderId}, Code={OrderCode}",
                    orderId, code);
            }



            return new PlaceOrderResponse(orderId, code);

        }

        public async Task<OrderDetailDto?> GetAsync(long orderId, CancellationToken ct)
        {
            using var con = _db.Create();
            using var multi = await con.QueryMultipleAsync(new CommandDefinition(
                "dbo.usp_order_get",
                new { order_id = orderId },
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct,
                commandTimeout: 30
            ));

            var header = await multi.ReadFirstOrDefaultAsync<OrderHeaderDto>();
            if (header is null) return null;
            var items = (await multi.ReadAsync<OrderItemDto>()).AsList();
            return new OrderDetailDto(header, items);
        }

        public async Task<OrdersPageDto> ListByUserAsync(
            long userId,
            byte? status,
            int page,
            int pageSize,
            CancellationToken ct)
        {
            using var con = _db.Create();
            var p = new DynamicParameters();
            p.Add("@user_info_id", userId);
            p.Add("@status", status);
            p.Add("@page", page);
            p.Add("@page_size", pageSize);
            p.Add("@total_count", dbType: DbType.Int32, direction: ParameterDirection.Output);

            var items = (await con.QueryAsync<OrderHeaderDto>(new CommandDefinition(
                "dbo.usp_orders_list_by_user",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct,
                commandTimeout: 30
            ))).AsList();

            var total = p.Get<int>("@total_count");
            return new OrdersPageDto(items, total, page, pageSize);
        }

        /// <summary>
        /// Update trạng thái đơn.
        /// Nếu newStatus = 3 (Đã giao) → cộng điểm loyalty + notify loyalty.
        /// Bất kỳ status nào thay đổi → bắn ORDER_STATUS_CHANGED.
        /// </summary>
        public async Task<bool> UpdateStatusAsync(long orderId, byte newStatus, CancellationToken ct)
        {
            using var con = _db.Create();

            try
            {
                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_order_update_status",
                    new { order_id = orderId, new_status = newStatus },
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct,
                    commandTimeout: 30
                ));
            }
            catch (SqlException ex) when (ex.Number == 50211)
            {
                // ORDER_NOT_FOUND
                return false;
            }

            // Đọc lại order để có user_id, code, pay_total
            // Đọc lại order để có user_id, code, pay_total, delivered_at
            long? userId = null;
            string orderCode = "";
            decimal payTotal = 0m;
            DateTime? deliveredAt = null;   // <--- THÊM BIẾN NÀY

            try
            {
                var row = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
                    """
        SELECT user_info_id, order_code, pay_total, delivered_at
        FROM dbo.tbl_orders
        WHERE id = @id
        """,
                    new { id = orderId },
                    cancellationToken: ct,
                    commandTimeout: 15
                ));

                if (row != null)
                {
                    userId = (long)row.user_info_id;
                    orderCode = (string)row.order_code;
                    payTotal = (decimal)row.pay_total;
                    deliveredAt = (DateTime?)row.delivered_at;   // <--- GÁN
                }
            }

            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error fetching order after usp_order_update_status. OrderId={OrderId}",
                    orderId);
            }

            // 🔔 Notify: ORDER_STATUS_CHANGED cho mọi trạng thái
            if (userId.HasValue)
            {
                try
                {
                    int estPoints = 0;
                    if (payTotal > 0)
                    {
                        estPoints = (int)Math.Floor(payTotal / 1000m);
                    }

                    string title;
                    string body;

                    switch (newStatus)
                    {
                        case 0:
                            title = $"Đơn {orderCode} đang chờ xác nhận";
                            body = "Đơn hàng của bạn đã được tạo và đang chờ shop xác nhận.";
                            break;
                        case 1:
                            title = $"Đơn {orderCode} đã được shop xác nhận";
                            body = "Shop đã xác nhận đơn hàng của bạn, chuẩn bị giao.";
                            break;
                        case 2:
                            title = $"Đơn {orderCode} đang được giao";
                            body = "Đơn hàng của bạn đang trên đường giao.";
                            break;
                        case 3:
                            title = $"Đơn {orderCode} đã giao thành công";
                            body = "Cảm ơn bạn đã mua hàng tại HAFood!";
                            break;
                        case 4:
                            title = $"Đơn {orderCode} đã bị huỷ";
                            body = "Đơn hàng của bạn đã bị huỷ. Nếu cần hỗ trợ, vui lòng liên hệ HAFood.";
                            break;
                        default:
                            title = $"Đơn {orderCode} vừa được cập nhật trạng thái";
                            body = "Đơn hàng của bạn vừa có cập nhật mới.";
                            break;
                    }

                    var dataObj = new
                    {
                        order_id = orderId,
                        order_code = orderCode,
                        new_status = newStatus,
                        est_points = estPoints
                    };
                    var dataJson = JsonSerializer.Serialize(dataObj);

                    await _notifications.CreateInAppAsync(
                        userId.Value,
                        NotificationTypes.ORDER_STATUS_CHANGED,
                        title,
                        body,
                        dataJson,
                        ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Send ORDER_STATUS_CHANGED failed for order {OrderId}, user {UserId}",
                        orderId, userId);
                }
            }

            // Nếu KHÔNG phải trạng thái "Đã giao" → không cộng điểm, return
            // Nếu KHÔNG phải trạng thái "Đã giao" → không cộng điểm, return
            if (newStatus != 3 || !userId.HasValue)
                return true;

            // Đã giao: cộng loyalty (đã có SP kiểm tra status = 3)
            try
            {
                // Rule: 1 điểm / 1.000đ
                int points = 0;
                if (payTotal > 0)
                {
                    points = (int)Math.Floor(payTotal / 1000m);
                }

                if (points > 0)
                {
                    var reason = $"Hoàn tất đơn hàng #{orderId}";
                    await _loyalty.AddPointsFromOrderAsync(
                        userId.Value,
                        orderId,
                        points,
                        reason,
                        ct);
                }
            }
            catch (AppException ex)
            {
                _logger.LogError(ex,
                    "AddPointsFromOrderAsync failed for Order {OrderId}, User {UserId}",
                    orderId, userId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Unexpected error when adding loyalty points for Order {OrderId}, User {UserId}",
                    orderId, userId);
            }

            // ✅ Gọi Mission Engine sau khi đơn đã giao
            try
            {
                await _missions.CheckOrderMissionsAsync(
                    orderId,
                    userId.Value,
                    payTotal,
                    deliveredAt,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "CheckOrderMissionsAsync failed for Order {OrderId}, User {UserId}",
                    orderId, userId);
            }

            return true;

        }

        public async Task<PaymentCreateResponse> CreatePaymentAsync(PaymentCreateRequest req, CancellationToken ct)
        {
            using var con = _db.Create();
            var p = new DynamicParameters();
            p.Add("@order_id", req.Order_Id);
            p.Add("@provider", req.Provider);
            p.Add("@method", req.Method);
            p.Add("@status", req.Status);
            p.Add("@amount", req.Amount);
            p.Add("@currency", string.IsNullOrWhiteSpace(req.Currency) ? "VND" : req.Currency);
            p.Add("@transaction_id", req.Transaction_Id ?? Guid.NewGuid().ToString("N"));
            p.Add("@merchant_ref", req.Merchant_Ref);
            p.Add("@error_code", req.Error_Code);
            p.Add("@error_message", req.Error_Message);
            p.Add("@paid_at", req.Paid_At);
            p.Add("@payment_id", dbType: DbType.Int64, direction: ParameterDirection.Output);

            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_payment_tx_create",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct,
                commandTimeout: 30
            ));

            return new PaymentCreateResponse(p.Get<long>("@payment_id"));
        }

        public async Task<SwitchPaymentResponse> SwitchPaymentAsync(
     string orderCode,
     byte newMethod,
     string? reason,
     CancellationToken ct)
        {
            using var con = _db.Create();

            // Open connection (DbConnection safe)
            if (con is DbConnection dbc) await dbc.OpenAsync(ct);
            else con.Open();

            using var tx = con.BeginTransaction();

            try
            {
                var o = await con.QueryFirstOrDefaultAsync<OrderHeaderDto>(new CommandDefinition(
                    "SELECT TOP 1 * FROM dbo.tbl_orders WHERE order_code=@c",
                    new { c = orderCode },
                    transaction: tx,
                    cancellationToken: ct,
                    commandTimeout: 15
                ));

                if (o is null) throw new AppException("ORDER_NOT_FOUND");
                if (string.Equals(o.Payment_Status, "Paid", StringComparison.OrdinalIgnoreCase))
                    throw new AppException("ORDER_ALREADY_PAID");

                var amount = o.Pay_Total;

                static string MapProvider(byte? m) => m switch
                {
                    1 => "ZALOPAY",
                    2 => "PAY2S",   // slot cũ VNPAY
                    0 => "COD",
                    _ => "UNKNOWN"
                };

                var oldProvider = string.IsNullOrWhiteSpace(o.Payment_Provider)
                    ? MapProvider(o.Payment_Method)
                    : o.Payment_Provider;

                // Log “user switched payment” (status=0)
                if (!string.IsNullOrWhiteSpace(oldProvider) || (o.Payment_Method ?? 0) != newMethod)
                {
                    await con.ExecuteAsync(new CommandDefinition(
                        """
                INSERT dbo.tbl_payment_transaction
                    (order_id, provider, method, status, amount, currency, transaction_id, merchant_ref,
                     paid_at, error_code, error_message, created_at, updated_at)
                VALUES
                    (@oid, @prov, COALESCE(@m,0), 0, @amt, 'VND', @txid, @oref,
                     NULL, @err, @msg, SYSDATETIME(), SYSDATETIME())
                """,
                        new
                        {
                            oid = o.Id,
                            prov = oldProvider,
                            m = o.Payment_Method,
                            amt = amount,
                            txid = Guid.NewGuid().ToString("N"),
                            oref = orderCode,
                            err = "USER_SWITCH_PAYMENT",
                            msg = reason ?? "User switched payment method"
                        },
                        transaction: tx,
                        cancellationToken: ct,
                        commandTimeout: 15
                    ));
                }

                var newProvider = MapProvider(newMethod);
                var newStatus = (newMethod == 0) ? "Unpaid" : "Pending";

                await con.ExecuteAsync(new CommandDefinition(
                    """
            UPDATE dbo.tbl_orders
            SET payment_method = @pm,
                payment_provider = @prov,
                payment_status = @stt,
                payment_ref = NULL,
                updated_at = SYSDATETIME()
            WHERE order_code = @code
            """,
                    new
                    {
                        code = orderCode,
                        pm = newMethod,
                        prov = (newMethod == 0 ? (string?)null : newProvider),
                        stt = newStatus
                    },
                    transaction: tx,
                    cancellationToken: ct,
                    commandTimeout: 15
                ));

                tx.Commit();

                return new SwitchPaymentResponse
                {
                    Order_Code = orderCode,
                    New_Method = newMethod,
                    New_Status = newStatus
                };
            }
            catch
            {
                try { tx.Rollback(); } catch { /* ignore */ }
                throw;
            }
        }

    }
    }
