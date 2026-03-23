using Common.Logging;
using DotNetty.Transport.Channels;
using TestClient.Controllers;
using TestClient.Stats;
using GameServer.Protocol;

namespace TestClient.Scenarios;

/// <summary>
/// 로그인 → 룸 입장 → 룸 채팅 반복 (Ctrl+C까지, 퇴장 없음)
/// </summary>
public class RoomChatScenario(string namePrefix, int chatIntervalMs, CancellationToken token)
    : BaseRoomScenario(namePrefix)
{
    protected override async Task OnLoginSuccessAsync(IChannel channel, ClientContext ctx)
    {
        ctx.RoomEnterSent = true;
        await channel.WriteAndFlushAsync(new GamePacket { ReqRoomEnter = new ReqRoomEnter() });
        LoadTestStats.IncrementSent();
    }

    protected override async Task<bool> OnOtherPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
    {
        switch (packet.PayloadCase)
        {
            case GamePacket.PayloadOneofCase.NotiRoomEnter:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 입장 알림: {packet.NotiRoomEnter.PlayerName}");
                if (packet.NotiRoomEnter.PlayerId == ctx.PlayerId)
                {
                    GameLogger.Info($"Client[{ctx.ClientIndex}]", "내 입장 확인 → 채팅 루프 시작");
                    _ = StartPeriodicRoomChatAsync(channel, ctx);
                }
                return true;

            case GamePacket.PayloadOneofCase.NotiRoomChat:
                LoadTestStats.IncrementChatReceived();
                return true;

            default:
                return false;
        }
    }

    private async Task StartPeriodicRoomChatAsync(IChannel channel, ClientContext ctx)
    {
        try
        {
            while (!token.IsCancellationRequested && channel.Active)
            {
                await Task.Delay(chatIntervalMs, token);
                if (!channel.Active) break;
                await channel.WriteAndFlushAsync(new GamePacket
                {
                    ReqRoomChat = new ReqRoomChat { Message = $"[{ctx.PlayerName}] room ping" }
                });
                LoadTestStats.IncrementSent();
                LoadTestStats.IncrementChatSent();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GameLogger.Error($"Client[{ctx.ClientIndex}]", "룸 채팅 루프 오류", ex);
            LoadTestStats.IncrementErrors();
        }
    }
}
