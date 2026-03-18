using Common.Logging;
using GameServer.Database;
using GameServer.Systems;
using Microsoft.AspNetCore.Mvc;

namespace GameServer.Web.Controllers;

[ApiController]
[Route("stats")]
public class StatsController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(StatsDto), 200)]
    public IActionResult Get()
    {
        var lobbies = LobbySystem.Instance.GetLobbyList()
            .Select(l => new LobbyDetailDto(l.LobbyId, l.PlayerCount, l.MaxCapacity, l.IsFull))
            .ToArray();

        var stats = new StatsDto(
            OnlinePlayers: PlayerSystem.Instance.Count,
            MaxPlayers: PlayerSystem.Instance.MaxPlayers,
            ActiveRooms: LobbySystem.Instance.GetAllRooms().Count,
            TotalLobbies: lobbies.Length,
            Lobbies: lobbies);

        return Ok(stats);
    }

    [HttpGet("history")]
    [ProducesResponseType(typeof(IEnumerable<StatHistoryItemDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetHistory([FromQuery] int limit = 100)
    {
        try
        {
            var rows = await DatabaseSystem.Instance.GameLog.StatLogs.GetHistoryAsync(limit);
            var result = rows.Select(r => new StatHistoryItemDto(r.player_count, r.created_at));
            return Ok(result);
        }
        catch (Exception ex)
        {
            GameLogger.Error("StatsController", "stats/history 조회 실패", ex);
            return StatusCode(503, new { error = "Database unavailable" });
        }
    }
}
