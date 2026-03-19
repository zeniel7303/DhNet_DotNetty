using GameServer.Systems;
using Microsoft.AspNetCore.Mvc;

namespace GameServer.Web.Controllers;

[ApiController]
[Route("shutdown")]
public class ShutdownController : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(202)]
    [ProducesResponseType(409)]
    public IActionResult Shutdown()
    {
        if (ShutdownSystem.Instance.IsShutdownRequested)
            return Conflict(new { error = "Shutdown already in progress" });

        // 응답 전송 후 종료되도록 짧은 지연 후 CancellationToken 발행
        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            ShutdownSystem.Instance.Request();
        });

        return Accepted(new { message = "Shutdown initiated" });
    }
}
