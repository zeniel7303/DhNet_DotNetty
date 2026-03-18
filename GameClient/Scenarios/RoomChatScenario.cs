using Common.Logging;
using DotNetty.Transport.Channels;
using GameClient.Controllers;
using GameClient.Stats;
using GameServer.Protocol;

namespace GameClient.Scenarios;

/// <summary>
/// 로그인 → 룸 입장 → 룸 채팅 반복 (Ctrl+C까지, 퇴장 없음)
/// </summary>
public class RoomChatScenario : ILoadTestScenario
{
    private readonly string _namePrefix;
    private readonly int _chatIntervalMs;
    private readonly CancellationToken _token;

    public RoomChatScenario(string namePrefix, int chatIntervalMs, CancellationToken token)
    {
        _namePrefix = namePrefix;
        _chatIntervalMs = chatIntervalMs;
        _token = token;
    }

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
                ctx.RoomEnterSent = true;
                await channel.WriteAndFlushAsync(new GamePacket { ReqRoomEnter = new ReqRoomEnter() });
                LoadTestStats.IncrementSent();
                break;

            case GamePacket.PayloadOneofCase.ResRoomEnter:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 입장 결과: Success={packet.ResRoomEnter.Success}");
                if (!packet.ResRoomEnter.Success)
                    ctx.ScheduleRoomEnterRetry(channel);
                break;

            case GamePacket.PayloadOneofCase.NotiRoomEnter:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 입장 알림: {packet.NotiRoomEnter.PlayerName}");
                if (packet.NotiRoomEnter.PlayerId == ctx.PlayerId)
                {
                    GameLogger.Info($"Client[{ctx.ClientIndex}]", "내 입장 확인 → 채팅 루프 시작");
                    _ = StartPeriodicRoomChatAsync(channel, ctx);
                }
                break;

            case GamePacket.PayloadOneofCase.NotiRoomChat:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 채팅: {packet.NotiRoomChat.PlayerName}: {packet.NotiRoomChat.Message}");
                break;

            case GamePacket.PayloadOneofCase.NotiSystem:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"[시스템] {packet.NotiSystem.Message}");
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

    private async Task StartPeriodicRoomChatAsync(IChannel channel, ClientContext ctx)
    {
        try
        {
            while (!_token.IsCancellationRequested && channel.Active)
            {
                await Task.Delay(_chatIntervalMs, _token);
                if (!channel.Active)
                {
                    break;
                }
                await channel.WriteAndFlushAsync(new GamePacket
                {
                    ReqRoomChat = new ReqRoomChat { Message = $"[{ctx.PlayerName}] room ping" }
                });
                LoadTestStats.IncrementSent();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            GameLogger.Error($"Client[{ctx.ClientIndex}]", "룸 채팅 루프 오류", ex);
            LoadTestStats.IncrementErrors();
        }
    }
}
