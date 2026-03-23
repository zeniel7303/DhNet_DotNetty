using Common.Logging;
using DotNetty.Transport.Channels;
using TestClient.Controllers;
using TestClient.Stats;
using GameServer.Protocol;

namespace TestClient.Scenarios;

/// <summary>
/// 스트레스 테스트: 접속 → 로그인 → 룸 입장 → 채팅 N회 → 룸 퇴장 → 접속 해제 → 재접속 무한 반복.
/// <para>사용법: --scenario reconnect-stress --clients 1000 [--reconnect-delay 2000] [--room-cycles 0] [--chat-count 3]</para>
/// </summary>
public class ReconnectStressScenario : ILoadTestScenario
{
    private readonly string _namePrefix;
    private readonly int _chatCount;
    private readonly int _maxCycles;
    private readonly CancellationToken _token;

    // 외부 루프가 ChannelInactive 완료를 기다리기 위한 시그널
    private TaskCompletionSource _disconnectTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IsFinished { get; private set; }

    public ReconnectStressScenario(string namePrefix, int chatCount, int maxCycles, CancellationToken token)
    {
        _namePrefix = namePrefix;
        _chatCount  = chatCount;
        _maxCycles  = maxCycles;
        _token      = token;
    }

    /// <summary>새 연결 루프 시작 전 disconnectTcs를 리셋합니다.</summary>
    public void BeginCycle() =>
        _disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>현재 채널의 ChannelInactive 이벤트를 기다립니다.</summary>
    public Task WaitForDisconnectAsync() => _disconnectTcs.Task;

    public async Task OnConnectedAsync(IChannel channel, ClientContext ctx)
    {
        ctx.ResetForReconnect();
        LoadTestStats.IncrementConnected();
        var name = $"{_namePrefix}{ctx.ClientIndex}";
        ctx.PlayerName = name;

        GameLogger.Info($"Client[{ctx.ClientIndex}]", $"접속 완료 (재접속 {ctx.ReconnectCount}회차) → 회원가입 시도: {name}");
        await channel.WriteAndFlushAsync(new GamePacket
        {
            ReqRegister = new ReqRegister { Username = name, Password = ctx.Password }
        });
        LoadTestStats.IncrementSent();
    }

    public async Task OnPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
    {
        LoadTestStats.IncrementReceived();

        switch (packet.PayloadCase)
        {
            case GamePacket.PayloadOneofCase.ResRegister:
                // SUCCESS: 신규 가입 / USERNAME_TAKEN: 재접속 봇 — 둘 다 로그인 진행
                if (packet.ResRegister.ErrorCode == ErrorCode.Success ||
                    packet.ResRegister.ErrorCode == ErrorCode.UsernameTaken)
                {
                    await channel.WriteAndFlushAsync(new GamePacket
                    {
                        ReqLogin = new ReqLogin { Username = ctx.PlayerName, Password = ctx.Password }
                    });
                    LoadTestStats.IncrementSent();
                }
                else
                {
                    GameLogger.Warn($"Client[{ctx.ClientIndex}]", $"회원가입 실패: {packet.ResRegister.ErrorCode} → 연결 종료");
                    LoadTestStats.IncrementErrors();
                    await channel.CloseAsync();
                }
                break;

            case GamePacket.PayloadOneofCase.ResLogin:
                if (packet.ResLogin.ErrorCode != ErrorCode.Success)
                {
                    GameLogger.Warn($"Client[{ctx.ClientIndex}]", $"로그인 실패: {packet.ResLogin.ErrorCode} → 연결 종료");
                    LoadTestStats.IncrementErrors();
                    await channel.CloseAsync();
                    return;
                }
                ctx.PlayerId   = packet.ResLogin.PlayerId;
                ctx.PlayerName = packet.ResLogin.PlayerName;
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"로그인 성공: {ctx.PlayerName} → 룸 입장 요청");
                await channel.WriteAndFlushAsync(new GamePacket { ReqRoomEnter = new ReqRoomEnter() });
                LoadTestStats.IncrementSent();
                break;

            case GamePacket.PayloadOneofCase.ResRoomEnter:
                if (packet.ResRoomEnter.ErrorCode == ErrorCode.Success)
                {
                    GameLogger.Info($"Client[{ctx.ClientIndex}]", "룸 입장 성공 → 채팅 시작");
                    _ = RunRoomActivityAsync(channel, ctx);
                }
                else
                {
                    GameLogger.Warn($"Client[{ctx.ClientIndex}]", $"룸 입장 실패 (retry {ctx.RoomEnterRetryCount + 1}/5)");
                    ctx.ScheduleRoomEnterRetry(channel);
                }
                break;

            case GamePacket.PayloadOneofCase.ResRoomExit:
                ctx.TotalRoomCycles++;
                LoadTestStats.IncrementRoomCycle();
                GameLogger.Info($"Client[{ctx.ClientIndex}]",
                    $"룸 사이클 완료 (누적 {ctx.TotalRoomCycles}회) → 연결 종료 후 재접속 대기");

                if (_maxCycles > 0 && ctx.TotalRoomCycles >= _maxCycles)
                {
                    IsFinished = true;
                    GameLogger.Info($"Client[{ctx.ClientIndex}]", $"목표 사이클 달성 ({_maxCycles}회) → 종료");
                }
                await channel.CloseAsync();
                break;

            case GamePacket.PayloadOneofCase.NotiRoomChat:
            case GamePacket.PayloadOneofCase.NotiLobbyChat:
                LoadTestStats.IncrementChatReceived();
                break;

            case GamePacket.PayloadOneofCase.NotiRoomEnter:
            case GamePacket.PayloadOneofCase.NotiRoomExit:
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

        if (!IsFinished)
        {
            ctx.ReconnectCount++;
            LoadTestStats.IncrementReconnect();
        }

        GameLogger.Info($"Client[{ctx.ClientIndex}]",
            $"연결 해제 (재접속 누적 {ctx.ReconnectCount}회, 룸사이클 {ctx.TotalRoomCycles}회)");

        _disconnectTcs.TrySetResult();
    }

    /// <summary>
    /// 룸 입장 후 채팅 N회 전송 → 룸 퇴장 요청.
    /// ResRoomExit 수신 시 channel.CloseAsync()가 호출되어 재접속 루프가 시작된다.
    /// </summary>
    private async Task RunRoomActivityAsync(IChannel channel, ClientContext ctx)
    {
        try
        {
            for (int i = 0; i < _chatCount; i++)
            {
                if (!channel.Active || _token.IsCancellationRequested) return;
                await Task.Delay(300, _token).ConfigureAwait(false);
                await channel.WriteAndFlushAsync(new GamePacket
                {
                    ReqRoomChat = new ReqRoomChat { Message = $"[{ctx.ClientIndex}] #{i + 1}" }
                });
                LoadTestStats.IncrementSent();
                LoadTestStats.IncrementChatSent();
            }

            if (channel.Active && !_token.IsCancellationRequested)
            {
                await channel.WriteAndFlushAsync(new GamePacket { ReqRoomExit = new ReqRoomExit() });
                LoadTestStats.IncrementSent();
            }
        }
        catch (OperationCanceledException)
        {
            // 종료 시그널 — 정상 취소
        }
        catch (Exception ex)
        {
            GameLogger.Error($"Client[{ctx.ClientIndex}]", $"룸 활동 중 오류: {ex.Message}", ex);
            LoadTestStats.IncrementErrors();
        }
    }
}
