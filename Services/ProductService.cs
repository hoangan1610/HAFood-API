using System.Data;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;

namespace HAShop.Api.Services;

public interface IProductService
{
    Task<PagedResult<ProductListItemDto>> SearchAsync(
        string? q, long? categoryId, string? brand, decimal? minPrice, decimal? maxPrice,
        byte? status, bool onlyInStock, int page, int pageSize, string? sort,
        int? wFrom, int? wTo, string? wJson, CancellationToken ct);

    Task<ProductDetailDto?> GetAsync(long id, CancellationToken ct);
    Task<ProductBySkuDto?> GetBySkuAsync(string sku, CancellationToken ct);
    Task<IReadOnlyList<VariantDto>> GetVariantsAsync(long productId, CancellationToken ct);
    Task<VariantWithProductDto?> GetVariantAsync(long id, CancellationToken ct);
    Task<VariantWithProductDto?> GetVariantBySkuAsync(string sku, CancellationToken ct);
    Task<bool> AdjustStockAsync(long variantId, int delta, CancellationToken ct);
    Task<IReadOnlyList<CategoryTreeDto>> GetCategoriesTreeAsync(CancellationToken ct);
    Task<IReadOnlyList<BrandItemDto>> GetBrandsAsync(CancellationToken ct);
    Task<IReadOnlyList<string>> SuggestAsync(string term, CancellationToken ct);
}

public class ProductService(ISqlConnectionFactory db) : IProductService
{
    public async Task<PagedResult<ProductListItemDto>> SearchAsync(
        string? q, long? categoryId, string? brand, decimal? minPrice, decimal? maxPrice,
        byte? status, bool onlyInStock, int page, int pageSize, string? sort,
        int? wFrom, int? wTo, string? wJson, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@q", q);
        p.Add("@category_id", categoryId);
        p.Add("@brand", brand);
        p.Add("@min_price", minPrice);
        p.Add("@max_price", maxPrice);
        p.Add("@status", status);
        p.Add("@only_in_stock", onlyInStock);
        p.Add("@page", page);
        p.Add("@page_size", pageSize);
        p.Add("@sort", sort);
        p.Add("@w_from", wFrom);
        p.Add("@w_to", wTo);
        p.Add("@w_json", wJson);
        p.Add("@total_count", dbType: DbType.Int32, direction: ParameterDirection.Output);

        var items = (await con.QueryAsync<ProductListItemDto>(
            new CommandDefinition("dbo.usp_products_search", p, commandType: CommandType.StoredProcedure, cancellationToken: ct)))
            .AsList();

        var total = p.Get<int>("@total_count");
        return new PagedResult<ProductListItemDto>(items, total, page, pageSize);
    }

    // Row type an toàn cho phần header của usp_product_get (tránh cast (string) vào null)
    private sealed class ProductRow
    {
        public long id { get; set; }
        public long category_id { get; set; }
        public string brand_name { get; set; } = "";
        public string name { get; set; } = "";
        public string? tag { get; set; }
        public string? product_keyword { get; set; }
        public string? detail { get; set; }
        public string? image_product { get; set; }
        public string? expiry { get; set; }
        public byte status { get; set; }
        public DateTime created_at { get; set; }
        public DateTime updated_at { get; set; }
    }

    public async Task<ProductDetailDto?> GetAsync(long id, CancellationToken ct)
    {
        using var con = db.Create();
        using var multi = await con.QueryMultipleAsync(new CommandDefinition(
            "dbo.usp_product_get",
            new { product_id = id },
            commandType: CommandType.StoredProcedure,
            cancellationToken: ct));

        var row = await multi.ReadFirstOrDefaultAsync<ProductRow>();
        if (row is null) return null;

        var variants = (await multi.ReadAsync<VariantDto>()).ToList();

        return new ProductDetailDto(
            Id: row.id,
            Category_Id: row.category_id,
            Brand_Name: row.brand_name ?? "",
            Name: row.name ?? "",
            Tag: row.tag,
            Product_Keyword: row.product_keyword,
            Detail: row.detail,
            Image_Product: string.IsNullOrWhiteSpace(row.image_product) ? "" : row.image_product,
            Expiry: row.expiry,
            Status: row.status,
            Created_At: row.created_at,
            Updated_At: row.updated_at,
            Variants: variants
        );
    }

    public async Task<ProductBySkuDto?> GetBySkuAsync(string sku, CancellationToken ct)
    {
        using var con = db.Create();
        return await con.QueryFirstOrDefaultAsync<ProductBySkuDto>(
            new CommandDefinition("dbo.usp_product_by_sku", new { sku }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<VariantDto>> GetVariantsAsync(long productId, CancellationToken ct)
    {
        using var con = db.Create();
        var list = await con.QueryAsync<VariantDto>(
            new CommandDefinition("dbo.usp_product_variants", new { product_id = productId }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return list.AsList();
    }

    public async Task<VariantWithProductDto?> GetVariantAsync(long id, CancellationToken ct)
    {
        using var con = db.Create();
        return await con.QueryFirstOrDefaultAsync<VariantWithProductDto>(
            new CommandDefinition("dbo.usp_variant_get", new { variant_id = id }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<VariantWithProductDto?> GetVariantBySkuAsync(string sku, CancellationToken ct)
    {
        using var con = db.Create();
        return await con.QueryFirstOrDefaultAsync<VariantWithProductDto>(
            new CommandDefinition("dbo.usp_variant_by_sku", new { sku }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
    }

    public async Task<bool> AdjustStockAsync(long variantId, int delta, CancellationToken ct)
    {
        using var con = db.Create();
        var p = new DynamicParameters();
        p.Add("@variant_id", variantId);
        p.Add("@delta", delta);
        p.Add("@ok", dbType: DbType.Boolean, direction: ParameterDirection.Output);

        try
        {
            await con.ExecuteAsync(new CommandDefinition(
                "dbo.usp_variant_adjust_stock", p, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        }
        catch (SqlException ex) when (ex.Number == 50091)
        {
            throw new KeyNotFoundException("VARIANT_NOT_FOUND");
        }
        catch (SqlException ex) when (ex.Number == 50092)
        {
            throw new InvalidOperationException("STOCK_NEGATIVE_REJECTED");
        }

        return p.Get<bool>("@ok");
    }

    public async Task<IReadOnlyList<CategoryTreeDto>> GetCategoriesTreeAsync(CancellationToken ct)
    {
        using var con = db.Create();
        var list = await con.QueryAsync<CategoryTreeDto>(
            new CommandDefinition("dbo.usp_categories_tree", commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return list.AsList();
    }

    public async Task<IReadOnlyList<BrandItemDto>> GetBrandsAsync(CancellationToken ct)
    {
        using var con = db.Create();
        var list = await con.QueryAsync<BrandItemDto>(
            new CommandDefinition("dbo.usp_brands", commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return list.AsList();
    }

    public async Task<IReadOnlyList<string>> SuggestAsync(string term, CancellationToken ct)
    {
        using var con = db.Create();
        var list = await con.QueryAsync<string>(
            new CommandDefinition("dbo.usp_search_suggest", new { term }, commandType: CommandType.StoredProcedure, cancellationToken: ct));
        return list.AsList();
    }
}
