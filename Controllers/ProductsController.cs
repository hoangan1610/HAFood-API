using Microsoft.AspNetCore.Mvc;

namespace HAShop.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    [HttpGet]
    public IActionResult GetAll() => Ok(new[] {
        new { Id = 1, Name = "Snack 1", Price = 15000 },
        new { Id = 2, Name = "Snack 2", Price = 20000 }
    });
}
