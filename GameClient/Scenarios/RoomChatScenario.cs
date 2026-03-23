using Common.Logging;
using DotNetty.Transport.Channels;
using GameClient.Controllers;
using GameClient.Stats;
using GameServer.Protocol;

namespace GameClient.Scenarios;

/// <summary>
/// 로그인 → 룸 입장 → 룸 채팅 반복 (Ctrl+C까지, 퇴장 없음)
/// </summary>
public class RoomChatScenario(string namePrefix, int chatIntervalMs, CancellationToken token)
    : BaseRoomScenario(namePrefix)
{
    protected override async Task OnLoginSuccessAsync(IChannel channel, ClientContext ctx)
    {
        ctx.RoomEnterSent = true;
        await channel.WriteAndFlushAsync(GamePacket.From(new ReqRoomEnter()));
        LoadTestStats.IncrementSent();
    }

    protected override async Task<bool> OnOtherPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
    {
        switch (packet.Type)
        {
            case PacketType.NotiRoomEnter:
            {
                var noti = packet.As<NotiRoomEnter>();
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 입장 알림: {noti.PlayerName}");
                if (noti.PlayerId == ctx.PlayerId)
                {
                    GameLogger.Info($"Client[{ctx.ClientIndex}]", "내 입장 확인 → 채팅 루프 시작");
                    _ = StartPeriodicRoomChatAsync(channel, ctx);
                }
                return true;
            }

            case PacketType.NotiRoomChat:
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
                await channel.WriteAndFlushAsync(GamePacket.From(
                    new ReqRoomChat { Message = $"[{ctx.PlayerName}] room ping" }));
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
