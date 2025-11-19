using Dapper;
using HAShop.Api.Data;
using System.Data;
using System.Data.Common;

namespace HAShop.Api.Services
{
    public sealed record ShippingQuoteResult(
     long? ShippingZoneId,
     decimal ShippingFee,
     long? RuleId,
     string Message
 );


    public interface IShippingService
    {
        
        Task<ShippingQuoteResult> QuoteAsync(
            string? cityCode,
            string? wardCode,
            decimal subtotal,
            int totalWeightGram,
            byte channel,
            CancellationToken ct = default);
    }

    public sealed class ShippingService : IShippingService
    {
        private sealed class ShippingQuoteRow
        {
            public long? shipping_zone_id { get; init; }
            public decimal shipping_fee { get; init; }
            public long? rule_id { get; init; }
            public string? message { get; init; }
        }
        private readonly ISqlConnectionFactory _db;

        public ShippingService(ISqlConnectionFactory db)
        {
            _db = db;
        }

        public async Task<ShippingQuoteResult> QuoteAsync(
            string? cityCode,
            string? wardCode,
            decimal subtotal,
            int totalWeightGram,
            byte channel,
            CancellationToken ct = default)
        {
            using var con = _db.Create();
            await ((DbConnection)con).OpenAsync(ct);

            var p = new DynamicParameters();
            p.Add("@city_code", cityCode, DbType.String);
            p.Add("@ward_code", wardCode, DbType.String);
            p.Add("@subtotal", subtotal, DbType.Decimal);
            p.Add("@total_weight_g", totalWeightGram, DbType.Int32);
            p.Add("@channel", channel, DbType.Byte);
            p.Add("@now_utc", DateTime.UtcNow, DbType.DateTime2);

            var row = await con.QuerySingleAsync<ShippingQuoteRow>(new CommandDefinition(
                "dbo.usp_shipping_quote_cart",
                p,
                commandType: CommandType.StoredProcedure,
                cancellationToken: ct));

            return new ShippingQuoteResult(
                row.shipping_zone_id,
                row.shipping_fee,
                row.rule_id,
                row.message ?? string.Empty
            );
        }
    }

}
