using Common.Logging;
using DotNetty.Transport.Channels;
using GameClient.Controllers;
using GameClient.Stats;
using GameServer.Protocol;

namespace GameClient.Scenarios;

/// <summary>
/// 동일 계정으로 동시 다중 로그인을 시도하는 테스트 시나리오.
/// 모든 클라이언트가 동일한 username으로 동시에 ReqLogin을 전송한다.
/// 기대 결과: 1개만 SUCCESS, 나머지는 ALREADY_LOGGED_IN.
///
/// 사용법:
///   --scenario duplicate-login --clients 5 --delay 0 --prefix duptest
/// </summary>
public class DuplicateLoginScenario(string namePrefix) : ILoadTestScenario
{
    // 모든 클라이언트가 동일한 계정을 사용 — prefix 자체가 username
    private string Username => namePrefix;
    private const string Password = "0000";

    public async Task OnConnectedAsync(IChannel channel, ClientContext ctx)
    {
        LoadTestStats.IncrementConnected();
        GameLogger.Info($"DupTest[{ctx.ClientIndex}]", $"연결됨 → 회원가입 시도: {Username}");

        // 먼저 가입 시도: 이미 존재하면 USERNAME_TAKEN → 그냥 로그인으로 넘어감
        await channel.WriteAndFlushAsync(GamePacket.From(new ReqRegister { Username = Username, Password = Password }));
        LoadTestStats.IncrementSent();
    }

    public async Task OnPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
    {
        LoadTestStats.IncrementReceived();

        switch (packet.Type)
        {
            case PacketType.ResRegister:
            {
                var regCode = packet.As<ResRegister>().ErrorCode;
                if (regCode == ErrorCode.Success || regCode == ErrorCode.UsernameTaken)
                {
                    GameLogger.Info($"DupTest[{ctx.ClientIndex}]",
                        $"가입 결과: {regCode} → 로그인 시도: {Username}");
                    await channel.WriteAndFlushAsync(GamePacket.From(
                        new ReqLogin { Username = Username, Password = Password }));
                    LoadTestStats.IncrementSent();
                }
                else
                {
                    GameLogger.Warn($"DupTest[{ctx.ClientIndex}]", $"가입 실패: {regCode} → 연결 종료");
                    await channel.CloseAsync();
                }
                break;
            }

            case PacketType.ResLogin:
            {
                var res = packet.As<ResLogin>();
                if (res.ErrorCode == ErrorCode.Success)
                {
                    GameLogger.Info($"DupTest[{ctx.ClientIndex}]",
                        $"[SUCCESS] 로그인 성공: {res.PlayerName} (Id={res.PlayerId})");

                    // 3초 후 연결 해제 — 다른 클라이언트의 재시도 여지 확인용
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(3000);
                        GameLogger.Info($"DupTest[{ctx.ClientIndex}]", "3초 경과 → 연결 종료");
                        await channel.CloseAsync();
                    });
                }
                else if (res.ErrorCode == ErrorCode.AlreadyLoggedIn)
                {
                    GameLogger.Warn($"DupTest[{ctx.ClientIndex}]",
                        $"[BLOCKED] 중복 로그인 거부: {res.ErrorCode} ← 정상 동작");
                    await channel.CloseAsync();
                }
                else
                {
                    GameLogger.Warn($"DupTest[{ctx.ClientIndex}]", $"[FAIL] 로그인 실패: {res.ErrorCode}");
                    await channel.CloseAsync();
                }
                break;
            }

            case PacketType.NotiSystem:
                GameLogger.Info($"DupTest[{ctx.ClientIndex}]", $"[시스템] {packet.As<NotiSystem>().Message}");
                break;

            default:
                GameLogger.Warn($"DupTest[{ctx.ClientIndex}]", $"미처리 패킷: {packet.Type}");
                break;
        }
    }

    public void OnDisconnected(ClientContext ctx)
    {
        LoadTestStats.IncrementDisconnected();
        GameLogger.Info($"DupTest[{ctx.ClientIndex}]", "연결 해제됨");
    }
}
