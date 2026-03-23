using Common.Logging;
using DotNetty.Transport.Channels;
using TestClient.Controllers;
using TestClient.Stats;
using GameServer.Protocol;

namespace TestClient.Scenarios;

/// <summary>
/// 로그인 → 로비 채팅 주기적 반복 시나리오 (룸 입장 없음, 부하 테스트용)
/// </summary>
public class LobbyChatScenario : BaseRoomScenario
{
    private readonly int _chatIntervalMs;
    private readonly CancellationToken _token;

    public LobbyChatScenario(string namePrefix, int chatIntervalMs, CancellationToken token)
        : base(namePrefix)
    {
        _chatIntervalMs = chatIntervalMs;
        _token = token;
    }

    protected override Task OnLoginSuccessAsync(IChannel channel, ClientContext ctx)
    {
        _ = StartPeriodicChatAsync(channel, ctx);
        return Task.CompletedTask;
    }

    protected override Task<bool> OnOtherPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
    {
        if (packet.PayloadCase == GamePacket.PayloadOneofCase.NotiLobbyChat)
        {
            LoadTestStats.IncrementChatReceived();
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    private async Task StartPeriodicChatAsync(IChannel channel, ClientContext ctx)
    {
        try
        {
            while (!_token.IsCancellationRequested && channel.Active)
            {
                await Task.Delay(_chatIntervalMs, _token);
                if (!channel.Active) break;
                await channel.WriteAndFlushAsync(new GamePacket
                {
                    ReqLobbyChat = new ReqLobbyChat { Message = $"[{ctx.PlayerName}] lobby ping" }
                });
                LoadTestStats.IncrementSent();
                LoadTestStats.IncrementChatSent();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            GameLogger.Error($"Client[{ctx.ClientIndex}]", "채팅 루프 오류", ex);
            LoadTestStats.IncrementErrors();
        }
    }
}
