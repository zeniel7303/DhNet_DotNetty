using Common.Logging;
using DotNetty.Transport.Channels;
using TestClient.Controllers;
using TestClient.Stats;
using GameServer.Protocol;

namespace TestClient.Scenarios;

/// <summary>
/// 로그인 → 룸 흐름 시나리오의 공통 로직을 담은 추상 기반 클래스.
/// ResLogin, ResRoomEnter, NotiSystem, default 처리를 공유하고
/// 시나리오별 분기는 OnLoginSuccessAsync / OnOtherPacketReceivedAsync로 위임한다.
/// </summary>
public abstract class BaseRoomScenario : ILoadTestScenario
{
    protected readonly string _namePrefix;

    protected BaseRoomScenario(string namePrefix) => _namePrefix = namePrefix;

    public async Task OnConnectedAsync(IChannel channel, ClientContext ctx)
    {
        LoadTestStats.IncrementConnected();
        var name = $"{_namePrefix}{ctx.ClientIndex}";
        GameLogger.Info($"Client[{ctx.ClientIndex}]", $"연결됨, 회원가입 시도: {name}");
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
                // SUCCESS: 신규 가입 / USERNAME_TAKEN: 이미 존재하는 봇 — 둘 다 로그인 진행
                if (packet.ResRegister.ErrorCode == ErrorCode.Success ||
                    packet.ResRegister.ErrorCode == ErrorCode.UsernameTaken)
                {
                    var name = $"{_namePrefix}{ctx.ClientIndex}";
                    await channel.WriteAndFlushAsync(new GamePacket
                    {
                        ReqLogin = new ReqLogin { Username = name, Password = ctx.Password }
                    });
                    LoadTestStats.IncrementSent();
                }
                else
                {
                    GameLogger.Warn($"Client[{ctx.ClientIndex}]", $"회원가입 실패: {packet.ResRegister.ErrorCode}");
                }
                break;

            case GamePacket.PayloadOneofCase.ResLogin:
                if (packet.ResLogin.ErrorCode != ErrorCode.Success)
                {
                    GameLogger.Warn($"Client[{ctx.ClientIndex}]", $"로그인 실패: {packet.ResLogin.ErrorCode}");
                    return;
                }
                ctx.PlayerId   = packet.ResLogin.PlayerId;
                ctx.PlayerName = packet.ResLogin.PlayerName;
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"로그인 성공: {ctx.PlayerName} (Id={ctx.PlayerId})");
                await OnLoginSuccessAsync(channel, ctx);
                break;

            case GamePacket.PayloadOneofCase.ResRoomEnter:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 입장 결과: {packet.ResRoomEnter.ErrorCode}");
                if (packet.ResRoomEnter.ErrorCode == ErrorCode.Success)
                    await OnRoomEnterSuccessAsync(channel, ctx);
                else
                    ctx.ScheduleRoomEnterRetry(channel);
                break;

            case GamePacket.PayloadOneofCase.NotiSystem:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"[시스템] {packet.NotiSystem.Message}");
                break;

            default:
                if (!await OnOtherPacketReceivedAsync(channel, ctx, packet))
                    GameLogger.Warn($"Client[{ctx.ClientIndex}]", $"미처리 패킷: {packet.PayloadCase}");
                break;
        }
    }

    public virtual void OnDisconnected(ClientContext ctx)
    {
        LoadTestStats.IncrementDisconnected();
        GameLogger.Info($"Client[{ctx.ClientIndex}]", "연결 해제됨");
    }

    /// <summary>로그인 성공 직후 시나리오별 동작 (룸 입장 요청, 로비 채팅 등).</summary>
    protected abstract Task OnLoginSuccessAsync(IChannel channel, ClientContext ctx);

    /// <summary>룸 입장 성공(ResRoomEnter.Success) 직후 시나리오별 동작.</summary>
    protected virtual Task OnRoomEnterSuccessAsync(IChannel channel, ClientContext ctx) => Task.CompletedTask;

    /// <summary>
    /// ResLogin/ResRoomEnter/NotiSystem/default 이외의 패킷 처리.
    /// 처리했으면 true, 미처리 패킷이면 false 반환 (false 시 기반 클래스가 경고 로그 출력).
    /// </summary>
    protected virtual Task<bool> OnOtherPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
        => Task.FromResult(false);
}
