using Common.Logging;
using DotNetty.Transport.Channels;
using GameClient.Controllers;
using GameClient.Stats;
using GameServer.Protocol;

namespace GameClient.Scenarios;

/// <summary>
/// 로그인 → (룸 입장 → 룸 채팅 → 퇴장) 무한 반복 (Ctrl+C까지)
/// </summary>
public class RoomLoopScenario(string namePrefix) : BaseRoomScenario(namePrefix)
{
    protected override async Task OnLoginSuccessAsync(IChannel channel, ClientContext ctx)
    {
        GameLogger.Info($"Client[{ctx.ClientIndex}]", $"로그인 성공: {ctx.PlayerName} → 룸 입장 요청");
        await EnterRoomAsync(channel, ctx);
    }

    public override void OnDisconnected(ClientContext ctx)
    {
        LoadTestStats.IncrementDisconnected();
        GameLogger.Info($"Client[{ctx.ClientIndex}]", $"연결 해제됨 (총 룸 사이클: {ctx.RoomLoopCount}회)");
    }

    protected override async Task<bool> OnOtherPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
    {
        switch (packet.PayloadCase)
        {
            case GamePacket.PayloadOneofCase.NotiRoomEnter:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 입장 알림: {packet.NotiRoomEnter.PlayerName}");
                await channel.WriteAndFlushAsync(new GamePacket
                {
                    ReqRoomChat = new ReqRoomChat { Message = $"안녕! (loop #{ctx.RoomLoopCount + 1}, {ctx.PlayerName})" }
                });
                LoadTestStats.IncrementSent();
                LoadTestStats.IncrementChatSent();
                return true;

            case GamePacket.PayloadOneofCase.NotiRoomChat:
                LoadTestStats.IncrementChatReceived();
                if (!ctx.RoomExitScheduled)
                {
                    ctx.RoomExitScheduled = true;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(2000);
                            await channel.WriteAndFlushAsync(new GamePacket { ReqRoomExit = new ReqRoomExit() });
                            LoadTestStats.IncrementSent();
                        }
                        catch (Exception ex)
                        {
                            GameLogger.Error($"Client[{ctx.ClientIndex}]", "룸 퇴장 요청 오류", ex);
                            LoadTestStats.IncrementErrors();
                        }
                    });
                }
                return true;

            case GamePacket.PayloadOneofCase.ResRoomExit:
                ctx.RoomLoopCount++;
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 퇴장 완료 (총 {ctx.RoomLoopCount}회) → 1초 후 재입장");
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1000);
                        if (!channel.Active) return;
                        ResetRoomState(ctx);
                        await EnterRoomAsync(channel, ctx);
                    }
                    catch (Exception ex)
                    {
                        GameLogger.Error($"Client[{ctx.ClientIndex}]", "룸 재입장 오류", ex);
                        LoadTestStats.IncrementErrors();
                    }
                });
                return true;

            case GamePacket.PayloadOneofCase.NotiRoomExit:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 퇴장 알림: {packet.NotiRoomExit.PlayerName}");
                return true;

            default:
                return false;
        }
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
