using Common.Logging;
using DotNetty.Transport.Channels;
using TestClient.Controllers;
using TestClient.Stats;
using GameServer.Protocol;

namespace TestClient.Scenarios;

/// <summary>
/// 로그인 → 룸 목록 주기적 조회 시나리오 (룸 입장 없음, 로비 허브 부하 테스트용)
/// </summary>
public class LobbyChatScenario : BaseRoomScenario
{
    private readonly int _intervalMs;
    private readonly CancellationToken _token;

    public LobbyChatScenario(string namePrefix, int chatIntervalMs, CancellationToken token)
        : base(namePrefix)
    {
        _intervalMs = chatIntervalMs;
        _token = token;
    }

    protected override Task OnLoginSuccessAsync(IChannel channel, ClientContext ctx)
    {
        _ = StartPeriodicRoomListAsync(channel, ctx);
        return Task.CompletedTask;
    }

    protected override Task<bool> OnOtherPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
    {
        if (packet.PayloadCase == GamePacket.PayloadOneofCase.ResRoomList)
        {
            LoadTestStats.IncrementChatReceived(); // 조회 응답 카운트 재활용
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    private async Task StartPeriodicRoomListAsync(IChannel channel, ClientContext ctx)
    {
        try
        {
            while (!_token.IsCancellationRequested && channel.Active)
            {
                await Task.Delay(_intervalMs, _token);
                if (!channel.Active) break;
                await channel.WriteAndFlushAsync(new GamePacket { ReqRoomList = new ReqRoomList() });
                LoadTestStats.IncrementSent();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            GameLogger.Error($"Client[{ctx.ClientIndex}]", "룸 목록 조회 루프 오류", ex);
            LoadTestStats.IncrementErrors();
        }
    }
}
