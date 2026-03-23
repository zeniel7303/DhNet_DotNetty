using Common.Server;
using GameServer.Systems;
using Microsoft.AspNetCore.Mvc;

namespace GameServer.Web.Controllers;

[ApiController]
[Route("rooms")]
public class RoomsController : ControllerBase
{

    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<RoomDto>), 200)]
    public IActionResult Get()
    {
        var rooms = LobbySystem.Instance.GetAllRooms()
            .Select(r => new RoomDto(r.RoomId, r.Name, r.PlayerCount, r.Capacity))
            .ToList();
        return Ok(rooms);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(RoomDetailDto), 200)]
    [ProducesResponseType(404)]
    public IActionResult GetById([FromRoute] ulong id)
    {
        var room = LobbySystem.Instance.TryGetRoom(id);
        if (room == null)
            return NotFound(new { error = $"Room {id} not found" });

        var players = room.GetPlayerList()
            .Select(p => new PlayerInRoomDto(p.AccountId, p.Name))
            .ToArray();

        return Ok(new RoomDetailDto(room.RoomId, room.LobbyId, room.Name, room.PlayerCount, room.Capacity, players));
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

        var room = LobbySystem.Instance.TryGetRoom(id);
        if (room == null)
            return NotFound(new { error = $"Room {id} not found" });

        if (!room.Broadcast(body.Message))
            return NotFound(new { error = $"Room {id} is no longer active" });

        return Ok(new { success = true });
    }
}
