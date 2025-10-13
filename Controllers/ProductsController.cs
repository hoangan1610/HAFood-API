using HAShop.Api.DTOs;
using HAShop.Api.Services;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api")]
public class ProductsController(IProductService repo) : ControllerBase
{
    // GET /products
    [HttpGet("products")]
    public async Task<ActionResult<PagedResult<ProductListItemDto>>> List(
        [FromQuery] string? q,
        [FromQuery(Name = "category_id")] long? categoryId,
        [FromQuery] string? brand,
        [FromQuery(Name = "min_price")] decimal? minPrice,
        [FromQuery(Name = "max_price")] decimal? maxPrice,
        [FromQuery] byte? status,
        [FromQuery(Name = "only_in_stock")] bool onlyInStock = false,
        [FromQuery] int page = 1,
        [FromQuery(Name = "page_size")] int pageSize = 20,
        [FromQuery] string? sort = "updated_at:desc",
        CancellationToken ct = default)
    {
        var res = await repo.SearchAsync(q, categoryId, brand, minPrice, maxPrice,
                                         status, onlyInStock, page, pageSize, sort, ct);
        return Ok(res);
    }

    // GET /products/{id}
    [HttpGet("products/{id:long}")]
    public async Task<ActionResult<ProductDetailDto>> GetProduct(long id, CancellationToken ct)
    {
        var data = await repo.GetAsync(id, ct);
        return data is null ? NotFound() : Ok(data);
    }

    // GET /products/by-sku/{sku}
    [HttpGet("products/by-sku/{sku}")]
    public async Task<ActionResult<ProductBySkuDto>> GetProductBySku(string sku, CancellationToken ct)
    {
        var data = await repo.GetBySkuAsync(sku, ct);
        return data is null ? NotFound() : Ok(data);
    }

    // GET /products/{id}/variants
    [HttpGet("products/{id:long}/variants")]
    public async Task<ActionResult<IReadOnlyList<VariantDto>>> GetProductVariants(long id, CancellationToken ct)
    {
        var list = await repo.GetVariantsAsync(id, ct);
        return Ok(list);
    }

    // GET /variants/{id}
    [HttpGet("variants/{id:long}")]
    public async Task<ActionResult<VariantWithProductDto>> GetVariant(long id, CancellationToken ct)
    {
        var data = await repo.GetVariantAsync(id, ct);
        return data is null ? NotFound() : Ok(data);
    }

    // GET /variants/by-sku/{sku}
    [HttpGet("variants/by-sku/{sku}")]
    public async Task<ActionResult<VariantWithProductDto>> GetVariantBySku(string sku, CancellationToken ct)
    {
        var data = await repo.GetVariantBySkuAsync(sku, ct);
        return data is null ? NotFound() : Ok(data);
    }

    // POST /variants/{id}/adjust-stock
    [Authorize]
    [HttpPost("variants/{id:long}/adjust-stock")]
    public async Task<ActionResult<AdjustStockResponse>> AdjustStock(long id, [FromBody] AdjustStockRequest req, CancellationToken ct)
    {
        try
        {
            var ok = await repo.AdjustStockAsync(id, req.Delta, ct);
            return Ok(new AdjustStockResponse(ok));
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { code = "VARIANT_NOT_FOUND", message = "Không tìm thấy biến thể." });
        }
        catch (InvalidOperationException ex) when (ex.Message == "STOCK_NEGATIVE_REJECTED")
        {
            return BadRequest(new { code = "STOCK_NEGATIVE_REJECTED", message = "Tồn kho không đủ để trừ." });
        }
    }

    // GET /categories/tree
    [HttpGet("categories/tree")]
    public async Task<ActionResult<IReadOnlyList<CategoryTreeDto>>> CategoriesTree(CancellationToken ct)
    {
        var data = await repo.GetCategoriesTreeAsync(ct);
        return Ok(data);
    }

    // GET /brands
    [HttpGet("brands")]
    public async Task<ActionResult<IReadOnlyList<BrandItemDto>>> Brands(CancellationToken ct)
    {
        var data = await repo.GetBrandsAsync(ct);
        return Ok(data);
    }

    // GET /search/suggest?q=...
    [HttpGet("search/suggest")]
    public async Task<ActionResult<SuggestResponse>> Suggest([FromQuery(Name = "q")] string q, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) return Ok(new SuggestResponse([]));
        var items = await repo.SuggestAsync(q, ct);
        return Ok(new SuggestResponse(items));
    }
}
