using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using HAShop.Api.Data;

namespace HAShop.Api.Services
{
    public interface IArticlePublicService
    {
        Task<ArticlePublicDto?> GetBySlugAsync(string slug, CancellationToken ct = default);
        Task<(IReadOnlyList<ArticleListItemDto> Items, int Total)> ListAsync(string? q, int page, int pageSize, CancellationToken ct = default);
    }

    public sealed class ArticlePublicService : IArticlePublicService
    {
        private readonly ISqlConnectionFactory _db;

        public ArticlePublicService(ISqlConnectionFactory db) => _db = db;

        public async Task<ArticlePublicDto?> GetBySlugAsync(string slug, CancellationToken ct = default)
        {
            slug = (slug ?? "").Trim();
            if (slug.Length == 0) return null;

            using var con = _db.Create();

            // ✅ thêm content_html
            const string sqlArticle = @"
SELECT TOP 1
  a.id,
  a.title,
  a.slug,
  a.excerpt,
  a.cover_image_url,
  a.content_json,
  a.content_html,
  a.published_at_utc
FROM dbo.tbl_article a
WHERE a.is_deleted = 0
  AND a.status = 1
  AND a.slug = @slug
  AND a.published_at_utc IS NOT NULL
  AND a.published_at_utc <= SYSUTCDATETIME()
ORDER BY a.published_at_utc DESC;";

            var a = await con.QueryFirstOrDefaultAsync<ArticlePublicRow>(
                new CommandDefinition(sqlArticle, new { slug }, commandType: CommandType.Text, cancellationToken: ct));

            if (a == null) return null;

            const string sqlCards = @"
SELECT
  m.sort_order,
  p.id AS product_id,
  p.name AS product_name,
  p.image_product AS product_image,
  COALESCE(vmap.id, v0.id) AS variant_id,
  COALESCE(vmap.name, v0.name) AS variant_name,
  COALESCE(vmap.retail_price, v0.retail_price) AS retail_price,
  COALESCE(vmap.stock, v0.stock) AS stock
FROM dbo.tbl_article_product_map m
JOIN dbo.tbl_product_info p ON p.id = m.product_id AND p.is_deleted = 0 AND p.status = 1
LEFT JOIN dbo.tbl_product_variant vmap ON vmap.id = m.variant_id AND vmap.status = 1
OUTER APPLY (
    SELECT TOP 1 v.id, v.name, v.retail_price, v.stock
    FROM dbo.tbl_product_variant v
    WHERE v.product_id = p.id AND v.status = 1
    ORDER BY v.id DESC
) v0
WHERE m.article_id = @article_id
ORDER BY m.sort_order, m.id;";

            var cards = (await con.QueryAsync<ArticleCardDto>(
                new CommandDefinition(sqlCards, new { article_id = a.Id }, commandType: CommandType.Text, cancellationToken: ct)
            )).ToList();

            return new ArticlePublicDto
            {
                Id = a.Id,
                Title = a.Title ?? "",
                Slug = a.Slug ?? "",
                Excerpt = a.Excerpt,
                Cover_Image_Url = a.Cover_Image_Url,

                // ✅ mới: trả về HTML (TinyMCE)
                Content_Html = a.Content_Html,

                // vẫn giữ JSON để fallback
                Content_Json = a.Content_Json ?? "{\"time\":0,\"blocks\":[],\"version\":\"2\"}",

                Published_At_Utc = a.Published_At_Utc,
                Cards = cards
            };
        }

        public async Task<(IReadOnlyList<ArticleListItemDto> Items, int Total)> ListAsync(
            string? q, int page, int pageSize, CancellationToken ct = default)
        {
            if (page <= 0) page = 1;
            if (pageSize <= 0 || pageSize > 100) pageSize = 20;

            q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
            var skip = (page - 1) * pageSize;

            using var con = _db.Create();

            const string sql = @"
;WITH filtered AS (
  SELECT a.id, a.title, a.slug, a.excerpt, a.cover_image_url, a.published_at_utc
  FROM dbo.tbl_article a
  WHERE a.is_deleted = 0
    AND a.status = 1
    AND a.published_at_utc IS NOT NULL
    AND a.published_at_utc <= SYSUTCDATETIME()
    AND (
      @q IS NULL
      OR a.title LIKE N'%' + @q + N'%'
      OR a.slug  LIKE N'%' + @q + N'%'
      OR EXISTS (
        SELECT 1
        FROM dbo.tbl_article_search_text s
        WHERE s.article_id = a.id
          AND (
               s.title LIKE N'%' + @q + N'%'
            OR s.excerpt LIKE N'%' + @q + N'%'
            OR s.content_text LIKE N'%' + @q + N'%'
          )
      )
    )
)
SELECT *
FROM filtered
ORDER BY published_at_utc DESC
OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY;

;WITH filtered AS (
  SELECT a.id
  FROM dbo.tbl_article a
  WHERE a.is_deleted = 0
    AND a.status = 1
    AND a.published_at_utc IS NOT NULL
    AND a.published_at_utc <= SYSUTCDATETIME()
    AND (
      @q IS NULL
      OR a.title LIKE N'%' + @q + N'%'
      OR a.slug  LIKE N'%' + @q + N'%'
      OR EXISTS (
        SELECT 1
        FROM dbo.tbl_article_search_text s
        WHERE s.article_id = a.id
          AND (
               s.title LIKE N'%' + @q + N'%'
            OR s.excerpt LIKE N'%' + @q + N'%'
            OR s.content_text LIKE N'%' + @q + N'%'
          )
      )
    )
)
SELECT COUNT(1) FROM filtered;
";

            using var multi = await con.QueryMultipleAsync(
                new CommandDefinition(sql, new { q, skip, take = pageSize }, cancellationToken: ct));

            var items = (await multi.ReadAsync<ArticleListItemDto>()).ToList();
            var total = await multi.ReadFirstAsync<int>();

            return (items, total);
        }

        private sealed class ArticlePublicRow
        {
            public long Id { get; set; }
            public string? Title { get; set; }
            public string? Slug { get; set; }
            public string? Excerpt { get; set; }
            public string? Cover_Image_Url { get; set; }
            public string? Content_Json { get; set; }

            // ✅ mới
            public string? Content_Html { get; set; }

            public DateTime? Published_At_Utc { get; set; }
        }
    }

    public sealed class ArticlePublicDto
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public string Slug { get; set; } = "";
        public string? Excerpt { get; set; }
        public string? Cover_Image_Url { get; set; }

        // ✅ mới: trả HTML TinyMCE
        public string? Content_Html { get; set; }

        // fallback EditorJS
        public string Content_Json { get; set; } = "{\"time\":0,\"blocks\":[],\"version\":\"2\"}";
        public DateTime? Published_At_Utc { get; set; }
        public List<ArticleCardDto> Cards { get; set; } = new();
    }

    public sealed class ArticleCardDto
    {
        public int Sort_Order { get; set; }
        public long Product_Id { get; set; }
        public string Product_Name { get; set; } = "";
        public string? Product_Image { get; set; }
        public long? Variant_Id { get; set; }
        public string? Variant_Name { get; set; }
        public decimal? Retail_Price { get; set; }
        public int? Stock { get; set; }
    }

    public sealed class ArticleListItemDto
    {
        public long Id { get; set; }
        public string Title { get; set; } = "";
        public string Slug { get; set; } = "";
        public string? Excerpt { get; set; }
        public string? Cover_Image_Url { get; set; }
        public DateTime? Published_At_Utc { get; set; }
    }
}
