using System;
using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HAShop.Api.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;

namespace HAShop.Api.Services;

public interface IArticleMetricsService
{
    Task TrackArticleViewAsync(long articleId, HttpContext http, CancellationToken ct = default);
    Task TrackArticleProductClickAsync(long articleId, long productId, long? variantId, CancellationToken ct = default);
}

public sealed class ArticleMetricsService : IArticleMetricsService
{
    private readonly ISqlConnectionFactory _db;

    public ArticleMetricsService(ISqlConnectionFactory db) => _db = db;

    // VN date (UTC+7, VN không DST)
    private static DateTime GetVnDate() => DateTime.UtcNow.AddHours(7).Date;

    public async Task TrackArticleViewAsync(long articleId, HttpContext http, CancellationToken ct = default)
    {
        if (articleId <= 0) return;

        var viewDate = GetVnDate();
        var visitorKey = BuildVisitorKey(http);

        using var con = _db.Create();
        if (con.State != ConnectionState.Open) con.Open();

        // ✅ READPAST rule không liên quan ở đây, nhưng Serializable dễ lock -> dùng ReadCommitted
        using var tx = con.BeginTransaction(IsolationLevel.ReadCommitted);

        var incUnique = 0;

        try
        {
            const string sqlUnique = @"
INSERT INTO dbo.tbl_article_view_unique_daily(article_id, view_date, visitor_key, created_at_utc)
VALUES (@articleId, @viewDate, @visitorKey, SYSUTCDATETIME());";

            var rows = await con.ExecuteAsync(new CommandDefinition(
                sqlUnique,
                new { articleId, viewDate, visitorKey },
                transaction: tx,
                cancellationToken: ct));

            if (rows == 1) incUnique = 1;
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            incUnique = 0; // duplicate => đã tính unique
        }

        const string sqlUpsert = @"
UPDATE dbo.tbl_article_view_daily WITH (UPDLOCK, HOLDLOCK)
SET view_count = view_count + 1,
    unique_count = unique_count + @incUnique,
    updated_at_utc = SYSUTCDATETIME()
WHERE article_id = @articleId AND view_date = @viewDate;

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO dbo.tbl_article_view_daily(article_id, view_date, view_count, unique_count, updated_at_utc)
    VALUES (@articleId, @viewDate, 1, @incUnique, SYSUTCDATETIME());
END";

        await con.ExecuteAsync(new CommandDefinition(
            sqlUpsert,
            new { articleId, viewDate, incUnique },
            transaction: tx,
            cancellationToken: ct));

        tx.Commit();
    }

    public async Task TrackArticleProductClickAsync(long articleId, long productId, long? variantId, CancellationToken ct = default)
    {
        if (articleId <= 0 || productId <= 0) return;

        var clickDate = GetVnDate();
        var variantId0 = variantId ?? 0;

        using var con = _db.Create();
        if (con.State != ConnectionState.Open) con.Open();

        // ✅ Serializable không cần, dễ lock / gây lỗi READPAST chỗ khác -> ReadCommitted
        using var tx = con.BeginTransaction(IsolationLevel.ReadCommitted);

        const string sql = @"
UPDATE dbo.tbl_article_product_click_daily WITH (UPDLOCK, HOLDLOCK)
SET click_count = click_count + 1,
    updated_at_utc = SYSUTCDATETIME()
WHERE article_id = @articleId
  AND product_id = @productId
  AND ISNULL(variant_id, 0) = @variantId0
  AND click_date = @clickDate;

IF @@ROWCOUNT = 0
BEGIN
    INSERT INTO dbo.tbl_article_product_click_daily(article_id, product_id, variant_id, click_date, click_count, updated_at_utc)
    VALUES (@articleId, @productId, @variantId, @clickDate, 1, SYSUTCDATETIME());
END";

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                sql,
                new { articleId, productId, variantId, variantId0, clickDate },
                transaction: tx,
                cancellationToken: ct));

            tx.Commit();
        }
        catch (SqlException)
        {
            tx.Rollback(); // FK fail / deadlock… thì nuốt để không phá flow public
        }
    }

    private static byte[] BuildVisitorKey(HttpContext http)
    {
        var uid = http.User?.FindFirst("user_info_id")?.Value
               ?? http.User?.FindFirst("sub")?.Value;

        var ua = http.Request.Headers.UserAgent.ToString() ?? "";

        var ip = http.Request.Headers["X-Forwarded-For"].ToString();
        if (!string.IsNullOrWhiteSpace(ip) && ip.Contains(",")) ip = ip.Split(',')[0].Trim();
        if (string.IsNullOrWhiteSpace(ip)) ip = http.Connection.RemoteIpAddress?.ToString() ?? "";

        var seed = uid != null ? $"uid:{uid}|{ua}" : $"ip:{ip}|{ua}";

        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes(seed));
    }
}
