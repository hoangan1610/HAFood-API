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

namespace HAShop.Api.Services
{
    public interface IOrderService
    {
        Task<PlaceOrderResponse> PlaceFromCartAsync(long userId, PlaceOrderRequest req, CancellationToken ct);
        Task<OrderDetailDto?> GetAsync(long orderId, CancellationToken ct);

        // ✅ NEW: thêm orderCode để search
        Task<OrdersPageDto> ListByUserAsync(long userId, byte? status, string? orderCode, int page, int pageSize, CancellationToken ct);

        // ✅ Admin update status (các trạng thái)
        Task<bool> UpdateStatusAsync(long orderId, byte newStatus, CancellationToken ct);

        // ✅ NEW: shipper/admin báo đã giao (status=3)
        Task<bool> ReportDeliveredAsync(long orderId, CancellationToken ct);

        // ✅ NEW: user xác nhận đã nhận hàng (status=7) -> cộng điểm + mission
        Task<bool> ConfirmReceivedAsync(long orderId, long userId, CancellationToken ct);

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

        // ✅ THỐNG NHẤT METHOD IDs
        private const byte METHOD_COD = 0;
        private const byte METHOD_MOMO = 1;
        private const byte METHOD_PAY2S = 2;
        private const byte METHOD_ZALOPAY = 9;

        // ✅ Status IDs theo Tbl_OrderStatus Type=2 (bạn đã gửi)
        private const byte ST_NEW = 0;
        private const byte ST_CONFIRMED = 1;
        private const byte ST_SHIPPING = 2;          // (tuỳ bạn dùng)
        private const byte ST_DELIVERED = 3;         // "Đã giao" (shipper báo giao)
        private const byte ST_CANCELED = 4;          // "Đã huỷ"
        private const byte ST_WAIT_PICKUP = 5;       // "Chờ lấy hàng"
        private const byte ST_OUT_FOR_DELIVERY = 6;  // "Đang giao hàng"
        private const byte ST_RECEIVED = 7;          // "Đã nhận hàng" (final)
        private const byte ST_RETURN = 8;            // "Hoàn trả"
        private const byte ST_CANCEL_ALT = 9;        // "Huỷ đơn"
        private const byte ST_OTHER = 10;

        private sealed class OrderSwitchRow
        {
            public long Id { get; set; }
            public decimal PayTotal { get; set; }
            public string? PaymentStatus { get; set; }
            public string? PaymentProvider { get; set; }
            public byte? PaymentMethod { get; set; }
            public string? PaymentRef { get; set; }

            public OrderSwitchRow() { }
        }

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
            catch (SqlException ex) when (ex.Number == 50201) { throw new AppException("CART_NOT_FOUND", ex.Message, ex); }
            catch (SqlException ex) when (ex.Number == 50202) { throw new AppException("CART_EMPTY", ex.Message, ex); }
            catch (SqlException ex) when (ex.Number == 50203) { throw new AppException("OUT_OF_STOCK", ex.Message, ex); }

            var orderId = p.Get<long>("@order_id");

            // Đọc code + tổng tiền + status để vừa trả về vừa notify
            var row = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
                """
                SELECT order_code,
                       pay_total,
                       status,
                       ship_full_address,
                       payment_method,
                       payment_status,
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
            byte? paymentMethodDb = row?.payment_method;
            DateTime? placedAt = row?.placed_at;

            var pm = paymentMethodDb ?? req.Payment_Method;
            bool isCod = pm == METHOD_COD;

            // Rút gọn địa chỉ để không quá dài trên Telegram
            string? shipAddressShort = null;
            if (!string.IsNullOrWhiteSpace(shipFullAddress))
            {
                shipAddressShort = shipFullAddress.Length <= 100
                    ? shipFullAddress
                    : shipFullAddress.Substring(0, 97) + "...";
            }

            int estPoints = (payTotal > 0) ? (int)Math.Floor(payTotal / 1000m) : 0;

            // 🔔 Notify USER (In-app) - CHỈ COD
            if (isCod)
            {
                try
                {
                    var dataJson = JsonSerializer.Serialize(new
                    {
                        order_id = orderId,
                        order_code = code,
                        new_status = status,
                        est_points = estPoints
                    });

                    var title = $"Đơn {code} đã được tạo thành công";
                    string body = (estPoints > 0)
                        ? $"Shop sẽ xác nhận đơn trong thời gian sớm nhất.\nNếu giao thành công, bạn sẽ nhận được khoảng +{estPoints} điểm HAFood."
                        : "Shop sẽ xác nhận đơn trong thời gian sớm nhất.";

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
                    _logger.LogError(ex, "Send ORDER_STATUS_CHANGED (created) failed for order {OrderId}, user {UserId}", orderId, userId);
                }
            }

            // 🔔 Notify ADMIN (Telegram) - COD: notify ngay, Online: notify khi Paid ở PaymentsController
            try
            {
                if (isCod)
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
                _logger.LogError(ex, "Notify admin new order failed. OrderId={OrderId}, Code={OrderCode}", orderId, code);
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
            string? orderCode,
            int page,
            int pageSize,
            CancellationToken ct)
        {
            using var con = _db.Create();
            var p = new DynamicParameters();
            p.Add("@user_info_id", userId);
            p.Add("@status", status);
            p.Add("@order_code", string.IsNullOrWhiteSpace(orderCode) ? null : orderCode.Trim());
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
        /// ✅ Admin update status: chỉ bắn notify.
        /// ❌ KHÔNG cộng điểm/mission ở status=3 nữa.
        /// ✅ Điểm + mission chỉ chạy khi user ConfirmReceived (status=7).
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
                return false;
            }

            // đọc lại để notify
            long? userId = null;
            string orderCode = "";
            decimal payTotal = 0m;
            string? paymentStatus = null;

            try
            {
                var row = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
                    """
                    SELECT user_info_id, order_code, pay_total, payment_status
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
                    paymentStatus = (string?)row.payment_status;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching order after usp_order_update_status. OrderId={OrderId}", orderId);
            }

            // chặn notify khi huỷ do payment cancel
            bool suppressCancelNotify =
                (newStatus == ST_CANCELED || newStatus == ST_CANCEL_ALT) &&
                string.Equals(paymentStatus, "Canceled", StringComparison.OrdinalIgnoreCase);

            if (userId.HasValue && !suppressCancelNotify)
            {
                try
                {
                    int estPoints = payTotal > 0 ? (int)Math.Floor(payTotal / 1000m) : 0;

                    (string title, string body) = BuildStatusMessage(orderCode, newStatus, estPoints);

                    var dataJson = JsonSerializer.Serialize(new
                    {
                        order_id = orderId,
                        order_code = orderCode,
                        new_status = newStatus,
                        est_points = estPoints
                    });

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
                    _logger.LogError(ex, "Send ORDER_STATUS_CHANGED failed for order {OrderId}, user {UserId}", orderId, userId);
                }
            }

            return true;
        }

        /// <summary>
        /// ✅ Admin/Shipper báo đã giao: set status=3
        /// (Không cộng điểm/mission)
        /// </summary>
        public async Task<bool> ReportDeliveredAsync(long orderId, CancellationToken ct)
        {
            using var con = _db.Create();

            try
            {
                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_order_report_delivered",
                    new { order_id = orderId },
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct,
                    commandTimeout: 30
                ));
            }
            catch (SqlException ex) when (ex.Number == 50211)
            {
                return false;
            }
            catch (SqlException ex) when (ex.Number == 50212)
            {
                throw new AppException("ORDER_NOT_IN_SHIPPING", ex.Message, ex);
            }

            // notify user: đã báo giao
            try
            {
                var row = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
                    "SELECT user_info_id, order_code, pay_total FROM dbo.tbl_orders WHERE id=@id",
                    new { id = orderId },
                    cancellationToken: ct,
                    commandTimeout: 15
                ));

                if (row != null)
                {
                    long uid = (long)row.user_info_id;
                    string code = (string)row.order_code;
                    decimal payTotal = (decimal)row.pay_total;
                    int estPoints = payTotal > 0 ? (int)Math.Floor(payTotal / 1000m) : 0;

                    var dataJson = JsonSerializer.Serialize(new
                    {
                        order_id = orderId,
                        order_code = code,
                        new_status = ST_DELIVERED,
                        est_points = estPoints
                    });

                    await _notifications.CreateInAppAsync(
                        uid,
                        NotificationTypes.ORDER_STATUS_CHANGED,
                        $"Đơn {code} đã được báo giao",
                        "Shipper báo đã giao. Nếu bạn đã nhận hàng, vui lòng bấm 'Đã nhận hàng' để hoàn tất đơn.",
                        dataJson,
                        ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notify report delivered failed. OrderId={OrderId}", orderId);
            }

            return true;
        }

        /// <summary>
        /// ✅ User xác nhận đã nhận hàng: set status=7
        /// ✅ Cộng điểm + mission ở đây.
        /// </summary>
        public async Task<bool> ConfirmReceivedAsync(long orderId, long userId, CancellationToken ct)
        {
            using var con = _db.Create();

            try
            {
                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_order_confirm_received",
                    new { order_id = orderId, user_info_id = userId },
                    commandType: CommandType.StoredProcedure,
                    cancellationToken: ct,
                    commandTimeout: 30
                ));
            }
            catch (SqlException ex) when (ex.Number == 50213)
            {
                return false; // ORDER_CANNOT_CONFIRM
            }

            // đọc lại để lấy payTotal + deliveredAt
            string orderCode = "";
            decimal payTotal = 0m;
            DateTime? deliveredAt = null;

            try
            {
                var row = await con.QueryFirstOrDefaultAsync(new CommandDefinition(
                    "SELECT order_code, pay_total, delivered_at FROM dbo.tbl_orders WHERE id=@id AND user_info_id=@uid",
                    new { id = orderId, uid = userId },
                    cancellationToken: ct,
                    commandTimeout: 15
                ));

                if (row != null)
                {
                    orderCode = (string)row.order_code;
                    payTotal = (decimal)row.pay_total;
                    deliveredAt = (DateTime?)row.delivered_at;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Fetch order after confirm received failed. OrderId={OrderId} UserId={UserId}", orderId, userId);
            }

            // cộng loyalty
            try
            {
                int points = payTotal > 0 ? (int)Math.Floor(payTotal / 1000m) : 0;
                if (points > 0)
                {
                    var reason = $"Xác nhận nhận hàng #{orderId}";
                    await _loyalty.AddPointsFromOrderAsync(userId, orderId, points, reason, ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AddPointsFromOrderAsync failed Order={OrderId} User={UserId}", orderId, userId);
            }

            // mission
            try
            {
                await _missions.CheckOrderMissionsAsync(orderId, userId, payTotal, deliveredAt, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CheckOrderMissionsAsync failed Order={OrderId} User={UserId}", orderId, userId);
            }

            // notify user: đã nhận
            try
            {
                int estPoints = payTotal > 0 ? (int)Math.Floor(payTotal / 1000m) : 0;

                var dataJson = JsonSerializer.Serialize(new
                {
                    order_id = orderId,
                    order_code = orderCode,
                    new_status = ST_RECEIVED,
                    est_points = estPoints
                });

                await _notifications.CreateInAppAsync(
                    userId,
                    NotificationTypes.ORDER_STATUS_CHANGED,
                    $"Đơn {orderCode} đã nhận hàng",
                    estPoints > 0
                        ? $"Cảm ơn bạn! Bạn nhận được khoảng +{estPoints} điểm HAFood."
                        : "Cảm ơn bạn đã mua hàng tại HAFood!",
                    dataJson,
                    ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Notify confirm received failed Order={OrderId} User={UserId}", orderId, userId);
            }

            return true;
        }

        private static (string title, string body) BuildStatusMessage(string orderCode, byte newStatus, int estPoints)
        {
            return newStatus switch
            {
                ST_NEW => ($"Đơn {orderCode} mới tạo", "Đơn hàng của bạn đã được tạo và đang chờ shop xử lý."),
                ST_CONFIRMED => ($"Đơn {orderCode} đã xác nhận", "Shop đã xác nhận đơn hàng của bạn."),
                ST_WAIT_PICKUP => ($"Đơn {orderCode} chờ lấy hàng", "Đơn hàng đang chờ lấy hàng để bắt đầu giao."),
                ST_SHIPPING or ST_OUT_FOR_DELIVERY => ($"Đơn {orderCode} đang giao", "Đơn hàng của bạn đang trên đường giao."),
                ST_DELIVERED => ($"Đơn {orderCode} đã được báo giao", "Shipper báo đã giao. Vui lòng bấm 'Đã nhận hàng' để hoàn tất."),
                ST_RECEIVED => ($"Đơn {orderCode} đã nhận hàng",
                    estPoints > 0 ? $"Cảm ơn bạn! Bạn nhận được khoảng +{estPoints} điểm HAFood." : "Cảm ơn bạn đã mua hàng tại HAFood!"),
                ST_CANCELED or ST_CANCEL_ALT => ($"Đơn {orderCode} đã huỷ", "Đơn hàng của bạn đã bị huỷ. Nếu cần hỗ trợ, vui lòng liên hệ HAFood."),
                ST_RETURN => ($"Đơn {orderCode} hoàn trả", "Đơn hàng của bạn đang trong trạng thái hoàn trả."),
                _ => ($"Đơn {orderCode} vừa cập nhật", "Đơn hàng của bạn vừa có cập nhật mới.")
            };
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
            if (string.IsNullOrWhiteSpace(orderCode))
                throw new AppException("ORDER_CODE_EMPTY");

            if (newMethod != METHOD_COD && newMethod != METHOD_MOMO && newMethod != METHOD_PAY2S && newMethod != METHOD_ZALOPAY)
                throw new AppException("UNSUPPORTED_METHOD");

            using var con = _db.Create();
            if (con is DbConnection dbc) await dbc.OpenAsync(ct);
            else con.Open();

            using var tx = con.BeginTransaction();

            static string MapProvider(byte? m) => m switch
            {
                0 => "COD",
                1 => "MOMO",
                2 => "PAY2S",
                9 => "ZALOPAY",
                _ => "UNKNOWN"
            };

            try
            {
                // ✅ SELECT rõ cột + lock row -> tránh Dapper record ctor fail + tránh race
                var o = await con.QueryFirstOrDefaultAsync<OrderSwitchRow>(new CommandDefinition(
                    """
            SELECT TOP(1)
                id               AS Id,
                pay_total        AS PayTotal,
                payment_status   AS PaymentStatus,
                payment_provider AS PaymentProvider,
                payment_method   AS PaymentMethod,
                payment_ref      AS PaymentRef
            FROM dbo.tbl_orders WITH (UPDLOCK, HOLDLOCK)
            WHERE order_code = @c
            """,
                    new { c = orderCode.Trim() },
                    transaction: tx,
                    cancellationToken: ct,
                    commandTimeout: 15
                ));

                if (o is null) throw new AppException("ORDER_NOT_FOUND");
                if (string.Equals(o.PaymentStatus, "Paid", StringComparison.OrdinalIgnoreCase))
                    throw new AppException("ORDER_ALREADY_PAID");

                var oldMethod = o.PaymentMethod ?? METHOD_COD;

                // ✅ idempotent: nếu switch đúng method đang có thì trả OK (không phá pending hiện tại)
                if (oldMethod == newMethod)
                {
                    var statusSame = (newMethod == METHOD_COD) ? "Unpaid" : "Pending";
                    tx.Commit();
                    return new SwitchPaymentResponse
                    {
                        Order_Code = orderCode,
                        New_Method = newMethod,
                        New_Status = statusSame
                    };
                }

                var oldProvider = !string.IsNullOrWhiteSpace(o.PaymentProvider)
                    ? o.PaymentProvider!
                    : MapProvider(oldMethod);

                // log transaction: user switch
                await con.ExecuteAsync(new CommandDefinition(
                    """
            INSERT dbo.tbl_payment_transaction
                (order_id, provider, method, status, amount, currency, transaction_id, merchant_ref,
                 paid_at, error_code, error_message, created_at, updated_at)
            VALUES
                (@oid, @prov, @meth, 0, CAST(@amt AS decimal(12,2)), 'VND', @txid, @oref,
                 NULL, @err, @msg, SYSDATETIME(), SYSDATETIME())
            """,
                    new
                    {
                        oid = o.Id,
                        prov = oldProvider,
                        meth = oldMethod,
                        amt = o.PayTotal,
                        txid = Guid.NewGuid().ToString("N"),
                        oref = orderCode,
                        err = "USER_SWITCH_PAYMENT",
                        msg = string.IsNullOrWhiteSpace(reason) ? "User switched payment method" : reason
                    },
                    transaction: tx,
                    cancellationToken: ct,
                    commandTimeout: 15
                ));

                var newProvider = MapProvider(newMethod);
                var newStatus = (newMethod == METHOD_COD) ? "Unpaid" : "Pending";

                await con.ExecuteAsync(new CommandDefinition(
                    """
            UPDATE dbo.tbl_orders
            SET payment_method   = @pm,
                payment_provider = @prov,
                payment_status   = @stt,
                payment_ref      = NULL,
                paid_at          = NULL,
                updated_at       = SYSDATETIME()
            WHERE order_code = @code
            """,
                    new
                    {
                        code = orderCode,
                        pm = newMethod,
                        prov = (newMethod == METHOD_COD ? (string?)null : newProvider),
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
                try { tx.Rollback(); } catch { }
                throw;
            }
        }
    }


    }
