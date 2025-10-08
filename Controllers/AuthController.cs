using Microsoft.AspNetCore.Mvc;
using HAShop.Api.DTOs;
using HAShop.Api.Services;
using System.Threading;
using System.Threading.Tasks;
using System;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(IAuthService auth) : ControllerBase
{
    [HttpPost("register")]
public async Task<ActionResult<RegisterResponse>> Register([FromBody] RegisterRequest req, CancellationToken ct)
{
    try
    {
        var res = await auth.RegisterAsync(req, ct);
        return Created(string.Empty, res);
    }
    catch (InvalidOperationException ex) when (ex.Message == "EMAIL_EXISTS")
    {
        return Conflict(new { code = "EMAIL_EXISTS", message = "Email đã tồn tại" });
    }
    catch (Exception ex)
    {
        return Problem(title: "USER_REGISTER_FAILED", detail: ex.Message);
    }
}
[HttpPost("verify-otp/registration")]
public async Task<ActionResult<VerifyOtpResponse>> VerifyRegistrationOtp(
    [FromBody] VerifyRegistrationOtpRequest req, CancellationToken ct)
{
    try
    {
        var res = await auth.VerifyRegistrationOtpAsync(req, ct);
        return res.Verified ? Ok(res) : BadRequest(res);
    }
    catch (Exception ex)
    {
        return Problem(title: "OTP_VERIFY_FAILED", detail: ex.Message);
    }
}

}
