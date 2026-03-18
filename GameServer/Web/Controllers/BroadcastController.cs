using GameServer.Systems;
using Microsoft.AspNetCore.Mvc;

namespace GameServer.Web.Controllers;

[ApiController]
[Route("broadcast")]
public class BroadcastController : ControllerBase
{
    private const int MaxMessageLength = 512;

    [HttpPost]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    public IActionResult Broadcast([FromBody] BroadcastBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Message))
            return BadRequest(new { error = "Message is required" });

        if (body.Message.Length > MaxMessageLength)
            return BadRequest(new { error = $"Message exceeds maximum length of {MaxMessageLength}" });

        PlayerSystem.Instance.BroadcastAll(body.Message);
        return Ok(new { success = true, recipients = PlayerSystem.Instance.Count });
    }
}
