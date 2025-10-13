using System.Security.Claims;
using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api")]
public class CartController(ICartService cart) : ControllerBase
{
    private long? GetUserIdFromJwt()
    {
        var uid = User?.FindFirstValue("uid") ?? User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(uid, out var v) ? v : null;
    }

    /// GET /cart?device_id=123
    /// - Nếu có JWT -> ưu tiên user; device_id (nếu có) sẽ được merge vào user cart trong SP.
    [HttpGet("cart")]
    public async Task<ActionResult<CartViewDto>> GetCart([FromQuery(Name = "device_id")] long? deviceId, CancellationToken ct)
    {
        var userId = GetUserIdFromJwt();
        try
        {
            var data = await cart.GetOrCreateAndViewAsync(userId, deviceId, ct);
            return Ok(data);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { code = "CART_NOT_FOUND", message = "Không tìm thấy giỏ." });
        }
    }

    /// POST /cart/items
    /// Body: { "variant_id": 11, "quantity": 2, "name_variant": "...", "price_variant": 47000, "image_variant": "..." }
    /// Query: device_id=...
    [HttpPost("cart/items")]
    public async Task<IActionResult> AddItem([FromQuery(Name = "device_id")] long? deviceId, [FromBody] CartAddRequest req, CancellationToken ct)
    {
        var userId = GetUserIdFromJwt();
        var cartId = await cart.GetOrCreateCartIdAsync(userId, deviceId, ct);

        try
        {
            await cart.AddOrIncrementAsync(cartId, req.Variant_Id, req.Quantity, req.Name_Variant, req.Price_Variant, req.Image_Variant, ct);
            var view = await cart.ViewAsync(cartId, ct);
            return Ok(view);
        }
        catch (InvalidOperationException ex) when (ex.Message == "INVALID_QUANTITY")
        {
            return BadRequest(new { code = "INVALID_QUANTITY", message = "Số lượng phải >= 1." });
        }
    }

    /// PUT /cart/items/{variantId}
    /// Body: { "quantity": 3 }
    [HttpPut("cart/items/{variantId:long}")]
    public async Task<IActionResult> UpdateQty(long variantId, [FromQuery(Name = "device_id")] long? deviceId, [FromBody] CartUpdateQtyRequest req, CancellationToken ct)
    {
        var userId = GetUserIdFromJwt();
        var cartId = await cart.GetOrCreateCartIdAsync(userId, deviceId, ct);

        try
        {
            await cart.UpdateQuantityAsync(cartId, variantId, req.Quantity, ct);
            var view = await cart.ViewAsync(cartId, ct);
            return Ok(view);
        }
        catch (InvalidOperationException ex) when (ex.Message == "INVALID_QUANTITY")
        {
            return BadRequest(new { code = "INVALID_QUANTITY", message = "Số lượng phải >= 1." });
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { code = "CART_ITEM_NOT_FOUND", message = "Không tìm thấy item trong giỏ." });
        }
    }

    /// DELETE /cart/items/{variantId}
    [HttpDelete("cart/items/{variantId:long}")]
    public async Task<IActionResult> RemoveItem(long variantId, [FromQuery(Name = "device_id")] long? deviceId, CancellationToken ct)
    {
        var userId = GetUserIdFromJwt();
        var cartId = await cart.GetOrCreateCartIdAsync(userId, deviceId, ct);

        try
        {
            await cart.RemoveItemAsync(cartId, variantId, ct);
            var view = await cart.ViewAsync(cartId, ct);
            return Ok(view);
        }
        catch (KeyNotFoundException)
        {
            return NotFound(new { code = "CART_ITEM_NOT_FOUND", message = "Không tìm thấy item trong giỏ." });
        }
    }

    /// DELETE /cart/items
    [HttpDelete("cart/items")]
    public async Task<IActionResult> Clear([FromQuery(Name = "device_id")] long? deviceId, CancellationToken ct)
    {
        var userId = GetUserIdFromJwt();
        var cartId = await cart.GetOrCreateCartIdAsync(userId, deviceId, ct);
        await cart.ClearAsync(cartId, ct);
        var view = await cart.ViewAsync(cartId, ct);
        return Ok(view);
    }
}
