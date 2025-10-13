
using System.Data;
namespace HAShop.Api.Services;
using Dapper;
using HAShop.Api.Data;
using HAShop.Api.DTOs;
using Microsoft.Data.SqlClient;
public interface IProductService
{
    Task<PagedResult<ProductListItemDto>> SearchAsync(
        string? q, long? categoryId, string? brand, decimal? minPrice, decimal? maxPrice,
        byte? status, bool onlyInStock, int page, int pageSize, string? sort, CancellationToken ct);

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
        byte? status, bool onlyInStock, int page, int pageSize, string? sort, CancellationToken ct)
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
        p.Add("@total_count", dbType: DbType.Int32, direction: ParameterDirection.Output);

        var items = (await con.QueryAsync<ProductListItemDto>(
            new CommandDefinition("dbo.usp_products_search", p, commandType: CommandType.StoredProcedure, cancellationToken: ct)))
            .AsList();

        var total = p.Get<int>("@total_count");
        return new PagedResult<ProductListItemDto>(items, total, page, pageSize);
    }

    public async Task<ProductDetailDto?> GetAsync(long id, CancellationToken ct)
    {
        using var con = db.Create();
        using var multi = await con.QueryMultipleAsync(new CommandDefinition(
            "dbo.usp_product_get", new { product_id = id }, commandType: CommandType.StoredProcedure, cancellationToken: ct));

        var prod = await multi.ReadFirstOrDefaultAsync<dynamic>();
        if (prod is null) return null;

        var variants = (await multi.ReadAsync<VariantDto>()).ToList();

        return new ProductDetailDto(
            Id: (long)prod.id,
            Category_Id: (long)prod.category_id,
            Brand_Name: (string)prod.brand_name,
            Name: (string)prod.name,
            Tag: (string?)prod.tag,
            Product_Keyword: (string?)prod.product_keyword,
            Detail: (string?)prod.detail,
            Image_Product: (string)prod.image_product,
            Expiry: (string?)prod.expiry,
            Status: (byte)prod.status,
            Created_At: (DateTime)prod.created_at,
            Updated_At: (DateTime)prod.updated_at,
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
            // map upstream (404)
            throw new KeyNotFoundException("VARIANT_NOT_FOUND");
        }
        catch (SqlException ex) when (ex.Number == 50092)
        {
            // map upstream (400)
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
