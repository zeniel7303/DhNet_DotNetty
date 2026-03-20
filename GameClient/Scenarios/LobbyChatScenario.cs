using Common.Logging;
using DotNetty.Transport.Channels;
using GameClient.Controllers;
using GameClient.Stats;
using GameServer.Protocol;

namespace GameClient.Scenarios;

/// <summary>
/// 로그인 → 로비 채팅 주기적 반복 시나리오 (룸 입장 없음, 부하 테스트용)
/// </summary>
public class LobbyChatScenario : ILoadTestScenario
{
    private readonly string _namePrefix;
    private readonly int _chatIntervalMs;
    private readonly CancellationToken _token;

    public LobbyChatScenario(string namePrefix, int chatIntervalMs, CancellationToken token)
    {
        _namePrefix = namePrefix;
        _chatIntervalMs = chatIntervalMs;
        _token = token;
    }

    public async Task OnConnectedAsync(IChannel channel, ClientContext ctx)
    {
        LoadTestStats.IncrementConnected();
        var name = $"{_namePrefix}{ctx.ClientIndex}";
        await channel.WriteAndFlushAsync(new GamePacket
        {
            ReqLogin = new ReqLogin { PlayerName = name }
        });
        LoadTestStats.IncrementSent();
    }

    public async Task OnPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
    {
        LoadTestStats.IncrementReceived();
        if (packet.PayloadCase == GamePacket.PayloadOneofCase.ResLogin)
        {
            if (packet.ResLogin.ErrorCode != ErrorCode.Success)
            {
                GameLogger.Warn($"Client[{ctx.ClientIndex}]", $"로그인 실패: {packet.ResLogin.ErrorCode}");
                return;
            }
            ctx.PlayerId = packet.ResLogin.PlayerId;
            ctx.PlayerName = packet.ResLogin.PlayerName;
            _ = StartPeriodicChatAsync(channel, ctx);
        }
        else if (packet.PayloadCase == GamePacket.PayloadOneofCase.NotiLobbyChat)
        {
            LoadTestStats.IncrementChatReceived();
        }
        else if (packet.PayloadCase == GamePacket.PayloadOneofCase.NotiSystem)
        {
            GameLogger.Info($"Client[{ctx.ClientIndex}]", $"[시스템] {packet.NotiSystem.Message}");
        }
    }

    public void OnDisconnected(ClientContext ctx)
    {
        LoadTestStats.IncrementDisconnected();
    }

    private async Task StartPeriodicChatAsync(IChannel channel, ClientContext ctx)
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
