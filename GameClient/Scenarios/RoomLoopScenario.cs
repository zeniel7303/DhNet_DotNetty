using Common.Logging;
using DotNetty.Transport.Channels;
using GameClient.Controllers;
using GameClient.Stats;
using GameServer.Protocol;

namespace GameClient.Scenarios;

/// <summary>
/// 로그인 → (룸 입장 → 룸 채팅 → 퇴장) 무한 반복 (Ctrl+C까지)
/// </summary>
public class RoomLoopScenario : ILoadTestScenario
{
    private readonly string _namePrefix;

    public RoomLoopScenario(string namePrefix) => _namePrefix = namePrefix;

    public async Task OnConnectedAsync(IChannel channel, ClientContext ctx)
    {
        LoadTestStats.IncrementConnected();
        var name = $"{_namePrefix}{ctx.ClientIndex}";
        GameLogger.Info($"Client[{ctx.ClientIndex}]", $"연결됨, 로그인 시도: {name}");
        await channel.WriteAndFlushAsync(new GamePacket
        {
            ReqLogin = new ReqLogin { PlayerName = name }
        });
        LoadTestStats.IncrementSent();
    }

    public async Task OnPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
    {
        LoadTestStats.IncrementReceived();
        switch (packet.PayloadCase)
        {
            case GamePacket.PayloadOneofCase.ResLogin:
                if (packet.ResLogin.PlayerId == 0)
                {
                    GameLogger.Warn($"Client[{ctx.ClientIndex}]", "로그인 실패 (서버 거부)");
                    return;
                }
                ctx.PlayerId = packet.ResLogin.PlayerId;
                ctx.PlayerName = packet.ResLogin.PlayerName;
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"로그인 성공: {ctx.PlayerName} → 룸 입장 요청");
                await EnterRoomAsync(channel, ctx);
                break;

            case GamePacket.PayloadOneofCase.ResRoomEnter:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 입장 결과: Success={packet.ResRoomEnter.Success}");
                if (!packet.ResRoomEnter.Success)
                    ctx.ScheduleRoomEnterRetry(channel);
                break;

            case GamePacket.PayloadOneofCase.NotiRoomEnter:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 입장 알림: {packet.NotiRoomEnter.PlayerName}");
                await channel.WriteAndFlushAsync(new GamePacket
                {
                    ReqRoomChat = new ReqRoomChat { Message = $"안녕! (loop #{ctx.RoomLoopCount + 1}, {ctx.PlayerName})" }
                });
                LoadTestStats.IncrementSent();
                break;

            case GamePacket.PayloadOneofCase.NotiRoomChat:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 채팅: {packet.NotiRoomChat.PlayerName}: {packet.NotiRoomChat.Message}");
                if (!ctx.RoomExitScheduled)
                {
                    ctx.RoomExitScheduled = true;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(2000);
                        await channel.WriteAndFlushAsync(new GamePacket { ReqRoomExit = new ReqRoomExit() });
                        LoadTestStats.IncrementSent();
                    });
                }
                break;

            case GamePacket.PayloadOneofCase.ResRoomExit:
                ctx.RoomLoopCount++;
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 퇴장 완료 (총 {ctx.RoomLoopCount}회) → 1초 후 재입장");
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1000);
                    if (!channel.Active)
                    {
                        return;
                    }
                    ResetRoomState(ctx);
                    await EnterRoomAsync(channel, ctx);
                });
                break;

            case GamePacket.PayloadOneofCase.NotiRoomExit:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 퇴장 알림: {packet.NotiRoomExit.PlayerName}");
                break;

            default:
                GameLogger.Warn($"Client[{ctx.ClientIndex}]", $"미처리 패킷: {packet.PayloadCase}");
                break;
        }
    }

    public void OnDisconnected(ClientContext ctx)
    {
        LoadTestStats.IncrementDisconnected();
        GameLogger.Info($"Client[{ctx.ClientIndex}]", $"연결 해제됨 (총 룸 사이클: {ctx.RoomLoopCount}회)");
    }

    private static async Task EnterRoomAsync(IChannel channel, ClientContext ctx)
    {
        ctx.RoomEnterSent = true;
        await channel.WriteAndFlushAsync(new GamePacket { ReqRoomEnter = new ReqRoomEnter() });
        LoadTestStats.IncrementSent();
    }

    private static void ResetRoomState(ClientContext ctx)
    {
        ctx.RoomEnterSent = false;
        ctx.RoomExitScheduled = false;
        ctx.RoomEnterRetryCount = 0;
    }
}
