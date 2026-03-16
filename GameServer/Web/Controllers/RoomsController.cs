using GameServer.Systems;
using Microsoft.AspNetCore.Mvc;

namespace GameServer.Web.Controllers;

[ApiController]
[Route("rooms")]
public class RoomsController : ControllerBase
{
    private const int MaxMessageLength = 512;

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<RoomDto>), 200)]
    public IActionResult Get()
    {
        var rooms = LobbySystem.Instance.GetRooms()
            .Select(r => new RoomDto(r.RoomId, r.Name, r.PlayerCount, r.Capacity))
            .ToList();
        return Ok(rooms);
    }

    [HttpPost("{id}/broadcast")]
    public IActionResult Broadcast([FromRoute] ulong id, [FromBody] BroadcastBody body)
    {
        if (string.IsNullOrWhiteSpace(body.Message))
            return BadRequest(new { error = "Message is required" });

        if (body.Message.Length > MaxMessageLength)
            return BadRequest(new { error = $"Message exceeds maximum length of {MaxMessageLength}" });

        var room = LobbySystem.Instance.TryGetRoom(id);
        if (room == null)
            return NotFound(new { error = $"Room {id} not found" });

        if (!room.Broadcast(body.Message))
            return NotFound(new { error = $"Room {id} is no longer active" });

        return Ok(new { success = true });
    }
}
