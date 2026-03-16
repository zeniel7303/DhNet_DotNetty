using Common.Logging;
using DotNetty.Transport.Channels;
using GameClient.Controllers;
using GameClient.Stats;
using GameServer.Protocol;

namespace GameClient.Scenarios;

/// <summary>
/// 로그인 → 로비채팅 → 룸 입장 → 룸 채팅 → 룸 퇴장 시나리오 (기존 흐름)
/// </summary>
public class RoomScenario : ILoadTestScenario
{
    private readonly string _namePrefix;

    public RoomScenario(string namePrefix) => _namePrefix = namePrefix;

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
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"로그인 성공: {ctx.PlayerName} (Id={ctx.PlayerId})");
                await channel.WriteAndFlushAsync(new GamePacket
                {
                    ReqLobbyChat = new ReqLobbyChat { Message = "안녕하세요 로비!" }
                });
                LoadTestStats.IncrementSent();
                break;

            case GamePacket.PayloadOneofCase.NotiLobbyChat:
                if (!ctx.RoomEnterSent)
                {
                    ctx.RoomEnterSent = true;
                    await channel.WriteAndFlushAsync(new GamePacket { ReqRoomEnter = new ReqRoomEnter() });
                    LoadTestStats.IncrementSent();
                }
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
                    ReqRoomChat = new ReqRoomChat { Message = $"안녕하세요! (from {ctx.PlayerName})" }
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
                        await Task.Delay(3000);
                        await channel.WriteAndFlushAsync(new GamePacket { ReqRoomExit = new ReqRoomExit() });
                        LoadTestStats.IncrementSent();
                    });
                }
                break;

            case GamePacket.PayloadOneofCase.ResRoomExit:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", "룸 퇴장 완료");
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
        GameLogger.Info($"Client[{ctx.ClientIndex}]", "연결 해제됨");
    }
}
