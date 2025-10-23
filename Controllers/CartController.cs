using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

[ApiController]
[Route("api")]
public class CartController(ICartService cart, IDeviceService devices) : ControllerBase
{
    private long? GetUserIdFromJwt()
    {
        var uid = User?.FindFirstValue("uid") ?? User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(uid, out var v) ? v : null;
    }

    // GET /cart?device_uuid=... | /cart?device_id=...
    [HttpGet("cart")]
    public async Task<ActionResult<CartViewDto>> GetCart(
        [FromQuery(Name = "device_uuid")] Guid? deviceUuid,
        [FromQuery(Name = "device_id")] long? deviceId,
        CancellationToken ct)
    {
        var userId = GetUserIdFromJwt();
        var devicePk = await devices.ResolveDevicePkAsync(deviceUuid, deviceId, userId, HttpContext, ct);
        if (userId is null && devicePk is null)
            return BadRequest(new { code = "MISSING_USER_OR_DEVICE", message = "Cần JWT hoặc device_uuid/device_id." });

        var data = await cart.GetOrCreateAndViewAsync(userId, devicePk, ct);
        return Ok(data);
    }

    // POST /cart/items?device_uuid=... | device_id=...
    [HttpPost("cart/items")]
    public async Task<IActionResult> AddItem(
        [FromQuery(Name = "device_uuid")] Guid? deviceUuid,
        [FromQuery(Name = "device_id")] long? deviceId,
        [FromBody] CartAddRequest req,
        CancellationToken ct)
    {
        var userId = GetUserIdFromJwt();
        var devicePk = await devices.ResolveDevicePkAsync(deviceUuid, deviceId, userId, HttpContext, ct);
        if (userId is null && devicePk is null)
            return BadRequest(new { code = "MISSING_USER_OR_DEVICE", message = "Cần JWT hoặc device_uuid/device_id." });

        var cartId = await cart.GetOrCreateCartIdAsync(userId, devicePk, ct);
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
        catch (KeyNotFoundException ex) when (ex.Message == "VARIANT_NOT_FOUND")
        {
            return NotFound(new { code = "VARIANT_NOT_FOUND", message = "Biến thể không tồn tại." });
        }
    }

    // PUT /cart/items/{variantId}?device_uuid=...
    [HttpPut("cart/items/{variantId:long}")]
    public async Task<IActionResult> UpdateQty(
        long variantId,
        [FromQuery(Name = "device_uuid")] Guid? deviceUuid,
        [FromQuery(Name = "device_id")] long? deviceId,
        [FromBody] CartUpdateQtyRequest req,
        CancellationToken ct)
    {
        var userId = GetUserIdFromJwt();
        var devicePk = await devices.ResolveDevicePkAsync(deviceUuid, deviceId, userId, HttpContext, ct);
        if (userId is null && devicePk is null)
            return BadRequest(new { code = "MISSING_USER_OR_DEVICE", message = "Cần JWT hoặc device_uuid/device_id." });

        var cartId = await cart.GetOrCreateCartIdAsync(userId, devicePk, ct);
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

    // DELETE /cart/items/{variantId}?device_uuid=...
    [HttpDelete("cart/items/{variantId:long}")]
    public async Task<IActionResult> RemoveItem(
        long variantId,
        [FromQuery(Name = "device_uuid")] Guid? deviceUuid,
        [FromQuery(Name = "device_id")] long? deviceId,
        CancellationToken ct)
    {
        var userId = GetUserIdFromJwt();
        var devicePk = await devices.ResolveDevicePkAsync(deviceUuid, deviceId, userId, HttpContext, ct);
        if (userId is null && devicePk is null)
            return BadRequest(new { code = "MISSING_USER_OR_DEVICE", message = "Cần JWT hoặc device_uuid/device_id." });

        var cartId = await cart.GetOrCreateCartIdAsync(userId, devicePk, ct);
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

    // DELETE /cart/items?device_uuid=...
    [HttpDelete("cart/items")]
    public async Task<IActionResult> Clear(
        [FromQuery(Name = "device_uuid")] Guid? deviceUuid,
        [FromQuery(Name = "device_id")] long? deviceId,
        CancellationToken ct)
    {
        var userId = GetUserIdFromJwt();
        var devicePk = await devices.ResolveDevicePkAsync(deviceUuid, deviceId, userId, HttpContext, ct);
        if (userId is null && devicePk is null)
            return BadRequest(new { code = "MISSING_USER_OR_DEVICE", message = "Cần JWT hoặc device_uuid/device_id." });

        var cartId = await cart.GetOrCreateCartIdAsync(userId, devicePk, ct);
        await cart.ClearAsync(cartId, ct);
        var view = await cart.ViewAsync(cartId, ct);
        return Ok(view);
    }
}
