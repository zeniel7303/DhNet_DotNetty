using System.Diagnostics;
using System.Reflection;
using Common;
using GameServer.Systems;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace GameServer.Web.Controllers;

[ApiController]
[Route("server-info")]
public class ServerInfoController(IConfiguration config) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ServerInfoDto), 200)]
    public IActionResult Get()
    {
        var uptime = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();
        var uptimeStr = $"{(int)uptime.TotalDays}d {uptime.Hours}h {uptime.Minutes}m {uptime.Seconds}s";

        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0";

        var settings = config.GetSection("GameServer").Get<GameServerSettings>();

        return Ok(new ServerInfoDto(
            Version: version,
            Uptime: uptimeStr,
            GamePort: settings?.GamePort ?? 7777,
            WebPort: settings?.WebPort ?? 8080,
            MaxPlayers: PlayerSystem.Instance.MaxPlayers));
    }
}
