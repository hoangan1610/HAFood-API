using System.Data;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;

namespace HAShop.Api.Services;

public interface IPromotionService
{
    Task<PromotionPreviewResponse> PreviewAsync(
        long userInfoId, string code, long? cartId, decimal? subtotal, CancellationToken ct);
}

public class PromotionService(ISqlConnectionFactory db) : IPromotionService
{
    public async Task<PromotionPreviewResponse> PreviewAsync(
        long userInfoId, string code, long? cartId, decimal? subtotal, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return new PromotionPreviewResponse(false, 0, null, null, "Thiếu mã khuyến mãi.");

        using var con = db.Create();

        // Nếu có cartId nhưng không có subtotal -> BE tự tính
        if (cartId is not null && !subtotal.HasValue)
        {
            const string sumSql = @"
SELECT SUM(ROUND(COALESCE(i.price_variant, v.retail_price) * i.quantity, 2))
FROM dbo.tbl_cart_item i
JOIN dbo.tbl_product_variant v ON v.id = i.variant_id
WHERE i.cart_id = @cartId AND i.[status] = 1;";
            subtotal = await con.ExecuteScalarAsync<decimal?>(new CommandDefinition(
                sumSql, new { cartId }, cancellationToken: ct)) ?? 0m;
        }

        var p = new DynamicParameters();
        p.Add("@code", code);
        p.Add("@user_info_id", userInfoId);
        p.Add("@sub_total", subtotal ?? 0m);
        p.Add("@discount", dbType: DbType.Decimal, direction: ParameterDirection.Output, precision: 12, scale: 2);
        p.Add("@promotion_id", dbType: DbType.Int64, direction: ParameterDirection.Output);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_promotion_preview",
                p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number is 50301) // PROMO_NOT_FOUND
        { return new PromotionPreviewResponse(false, 0, null, code, "Mã không tồn tại."); }
        catch (SqlException ex) when (ex.Number is 50302) // PROMO_INACTIVE
        { return new PromotionPreviewResponse(false, 0, null, code, "Mã không còn hiệu lực."); }
        catch (SqlException ex) when (ex.Number is 50303) // PROMO_MIN_ORDER_NOT_MET
        { return new PromotionPreviewResponse(false, 0, null, code, "Chưa đạt giá trị đơn tối thiểu."); }
        catch (SqlException ex) when (ex.Number is 50304) // PROMO_NEW_USER_ONLY
        { return new PromotionPreviewResponse(false, 0, null, code, "Mã chỉ áp dụng cho khách hàng mới."); }
        catch (SqlException ex) when (ex.Number is 50305) // PROMO_EXHAUSTED
        { return new PromotionPreviewResponse(false, 0, null, code, "Mã đã hết lượt sử dụng."); }

        var discount = p.Get<decimal>("@discount");
        var pid = p.Get<long?>("@promotion_id");
        return new PromotionPreviewResponse(true, discount, pid, code, "OK");
    }
}
