using Common.Logging;
using DotNetty.Transport.Channels;
using TestClient.Controllers;
using TestClient.Stats;
using GameServer.Protocol;

namespace TestClient.Scenarios;

/// <summary>
/// RPG 2인 게임 반복 스트레스 시나리오.
/// RpgRoomScenario와 동일한 흐름이나, 게임 종료 후 룸을 나가고
/// 다시 룸 생성/입장 → Ready → 게임 사이클을 반복한다 (Ctrl+C까지).
/// 사용 예: --scenario pve-stress --clients 20 --delay 100
/// (짝수:홀수 쌍으로 10개 룸 동시 진행)
/// </summary>
public class PveStressScenario(string namePrefix) : BaseRoomScenario(namePrefix)
{
    private readonly List<ulong> _monsterIds = [];
    private volatile bool _gameEnded;
    private int _cycleCount;

    // ──────────────────────────────────────────────────────
    // 로그인 성공 → 사이클 시작
    // ──────────────────────────────────────────────────────
    protected override async Task OnLoginSuccessAsync(IChannel channel, ClientContext ctx)
    {
        await StartCycleAsync(channel, ctx);
    }

    // ──────────────────────────────────────────────────────
    // 룸 입장 성공 → 홀수: 즉시 Ready
    // ──────────────────────────────────────────────────────
    protected override async Task OnRoomEnterSuccessAsync(IChannel channel, ClientContext ctx)
    {
        if (ctx.ClientIndex % 2 != 0)
        {
            await channel.WriteAndFlushAsync(new GamePacket { ReqReadyGame = new ReqReadyGame() });
            LoadTestStats.IncrementSent();
        }
    }

    // ──────────────────────────────────────────────────────
    // 패킷 처리
    // ──────────────────────────────────────────────────────
    protected override async Task<bool> OnOtherPacketReceivedAsync(IChannel channel, ClientContext ctx, GamePacket packet)
    {
        switch (packet.PayloadCase)
        {
            case GamePacket.PayloadOneofCase.ResCharacterInfo:
                return true;

            case GamePacket.PayloadOneofCase.ResRoomList:
                await HandleRoomListAsync(channel, ctx, packet.ResRoomList);
                return true;

            case GamePacket.PayloadOneofCase.NotiRoomEnter:
            {
                var noti = packet.NotiRoomEnter;
                if (ctx.ClientIndex % 2 == 0 && noti.PlayerId != ctx.PlayerId)
                {
                    await channel.WriteAndFlushAsync(new GamePacket { ReqReadyGame = new ReqReadyGame() });
                    LoadTestStats.IncrementSent();
                }
                return true;
            }

            case GamePacket.PayloadOneofCase.NotiReadyGame:
            case GamePacket.PayloadOneofCase.NotiGameStart:
                return true;

            case GamePacket.PayloadOneofCase.ResEnterGame:
            {
                _monsterIds.Clear();
                _gameEnded = false;
                foreach (var m in packet.ResEnterGame.Monsters)
                    _monsterIds.Add(m.MonsterId);

                GameLogger.Info($"Client[{ctx.ClientIndex}]",
                    $"[Cycle#{_cycleCount + 1}] 게임 입장: 몬스터 {packet.ResEnterGame.Monsters.Count}마리");

                _ = RunGameActionsAsync(channel, ctx);
                return true;
            }

            case GamePacket.PayloadOneofCase.NotiMove:
            case GamePacket.PayloadOneofCase.NotiHpChange:
            case GamePacket.PayloadOneofCase.NotiCombat:
            case GamePacket.PayloadOneofCase.NotiMonsterAttack:
            case GamePacket.PayloadOneofCase.NotiDeath:
            case GamePacket.PayloadOneofCase.NotiRespawn:
            case GamePacket.PayloadOneofCase.NotiExpGain:
            case GamePacket.PayloadOneofCase.NotiLevelUp:
            case GamePacket.PayloadOneofCase.NotiGameChat:
                return true;

            // 게임 종료 → 룸 퇴장 후 다음 사이클
            case GamePacket.PayloadOneofCase.NotiGameEnd:
            {
                _gameEnded = true;
                _cycleCount++;
                ctx.RoomExitScheduled = true;
                var result = packet.NotiGameEnd.IsClear ? "클리어" : "전멸";
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"[Cycle#{_cycleCount}] 게임 종료: {result}");

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(1000);
                        if (!channel.Active) return;
                        await channel.WriteAndFlushAsync(new GamePacket { ReqRoomExit = new ReqRoomExit() });
                        LoadTestStats.IncrementSent();
                    }
                    catch (Exception ex)
                    {
                        GameLogger.Error($"Client[{ctx.ClientIndex}]", "룸 퇴장 오류", ex);
                    }
                });
                return true;
            }

            // 룸 퇴장 완료 → 다음 사이클 시작
            case GamePacket.PayloadOneofCase.ResRoomExit:
            {
                ctx.RoomExitScheduled = false;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(2000);
                        if (!channel.Active) return;
                        await StartCycleAsync(channel, ctx);
                    }
                    catch (Exception ex)
                    {
                        GameLogger.Error($"Client[{ctx.ClientIndex}]", "다음 사이클 시작 오류", ex);
                    }
                });
                return true;
            }

            case GamePacket.PayloadOneofCase.NotiRoomExit:
                return true;

            default:
                return false;
        }
    }

    public override void OnDisconnected(ClientContext ctx)
    {
        LoadTestStats.IncrementDisconnected();
        GameLogger.Info($"Client[{ctx.ClientIndex}]", $"연결 해제 (총 게임 사이클: {_cycleCount}회)");
    }

    // ──────────────────────────────────────────────────────
    // 사이클 시작: 짝수 → 룸 생성, 홀수 → 룸 목록 요청
    // ──────────────────────────────────────────────────────
    private async Task StartCycleAsync(IChannel channel, ClientContext ctx)
    {
        if (ctx.ClientIndex % 2 == 0)
        {
            await channel.WriteAndFlushAsync(new GamePacket { ReqCreateRoom = new ReqCreateRoom() });
            LoadTestStats.IncrementSent();
        }
        else
        {
            // 짝수 클라이언트가 방을 만들 시간을 확보한 뒤 목록 조회
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(3000);
                    if (!channel.Active) return;
                    await channel.WriteAndFlushAsync(new GamePacket { ReqRoomList = new ReqRoomList() });
                    LoadTestStats.IncrementSent();
                }
                catch (Exception ex)
                {
                    GameLogger.Error($"Client[{ctx.ClientIndex}]", "룸 목록 요청 오류", ex);
                }
            });
        }
    }

    // ──────────────────────────────────────────────────────
    // 룸 목록 → 입장 가능한 방 선택
    // ──────────────────────────────────────────────────────
    private async Task HandleRoomListAsync(IChannel channel, ClientContext ctx, ResRoomList res)
    {
        var available = res.Rooms.FirstOrDefault(r => !r.IsStarted && r.PlayerCount < r.MaxPlayers);
        if (available != null)
        {
            await channel.WriteAndFlushAsync(new GamePacket
            {
                ReqRoomEnter = new ReqRoomEnter { RoomId = available.RoomId }
            });
            LoadTestStats.IncrementSent();
        }
        else
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2000);
                    if (!channel.Active) return;
                    await channel.WriteAndFlushAsync(new GamePacket { ReqRoomList = new ReqRoomList() });
                    LoadTestStats.IncrementSent();
                }
                catch (Exception ex)
                {
                    GameLogger.Error($"Client[{ctx.ClientIndex}]", "룸 목록 재시도 오류", ex);
                }
            });
        }
    }

    // ──────────────────────────────────────────────────────
    // 게임 행동: 이동 3회 → 공격 5회 → 채팅 1회
    // ──────────────────────────────────────────────────────
    private async Task RunGameActionsAsync(IChannel channel, ClientContext ctx)
    {
        try
        {
            var offsetX = ctx.ClientIndex % 2 == 0 ? 1f : -1f;

            for (var i = 0; i < 3 && channel.Active && !_gameEnded; i++)
            {
                await Task.Delay(500);
                if (!channel.Active || _gameEnded) break;
                await channel.WriteAndFlushAsync(new GamePacket
                {
                    ReqMove = new ReqMove
                    {
                        X = 200f + offsetX * (i + 1) * 40f,
                        Y = 200f + i * 30f
                    }
                });
                LoadTestStats.IncrementSent();
            }

            for (var i = 0; i < 5 && channel.Active && !_gameEnded; i++)
            {
                await Task.Delay(1100);
                if (!channel.Active || _gameEnded || _monsterIds.Count == 0) break;
                await channel.WriteAndFlushAsync(new GamePacket
                {
                    ReqAttack = new ReqAttack
                    {
                        TargetMonsterId = _monsterIds[i % _monsterIds.Count]
                    }
                });
                LoadTestStats.IncrementSent();
            }

            if (channel.Active && !_gameEnded)
            {
                await channel.WriteAndFlushAsync(new GamePacket
                {
                    ReqGameChat = new ReqGameChat { Message = $"[Bot{ctx.ClientIndex}] Cycle#{_cycleCount + 1} GG!" }
                });
                LoadTestStats.IncrementSent();
            }
        }
        catch (Exception ex)
        {
            GameLogger.Error($"Client[{ctx.ClientIndex}]", "게임 행동 오류", ex);
            LoadTestStats.IncrementErrors();
        }
    }
}
