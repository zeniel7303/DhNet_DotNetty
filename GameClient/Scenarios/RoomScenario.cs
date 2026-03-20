using Common.Logging;
using DotNetty.Transport.Channels;
using GameClient.Controllers;
using GameClient.Stats;
using GameServer.Protocol;

namespace GameClient.Scenarios;

/// <summary>
/// 로그인 → 로비채팅 → 룸 입장 → 룸 채팅 → 룸 퇴장 시나리오 (기존 흐름)
/// </summary>
public class RoomScenario(string namePrefix) : BaseRoomScenario(namePrefix)
{
    protected override async Task OnLoginSuccessAsync(IChannel channel, ClientContext ctx)
    {
        await channel.WriteAndFlushAsync(new GamePacket
        {
            ReqLobbyChat = new ReqLobbyChat { Message = "안녕하세요 로비!" }
        });
        LoadTestStats.IncrementSent();
    }

    protected override async Task<bool> OnOtherPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
    {
        switch (packet.PayloadCase)
        {
            case GamePacket.PayloadOneofCase.NotiLobbyChat:
                if (!ctx.RoomEnterSent)
                {
                    ctx.RoomEnterSent = true;
                    await channel.WriteAndFlushAsync(new GamePacket { ReqRoomEnter = new ReqRoomEnter() });
                    LoadTestStats.IncrementSent();
                }
                return true;

            case GamePacket.PayloadOneofCase.NotiRoomEnter:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 입장 알림: {packet.NotiRoomEnter.PlayerName}");
                if (packet.NotiRoomEnter.PlayerId == ctx.PlayerId)
                {
                    await channel.WriteAndFlushAsync(new GamePacket
                    {
                        ReqRoomChat = new ReqRoomChat { Message = $"안녕하세요! (from {ctx.PlayerName})" }
                    });
                    LoadTestStats.IncrementSent();
                }
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
                            await Task.Delay(3000);
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
                GameLogger.Info($"Client[{ctx.ClientIndex}]", "룸 퇴장 완료");
                return true;

            case GamePacket.PayloadOneofCase.NotiRoomExit:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 퇴장 알림: {packet.NotiRoomExit.PlayerName}");
                return true;

            default:
                return false;
        }
    }
}
