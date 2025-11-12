using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using System.Linq;

[ApiController]
[Route("api")]
public class CartController(ICartService cart, IDeviceService devices) : ControllerBase
{
    private long? GetUserIdFromJwt()
    {
        var uid = User?.FindFirstValue("uid") ?? User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return long.TryParse(uid, out var v) ? v : null;
    }

    // GET /cart?device_uuid=... | /cart?device_id=... | &channel=1
    [HttpGet("cart")]
    public async Task<ActionResult<CartViewDto>> GetCart(
        [FromQuery(Name = "device_uuid")] Guid? deviceUuid,
        [FromQuery(Name = "device_id")] long? deviceId,
        [FromQuery] int channel = 1,
        CancellationToken ct = default)
    {
        var userId = GetUserIdFromJwt();
        var devicePk = await devices.ResolveDevicePkAsync(deviceUuid, deviceId, userId, HttpContext, ct);
        if (userId is null && devicePk is null)
            return BadRequest(new { code = "MISSING_USER_OR_DEVICE", message = "Cần JWT hoặc device_uuid/device_id." });

        var data = await cart.GetOrCreateAndViewAsync(userId, devicePk, channel, null, ct);
        return Ok(data);
    }

    // POST /cart/items?device_uuid=... | device_id=... | &channel=1
    [HttpPost("cart/items")]
    public async Task<IActionResult> AddItem(
     [FromQuery(Name = "device_uuid")] Guid? deviceUuid,
     [FromQuery(Name = "device_id")] long? deviceId,
     [FromBody] CartAddRequest req,          // <== Đưa req lên trước
     [FromQuery] int channel = 1,            // <== Tham số tùy chọn đứng sau
     CancellationToken ct = default)
    {
        var userId = GetUserIdFromJwt();
        var devicePk = await devices.ResolveDevicePkAsync(deviceUuid, deviceId, userId, HttpContext, ct);
        if (userId is null && devicePk is null)
            return BadRequest(new { code = "MISSING_USER_OR_DEVICE", message = "Cần JWT hoặc device_uuid/device_id." });

        var cartId = await cart.GetOrCreateCartIdAsync(userId, devicePk, ct);
        try
        {
            await cart.AddOrIncrementAsync(cartId, req.Variant_Id, req.Quantity, req.Name_Variant, req.Price_Variant, req.Image_Variant, ct);
            var view = await cart.ViewAsync(cartId, channel, null, ct);
            return Ok(view);
        }
        catch (InvalidOperationException ex) when (ex.Message == "INVALID_QUANTITY")
        { return BadRequest(new { code = "INVALID_QUANTITY", message = "Số lượng phải >= 1." }); }
        catch (KeyNotFoundException ex) when (ex.Message == "VARIANT_NOT_FOUND")
        { return NotFound(new { code = "VARIANT_NOT_FOUND", message = "Biến thể không tồn tại." }); }
    }

    // PUT /cart/items/{variantId}?device_uuid=...&channel=1
    [HttpPut("cart/items/{variantId:long}")]
    public async Task<IActionResult> UpdateQty(
        long variantId,
        [FromQuery(Name = "device_uuid")] Guid? deviceUuid,
        [FromQuery(Name = "device_id")] long? deviceId,
        [FromBody] CartUpdateQtyRequest req,     // <== req trước
        [FromQuery] int channel = 1,             // <== optional sau
        CancellationToken ct = default)
    {
        var userId = GetUserIdFromJwt();
        var devicePk = await devices.ResolveDevicePkAsync(deviceUuid, deviceId, userId, HttpContext, ct);
        if (userId is null && devicePk is null)
            return BadRequest(new { code = "MISSING_USER_OR_DEVICE", message = "Cần JWT hoặc device_uuid/device_id." });

        var cartId = await cart.GetOrCreateCartIdAsync(userId, devicePk, ct);
        try
        {
            await cart.UpdateQuantityAsync(cartId, variantId, req.Quantity, ct);
            var view = await cart.ViewAsync(cartId, channel, null, ct);
            return Ok(view);
        }
        catch (InvalidOperationException ex) when (ex.Message == "INVALID_QUANTITY")
        { return BadRequest(new { code = "INVALID_QUANTITY", message = "Số lượng phải >= 1." }); }
        catch (KeyNotFoundException)
        { return NotFound(new { code = "CART_ITEM_NOT_FOUND", message = "Không tìm thấy item trong giỏ." }); }
    }

    // DELETE /cart/items/{variantId}?device_uuid=...&channel=1
    [HttpDelete("cart/items/{variantId:long}")]
    public async Task<IActionResult> RemoveItem(
        long variantId,
        [FromQuery(Name = "device_uuid")] Guid? deviceUuid,
        [FromQuery(Name = "device_id")] long? deviceId,
        [FromQuery] int channel = 1,
        CancellationToken ct = default)
    {
        var userId = GetUserIdFromJwt();
        var devicePk = await devices.ResolveDevicePkAsync(deviceUuid, deviceId, userId, HttpContext, ct);
        if (userId is null && devicePk is null)
            return BadRequest(new { code = "MISSING_USER_OR_DEVICE", message = "Cần JWT hoặc device_uuid/device_id." });

        var cartId = await cart.GetOrCreateCartIdAsync(userId, devicePk, ct);
        try
        {
            await cart.RemoveItemAsync(cartId, variantId, ct);
            var view = await cart.ViewAsync(cartId, channel, null, ct);
            return Ok(view);
        }
        catch (KeyNotFoundException)
        { return NotFound(new { code = "CART_ITEM_NOT_FOUND", message = "Không tìm thấy item trong giỏ." }); }
    }

    // DELETE /cart/items?device_uuid=...&channel=1
    [HttpDelete("cart/items")]
    public async Task<IActionResult> Clear(
        [FromQuery(Name = "device_uuid")] Guid? deviceUuid,
        [FromQuery(Name = "device_id")] long? deviceId,
        [FromQuery] int channel = 1,
        CancellationToken ct = default)
    {
        var userId = GetUserIdFromJwt();
        var devicePk = await devices.ResolveDevicePkAsync(deviceUuid, deviceId, userId, HttpContext, ct);
        if (userId is null && devicePk is null)
            return BadRequest(new { code = "MISSING_USER_OR_DEVICE", message = "Cần JWT hoặc device_uuid/device_id." });

        var cartId = await cart.GetOrCreateCartIdAsync(userId, devicePk, ct);
        await cart.ClearAsync(cartId, ct);
        var view = await cart.ViewAsync(cartId, channel, null, ct);
        return Ok(view);
    }

    // PUT /cart/lines/batch?compact=1&channel=1
    [HttpPut("cart/lines/batch")]
    public async Task<IActionResult> BatchSetQuantities(
        [FromBody] CartBatchRequest req,
        [FromQuery] int? compact,
        [FromQuery] int channel = 1,
        CancellationToken ct = default)
    {
        if (req?.Changes == null || req.Changes.Count == 0)
            return BadRequest(new { code = "EMPTY_CHANGES", message = "Danh sách thay đổi trống." });

        var userId = GetUserIdFromJwt();
        long cartId;

        if (req.Cart_Id.HasValue)
        {
            cartId = req.Cart_Id.Value;
        }
        else
        {
            Guid? deviceUuid = null;
            if (!string.IsNullOrWhiteSpace(req.Device_Uuid) && Guid.TryParse(req.Device_Uuid, out var g))
                deviceUuid = g;

            var devicePk = await devices.ResolveDevicePkAsync(deviceUuid, req.Device_Id, userId, HttpContext, ct);
            if (userId is null && devicePk is null)
                return BadRequest(new { code = "MISSING_USER_OR_DEVICE", message = "Cần JWT hoặc device_uuid/device_id." });

            cartId = await cart.GetOrCreateCartIdAsync(userId, devicePk, ct);
        }

        try
        {
            await cart.BatchSetQuantitiesByLineAsync(cartId, req.Changes, ct);

            if (compact == 1)
            {
                var ids = req.Changes.Select(x => x.Line_Id).ToArray();
                var resp = await cart.ViewCompactAsync(cartId, ids, channel, null, ct);
                return Ok(resp);
            }

            var view = await cart.ViewAsync(cartId, channel, null, ct);
            return Ok(view);
        }
        catch (InvalidOperationException ex) when (ex.Message == "INVALID_CHANGES_JSON")
        { return BadRequest(new { code = "INVALID_CHANGES_JSON", message = "Dữ liệu thay đổi không hợp lệ." }); }
        catch (KeyNotFoundException ex) when (ex.Message == "CART_LINE_NOT_FOUND")
        { return NotFound(new { code = "CART_LINE_NOT_FOUND", message = "line_id không tồn tại trong giỏ." }); }
    }

    // DELETE /cart/lines/{lineId}?device_uuid=...&channel=1
    [HttpDelete("cart/lines/{lineId:long}")]
    public async Task<IActionResult> RemoveLine(
        long lineId,
        [FromQuery(Name = "device_uuid")] Guid? deviceUuid,
        [FromQuery(Name = "device_id")] long? deviceId,
        [FromQuery] int channel = 1,
        CancellationToken ct = default)
    {
        var userId = GetUserIdFromJwt();
        var devicePk = await devices.ResolveDevicePkAsync(deviceUuid, deviceId, userId, HttpContext, ct);
        if (userId is null && devicePk is null)
            return BadRequest(new { code = "MISSING_USER_OR_DEVICE", message = "Cần JWT hoặc device_uuid/device_id." });

        var cartId = await cart.GetOrCreateCartIdAsync(userId, devicePk, ct);

        try
        {
            await cart.RemoveLineAsync(cartId, lineId, ct);
            var resp = await cart.ViewCompactAsync(cartId, new[] { lineId }, channel, null, ct);
            return Ok(resp);
        }
        catch (KeyNotFoundException)
        { return NotFound(new { code = "CART_LINE_NOT_FOUND", message = "line_id không tồn tại trong giỏ." }); }
    }
}
