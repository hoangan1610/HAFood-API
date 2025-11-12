// File: Api/Services/PromotionService.cs
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace HAShop.Api.Services
{
    public sealed class PromotionService : IPromotionService
    {
        private readonly ISqlConnectionFactory _dbFactory;
        private readonly ILogger<PromotionService> _log;
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            NumberHandling = JsonNumberHandling.Strict
        };

        public PromotionService(ISqlConnectionFactory dbFactory, ILogger<PromotionService> log)
        {
            _dbFactory = dbFactory;
            _log = log;
        }

        // PromotionService.cs

        private static string BuildItemsJson<TItem>(IReadOnlyCollection<TItem> items)
    where TItem : class
        {
            if (items is not { Count: > 0 }) return "[]";

            var snake = items
                // lọc luôn item lỗi dữ liệu để tránh rớt xuống DB
                .Select(x => new
                {
                    product_id = GetProp<long?>(x, "ProductId") ?? GetProp<long?>(x, "product_id"),
                    variant_id = GetProp<long?>(x, "VariantId") ?? GetProp<long?>(x, "variant_id"),
                    // 👇 ĐỔI TÊN TRƯỜNG JSON THÀNH `qty`
                    qty = GetProp<int?>(x, "Quantity") ?? GetProp<int?>(x, "qty") ?? 0,
                    unit_price = GetProp<decimal?>(x, "UnitPrice")
                                 ?? GetProp<decimal?>(x, "unit_price")
                                 ?? GetProp<decimal?>(x, "Price")
                                 ?? 0m
                })
                // chỉ giữ item hợp lệ (variant_id có giá trị và qty > 0)
                .Where(i => (i.variant_id ?? 0) > 0 && i.qty > 0);

            var opts = new JsonSerializerOptions
            {
                PropertyNamingPolicy = null, // giữ nguyên snake_case
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };
            var json = JsonSerializer.Serialize(snake, opts);

            // debug tạm để chắc ăn
            // _log.LogInformation("items_json: {json}", json);

            return json;
        }

        private static T? GetProp<T>(object obj, string name)
        {
            var prop = obj.GetType().GetProperty(name);
            if (prop == null) return default;
            var val = prop.GetValue(obj);
            return val is T t ? t : default;
        }

        public async Task<PromoListResponse> ListActiveAsync(long? userId, string? deviceUuid, PromoListRequest req, CancellationToken ct = default)
        {
            if (req is null) throw new ArgumentNullException(nameof(req));
            if (req.Subtotal < 0 || req.ShippingFee < 0) throw new ArgumentException("subtotal/shipping_fee must be >= 0");

            var itemsJson = BuildItemsJson(req.Items ?? new());


            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            var p = new DynamicParameters();
            p.Add("@user_info_id", userId, DbType.Int64);
            p.Add("@device_uuid", deviceUuid, DbType.String);
            p.Add("@channel", req.Channel, DbType.Byte);
            p.Add("@items_json", itemsJson, DbType.String);
            p.Add("@subtotal", req.Subtotal, DbType.Decimal);
            p.Add("@shipping_fee", req.ShippingFee, DbType.Decimal);
            p.Add("@now_utc", DateTime.UtcNow, DbType.DateTime2);
            p.Add("@limit", 50, DbType.Int32);

            var cmd = new CommandDefinition(
                "dbo.usp_promotion_list_active_for_cart",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct,
                commandTimeout: 30
            );

            var rows = await con.QueryAsync<PromoCandidateRow>(cmd);

            var list = new List<PromoCandidateDto>();
            foreach (var r in rows)
            {
                list.Add(new PromoCandidateDto(
                    r.promotion_id, r.code, r.name, r.type, r.value,
                    r.max_discount, r.min_order_amount, r.apply_scope,
                    r.is_exclusive, r.is_stackable, r.priority,
                    r.estimated_discount, r.status_text, r.reason ?? string.Empty
                ));
            }

            return new PromoListResponse(list);
        }

        public async Task<PromoQuoteResponse> QuoteAsync(long? userId, string? deviceUuid, PromoQuoteRequest req, CancellationToken ct = default)
        {
            if (req is null) throw new ArgumentNullException(nameof(req));
            if (req.Subtotal < 0 || req.ShippingFee < 0) throw new ArgumentException("subtotal/shipping_fee must be >= 0");

            var itemsJson = BuildItemsJson(req.Items ?? new());


            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            var p = new DynamicParameters();
            p.Add("@user_info_id", userId, DbType.Int64);
            p.Add("@device_uuid", deviceUuid, DbType.String);
            p.Add("@channel", req.Channel, DbType.Byte);
            p.Add("@code", req.Code, DbType.String); // SP đã xử lý code_ci
            p.Add("@items_json", itemsJson, DbType.String);
            p.Add("@subtotal", req.Subtotal, DbType.Decimal);
            p.Add("@shipping_fee", req.ShippingFee, DbType.Decimal);
            p.Add("@now_utc", DateTime.UtcNow, DbType.DateTime2);

            var cmd = new CommandDefinition(
                "dbo.usp_promotion_quote",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct,
                commandTimeout: 30
            );

            using var multi = await con.QueryMultipleAsync(cmd);

            var listRows = await multi.ReadAsync<PromoCandidateRow>();
            var list = new List<PromoCandidateDto>();
            foreach (var r in listRows)
            {
                list.Add(new PromoCandidateDto(
                    r.promotion_id, r.code, r.name, r.type, r.value,
                    r.max_discount, r.min_order_amount, r.apply_scope,
                    r.is_exclusive, r.is_stackable, r.priority,
                    r.estimated_discount, r.status_text, r.reason ?? string.Empty
                ));
            }

            var best = await multi.ReadFirstOrDefaultAsync<BestRow>();
            PromoBestDto? bestDto = best is null
                ? null
                : new PromoBestDto(best.chosen_promotion_id, best.total_discount, best.apply_scope);

            return new PromoQuoteResponse(list, bestDto);
        }

        public async Task<PromoReserveResponse> ReserveAsync(long userIdOr0, string? deviceUuid, PromoReserveRequest req, CancellationToken ct = default)
        {
            if (req is null) throw new ArgumentNullException(nameof(req));
            if (req.OrderSubtotal < 0 || req.ShippingFee < 0)
                return new PromoReserveResponse(false, -12, "Giá trị không hợp lệ.");

            // 🚩 CHỈNH: nếu chưa có orderId thì coi như bỏ qua (no-op), trả success
            if (req.OrderId <= 0)
            {
                _log.LogInformation("Reserve skipped: no order yet (code={Code}, device={Device})", req.Code, deviceUuid ?? req.DeviceUuid);
                return new PromoReserveResponse(true, 1, "skip_reserve_no_order");
            }

            if (req.DiscountAmount <= 0)
                return new PromoReserveResponse(false, -11, "discount_amount phải > 0.");

            var itemsJson = BuildItemsJson(req.Items ?? new());

            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            var p = new DynamicParameters();
            p.Add("@order_id", req.OrderId, DbType.Int64);
            p.Add("@promotion_id", req.PromotionId, DbType.Int64);
            p.Add("@code", req.Code, DbType.String);
            p.Add("@user_info_id", req.UserInfoId, DbType.Int64);
            p.Add("@device_uuid", deviceUuid ?? req.DeviceUuid, DbType.String);
            p.Add("@channel", req.Channel, DbType.Byte);
            p.Add("@order_subtotal", req.OrderSubtotal, DbType.Decimal);
            p.Add("@shipping_fee", req.ShippingFee, DbType.Decimal);
            p.Add("@discount_amount", req.DiscountAmount, DbType.Decimal);
            p.Add("@items_json", itemsJson, DbType.String);
            p.Add("@ip", req.Ip, DbType.String);
            p.Add("@now_utc", DateTime.UtcNow, DbType.DateTime2);

            p.Add("@out_code", dbType: DbType.Int32, direction: ParameterDirection.Output);
            p.Add("@out_msg", dbType: DbType.String, size: 200, direction: ParameterDirection.Output);
            p.Add("@Code", dbType: DbType.Int32, direction: ParameterDirection.Output);
            p.Add("@Msg", dbType: DbType.String, size: 200, direction: ParameterDirection.Output);

            try
            {
                await con.ExecuteAsync(new CommandDefinition(
                    "dbo.usp_promotion_reserve", p, commandType: CommandType.StoredProcedure, cancellationToken: ct, commandTimeout: 30));

                int code = p.Get<int?>("@out_code") ?? p.Get<int?>("@Code") ?? 0;
                string msg = p.Get<string?>("@out_msg") ?? p.Get<string?>("@Msg") ?? string.Empty;
                return new PromoReserveResponse(code > 0, code, msg);
            }
            catch (SqlException ex)
            {
                _log.LogError(ex, "Reserve promotion failed for order {OrderId}", req.OrderId);
                return new PromoReserveResponse(false, -500, "Lỗi hệ thống khi giữ khuyến mãi.");
            }
        }


        public async Task<PromoReleaseResponse> ReleaseAsync(PromoReleaseRequest req, CancellationToken ct = default)
        {
            if (req is null) throw new ArgumentNullException(nameof(req));

            // 🚩 CHỈNH: nếu chưa có orderId thì coi như đã release 0 bản ghi, success
            if (req.OrderId <= 0)
                return new PromoReleaseResponse(true, 0, 1, "skip_release_no_order");

            using var con = _dbFactory.Create();
            await ((DbConnection)con).OpenAsync(ct);

            var p = new DynamicParameters();
            p.Add("@order_id", req.OrderId, DbType.Int64);
            p.Add("@reason", req.Reason, DbType.String);
            p.Add("@now_utc", DateTime.UtcNow, DbType.DateTime2);

            p.Add("@released_count", dbType: DbType.Int32, direction: ParameterDirection.Output);
            p.Add("@out_code", dbType: DbType.Int32, direction: ParameterDirection.Output);
            p.Add("@out_msg", dbType: DbType.String, size: 200, direction: ParameterDirection.Output);
            // fallback legacy names
            p.Add("@ReleasedCount", dbType: DbType.Int32, direction: ParameterDirection.Output);
            p.Add("@Code", dbType: DbType.Int32, direction: ParameterDirection.Output);
            p.Add("@Msg", dbType: DbType.String, size: 200, direction: ParameterDirection.Output);

            var cmd = new CommandDefinition(
                "dbo.usp_promotion_release",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct,
                commandTimeout: 30
            );

            try
            {
                await con.ExecuteAsync(cmd);

                int released = p.Get<int?>("@released_count") ?? p.Get<int?>("@ReleasedCount") ?? 0;
                int code = p.Get<int?>("@out_code") ?? p.Get<int?>("@Code") ?? 0;
                string msg = p.Get<string?>("@out_msg") ?? p.Get<string?>("@Msg") ?? string.Empty;

                return new PromoReleaseResponse(code > 0, released, code, msg);
            }
            catch (SqlException ex)
            {
                _log.LogError(ex, "Release promotion failed for order {OrderId}", req.OrderId);
                return new PromoReleaseResponse(false, 0, -500, "Lỗi hệ thống khi release khuyến mãi.");
            }
        }

        // ======= Local mapping rows from SP =======
        private sealed class PromoCandidateRow
        {
            public long promotion_id { get; init; }
            public string code { get; init; } = "";
            public string name { get; init; } = "";
            public byte type { get; init; }
            public decimal value { get; init; }
            public decimal? max_discount { get; init; }
            public decimal? min_order_amount { get; init; }
            public byte apply_scope { get; init; }
            public bool is_exclusive { get; init; }
            public bool is_stackable { get; init; }
            public byte priority { get; init; }
            public decimal estimated_discount { get; init; }
            public string status_text { get; init; } = "";
            public string? reason { get; init; }
        }

        private sealed class BestRow
        {
            public long? chosen_promotion_id { get; init; }
            public decimal total_discount { get; init; }
            public byte? apply_scope { get; init; }
        }
    }
}
