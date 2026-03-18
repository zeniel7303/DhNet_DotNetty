using GameServer.Systems;
using Microsoft.AspNetCore.Mvc;

namespace GameServer.Web.Controllers;

[ApiController]
[Route("players")]
public class PlayersController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IEnumerable<PlayerDto>), 200)]
    public IActionResult Get()
    {
        var players = PlayerSystem.Instance.GetAll()
            .Select(p =>
            {
                // Lobby/Room 속성을 각각 한 번만 읽어 OnDispose 경쟁 조건 방지
                var lobby = p.Lobby;
                var room  = p.Room;
                return new PlayerDto(
                    p.PlayerId,
                    p.Name,
                    lobby?.CurrentLobby?.LobbyId,
                    room?.CurrentRoom?.RoomId);
            })
            .ToList();
        return Ok(players);
    }

    [HttpPost("{id}/kick")]
    [ProducesResponseType(204)]
    [ProducesResponseType(404)]
    public IActionResult Kick([FromRoute] ulong id)
    {
        var player = PlayerSystem.Instance.TryGet(id);
        if (player == null)
            return NotFound(new { error = $"Player {id} not found" });

        player.DisconnectForNextTick();
        return NoContent();
    }
}
