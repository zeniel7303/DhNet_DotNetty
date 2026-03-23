using Common.Logging;
using DotNetty.Transport.Channels;
using GameClient.Controllers;
using GameClient.Stats;
using GameServer.Protocol;

namespace GameClient.Scenarios;

/// <summary>
/// 로그인 → 로비채팅 → 룸 입장 → 룸 채팅 → 퇴장 → 연결 해제
/// </summary>
public class RoomOnceScenario(string namePrefix) : BaseRoomScenario(namePrefix)
{
    protected override async Task OnLoginSuccessAsync(IChannel channel, ClientContext ctx)
    {
        await channel.WriteAndFlushAsync(GamePacket.From(new ReqLobbyChat { Message = "안녕하세요 로비!" }));
        LoadTestStats.IncrementSent();
    }

    protected override async Task OnRoomEnterSuccessAsync(IChannel channel, ClientContext ctx)
    {
        await channel.WriteAndFlushAsync(GamePacket.From(new ReqRoomChat { Message = $"안녕하세요! (from {ctx.PlayerName})" }));
        LoadTestStats.IncrementSent();
    }

    protected override async Task<bool> OnOtherPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
    {
        switch (packet.Type)
        {
            case PacketType.NotiLobbyChat:
                if (!ctx.RoomEnterSent)
                {
                    ctx.RoomEnterSent = true;
                    await channel.WriteAndFlushAsync(GamePacket.From(new ReqRoomEnter()));
                    LoadTestStats.IncrementSent();
                }
                return true;

            case PacketType.NotiRoomEnter:
            {
                var noti = packet.As<NotiRoomEnter>();
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 입장 알림: {noti.PlayerName}");
                return true;
            }

            case PacketType.NotiRoomChat:
                LoadTestStats.IncrementChatReceived();
                if (!ctx.RoomExitScheduled)
                {
                    ctx.RoomExitScheduled = true;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(3000);
                            await channel.WriteAndFlushAsync(GamePacket.From(new ReqRoomExit()));
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

            case PacketType.ResRoomExit:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", "룸 퇴장 완료 → 연결 해제");
                await channel.CloseAsync();
                return true;

            case PacketType.NotiRoomExit:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 퇴장 알림: {packet.As<NotiRoomExit>().PlayerName}");
                return true;

            default:
                return false;
        }
    }
}
