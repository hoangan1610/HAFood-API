using Microsoft.AspNetCore.Mvc;

[ApiController]
[Route("api/chat")]
public class ChatController : ControllerBase
{
    private readonly IChatService _chat;
    public ChatController(IChatService chat) { _chat = chat; }

    public record AskRequest(string Message);

    [HttpPost("ask")]
    public async Task<IActionResult> Ask([FromBody] AskRequest req)
    {
        var ans = await _chat.AskAsync(req.Message, HttpContext.User);
        return Ok(new { answer = ans });
    }
}
