using Common.Server;
using GameServer.Systems;
using Microsoft.AspNetCore.Mvc;

namespace GameServer.Web.Controllers;

[ApiController]
[Route("lobbies")]
public class LobbiesController : ControllerBase
{

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<LobbyDetailDto>), 200)]
    public IActionResult Get()
    {
        var lobbies = LobbySystem.Instance.GetLobbyList()
            .Select(l => new LobbyDetailDto(l.LobbyId, l.PlayerCount, l.MaxCapacity, l.IsFull))
            .ToList();
        return Ok(lobbies);
    }

    [HttpPost("{id}/broadcast")]
    [ProducesResponseType(200)]
    [ProducesResponseType(400)]
    [ProducesResponseType(404)]
    public IActionResult Broadcast([FromRoute] ulong id, [FromBody] BroadcastBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Message))
            return BadRequest(new { error = "Message is required" });

        if (body.Message.Length > ServerConstants.MaxMessageLength)
            return BadRequest(new { error = $"Message exceeds maximum length of {ServerConstants.MaxMessageLength}" });

        var lobby = LobbySystem.Instance.TryGetLobby(id);
        if (lobby == null)
            return NotFound(new { error = $"Lobby {id} not found" });

        if (!lobby.Broadcast(body.Message))
            return NotFound(new { error = $"Lobby {id} is no longer active" });

        return Ok(new { success = true });
    }
}
