using HAShop.Api.Services;
using Microsoft.AspNetCore.Mvc;

public class IntentController : ControllerBase
{
    private readonly IIntentService _intent;

    public IntentController(IIntentService intent)
    {
        _intent = intent;
    }

    [HttpGet("predict")]
    public IActionResult Predict([FromQuery] string q)
    {
        var (intent, conf) = _intent.PredictIntent(q);
        return Ok(new { intent, confidence = conf });
    }
}
