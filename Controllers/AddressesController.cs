using System.Security.Claims;
using HAShop.Api.DTOs;
using HAShop.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/addresses")]
[Authorize(AuthenticationSchemes = JwtBearerDefaults.AuthenticationScheme)]
public class AddressesController : ControllerBase
{
    private readonly IAddressService _svc;

    public AddressesController(IAddressService svc) => _svc = svc;

    // GET /api/addresses/me?onlyActive=true
    [HttpGet("me")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiOkResponse<IReadOnlyList<AddressDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiOkResponse<IReadOnlyList<AddressDto>>>> ListMine([FromQuery] bool onlyActive = true, CancellationToken ct = default)
    {
        var userId = GetUserIdFromClaims(User);
        if (userId is null)
            return Unauthorized(BuildProblem("UNAUTHENTICATED", StatusCodes.Status401Unauthorized));

        var rows = await _svc.ListAsync(userId.Value, onlyActive, ct);
        return Ok(new ApiOkResponse<IReadOnlyList<AddressDto>>(true, rows));
    }

    // POST /api/addresses
    [HttpPost]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiOkResponse<AddressDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiOkResponse<AddressDto>>> Add([FromBody] AddressCreateRequest body, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims(User);
        if (userId is null)
            return Unauthorized(BuildProblem("UNAUTHENTICATED", StatusCodes.Status401Unauthorized));

        if (string.IsNullOrWhiteSpace(body.FullAddress))
            return BadRequest(BuildProblem("FULL_ADDRESS_REQUIRED", StatusCodes.Status400BadRequest));

        try
        {
            var dto = await _svc.AddAsync(userId.Value, body, ct);
            return Ok(new ApiOkResponse<AddressDto>(true, dto, "Đã thêm địa chỉ."));
        }
        catch (SqlException ex) when (ex.Number == 50402)
        {
            return NotFound(BuildProblem("USER_INFO_NOT_FOUND", StatusCodes.Status404NotFound, ex.Message));
        }
        catch (SqlException ex) when (ex.Number == 50404)
        {
            return Conflict(BuildProblem("PHONE_ALREADY_IN_USE", StatusCodes.Status409Conflict, ex.Message));
        }
    }

    // PUT /api/addresses/{id}
    [HttpPut("{id:long}")]
    [Consumes("application/json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiOkResponse<AddressDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiOkResponse<AddressDto>>> Update(long id, [FromBody] AddressUpdateRequest body, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims(User);
        if (userId is null)
            return Unauthorized(BuildProblem("UNAUTHENTICATED", StatusCodes.Status401Unauthorized));

        try
        {
            var dto = await _svc.UpdateAsync(userId.Value, id, body, ct);
            return Ok(new ApiOkResponse<AddressDto>(true, dto, "Đã cập nhật địa chỉ."));
        }
        catch (SqlException ex) when (ex.Number == 50401)
        {
            return NotFound(BuildProblem("ADDRESS_NOT_FOUND", StatusCodes.Status404NotFound, ex.Message));
        }
    }

    // DELETE /api/addresses/{id}
    [HttpDelete("{id:long}")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiOkResponse<IReadOnlyList<AddressDto>>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiOkResponse<IReadOnlyList<AddressDto>>>> DeleteSoft(long id, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims(User);
        if (userId is null)
            return Unauthorized(BuildProblem("UNAUTHENTICATED", StatusCodes.Status401Unauthorized));

        var rows = await _svc.DeleteSoftAsync(userId.Value, id, ct);
        return Ok(new ApiOkResponse<IReadOnlyList<AddressDto>>(true, rows, "Đã xoá địa chỉ."));
    }

    // PUT /api/addresses/{id}/default
    [HttpPut("{id:long}/default")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(ApiOkResponse<AddressDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<ApiOkResponse<AddressDto>>> SetDefault(long id, CancellationToken ct)
    {
        var userId = GetUserIdFromClaims(User);
        if (userId is null)
            return Unauthorized(BuildProblem("UNAUTHENTICATED", StatusCodes.Status401Unauthorized));

        try
        {
            var dto = await _svc.SetDefaultAsync(userId.Value, id, ct);
            return Ok(new ApiOkResponse<AddressDto>(true, dto, "Đã đặt làm mặc định."));
        }
        catch (SqlException ex) when (ex.Number == 50401)
        {
            return NotFound(BuildProblem("ADDRESS_NOT_FOUND_OR_INACTIVE", StatusCodes.Status404NotFound, ex.Message));
        }
    }

    // ===== helpers =====
    private static long? GetUserIdFromClaims(ClaimsPrincipal user)
    {
        string? raw =
            user.FindFirstValue("sub") ??
            user.FindFirstValue("user_id") ??
            user.FindFirstValue(ClaimTypes.NameIdentifier) ??
            user.FindFirstValue("uid");

        return long.TryParse(raw, out var id) ? id : null;
    }

    private ProblemDetails BuildProblem(string code, int status, string? tech = null)
    {
        var pd = new ProblemDetails
        {
            Title = code,
            Detail = tech ?? code,
            Status = status,
            Type = "about:blank"
        };
        pd.Extensions["traceId"] = HttpContext?.TraceIdentifier ?? string.Empty;
        pd.Extensions["code"] = code;
        if (!string.IsNullOrWhiteSpace(tech))
            pd.Extensions["techMessage"] = tech;
        return pd;
    }
}
