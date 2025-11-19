using HAShop.Api.Services;
using Microsoft.AspNetCore.Mvc;
using HAShop.Api.DTOs;

namespace HAShop.Api.Controllers
{
    [ApiController]
    [Route("api/shipping")]
    public class ShippingController : ControllerBase
    {
        private readonly IShippingService _svc;

        public ShippingController(IShippingService svc) => _svc = svc;

        [HttpPost("quote")]
        public async Task<ActionResult<ShippingQuoteResult>> Quote([FromBody] ShippingQuoteRequest body, CancellationToken ct)
        {
            var res = await _svc.QuoteAsync(
                body.CityCode,
                body.WardCode,
                body.Subtotal,
                body.TotalWeightGram,
                body.Channel,
                ct);

            return Ok(res);
        }
    }
}
