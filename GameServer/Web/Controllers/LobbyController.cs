using GameServer.Systems;
using Microsoft.AspNetCore.Mvc;

namespace GameServer.Web.Controllers;

[ApiController]
[Route("lobby")]
public class LobbyController : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(LobbyDto), 200)]
    public IActionResult Get()
    {
        var count = LobbySystem.Instance.GetTotalPlayerCount();
        return Ok(new LobbyDto(count));
    }
}
