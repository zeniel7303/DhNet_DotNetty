using Common.Logging;
using GameServer.Database;
using Microsoft.AspNetCore.Mvc;

namespace GameServer.Web.Controllers;

[ApiController]
[Route("analytics")]
public class AnalyticsController : ControllerBase
{
    [HttpGet("chat-logs")]
    [ProducesResponseType(typeof(IEnumerable<ChatLogDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetChatLogs(
        [FromQuery] ulong? accountId,
        [FromQuery] ulong? roomId,
        [FromQuery] DateTime? startTime,
        [FromQuery] DateTime? endTime,
        [FromQuery] int limit = 100)
    {
        try
        {
            var rows = await DatabaseSystem.Instance.GameLog.ChatLogs.QueryAsync(accountId, roomId, startTime, endTime, limit);
            var result = rows.Select(r => new ChatLogDto(r.account_id, r.room_id, r.channel, r.message, r.created_at));
            return Ok(result);
        }
        catch (Exception ex)
        {
            GameLogger.Error("AnalyticsController", "chat-logs 조회 실패", ex);
            return StatusCode(503, new { error = "Database unavailable" });
        }
    }

    [HttpGet("login-logs")]
    [ProducesResponseType(typeof(IEnumerable<LoginLogDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetLoginLogs(
        [FromQuery] ulong? accountId,
        [FromQuery] DateTime? startTime,
        [FromQuery] DateTime? endTime,
        [FromQuery] int limit = 100)
    {
        try
        {
            var rows = await DatabaseSystem.Instance.GameLog.LoginLogs.QueryAsync(accountId, startTime, endTime, limit);
            var result = rows.Select(r => new LoginLogDto(r.account_id, r.player_name, r.ip_address, r.login_at, r.logout_at));
            return Ok(result);
        }
        catch (Exception ex)
        {
            GameLogger.Error("AnalyticsController", "login-logs 조회 실패", ex);
            return StatusCode(503, new { error = "Database unavailable" });
        }
    }

    [HttpGet("room-logs")]
    [ProducesResponseType(typeof(IEnumerable<RoomLogEntryDto>), 200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetRoomLogs(
        [FromQuery] ulong? accountId,
        [FromQuery] ulong? roomId,
        [FromQuery] string? action,
        [FromQuery] DateTime? startTime,
        [FromQuery] DateTime? endTime,
        [FromQuery] int limit = 100)
    {
        try
        {
            var rows = await DatabaseSystem.Instance.GameLog.RoomLogs.QueryAsync(accountId, roomId, action, startTime, endTime, limit);
            var result = rows.Select(r => new RoomLogEntryDto(r.account_id, r.room_id, r.action, r.created_at));
            return Ok(result);
        }
        catch (Exception ex)
        {
            GameLogger.Error("AnalyticsController", "room-logs 조회 실패", ex);
            return StatusCode(503, new { error = "Database unavailable" });
        }
    }
}
