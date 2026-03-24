using Common.Logging;
using DotNetty.Transport.Channels;
using TestClient.Controllers;
using TestClient.Stats;
using GameServer.Protocol;

namespace TestClient.Scenarios;

/// <summary>
/// RPG 2인 게임 1회 플레이 시나리오.
/// 짝수 인덱스 클라이언트 → 룸 생성 후 2번째 플레이어 입장 시 Ready.
/// 홀수 인덱스 클라이언트 → 2초 후 룸 목록 조회 → 입장 후 즉시 Ready.
/// 게임 시작 후: 이동 3회 → 공격 5회 → 채팅 1회 → NotiGameEnd 수신 시 연결 해제.
/// </summary>
public class RpgRoomScenario(string namePrefix) : BaseRoomScenario(namePrefix)
{
    private readonly List<ulong> _monsterIds = [];
    private volatile bool _gameEnded;

    // ──────────────────────────────────────────────────────
    // 로그인 성공 → 짝수: 룸 생성, 홀수: 2초 후 룸 목록 요청
    // ──────────────────────────────────────────────────────
    protected override async Task OnLoginSuccessAsync(IChannel channel, ClientContext ctx)
    {
        if (ctx.ClientIndex % 2 == 0)
        {
            await channel.WriteAndFlushAsync(new GamePacket { ReqCreateRoom = new ReqCreateRoom() });
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
                    GameLogger.Error($"Client[{ctx.ClientIndex}]", "룸 목록 요청 오류", ex);
                }
            });
        }
    }

    // ──────────────────────────────────────────────────────
    // 룸 입장 성공 → 홀수: 즉시 Ready (짝수는 NotiRoomEnter에서 Ready)
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
            // 캐릭터 정보 — 서버가 로그인 직후 전송, 클라이언트 측에서는 무시
            case GamePacket.PayloadOneofCase.ResCharacterInfo:
                return true;

            // 룸 목록 — 홀수 클라이언트가 입장할 방 선택
            case GamePacket.PayloadOneofCase.ResRoomList:
                await HandleRoomListAsync(channel, ctx, packet.ResRoomList);
                return true;

            // 룸 입장 알림 — 짝수 클라이언트: 다른 플레이어 입장 시 Ready
            case GamePacket.PayloadOneofCase.NotiRoomEnter:
            {
                var noti = packet.NotiRoomEnter;
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"룸 입장 알림: {noti.PlayerName}");
                if (ctx.ClientIndex % 2 == 0 && noti.PlayerId != ctx.PlayerId)
                {
                    await channel.WriteAndFlushAsync(new GamePacket { ReqReadyGame = new ReqReadyGame() });
                    LoadTestStats.IncrementSent();
                }
                return true;
            }

            case GamePacket.PayloadOneofCase.NotiReadyGame:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"준비 알림: PlayerId={packet.NotiReadyGame.PlayerId}");
                return true;

            case GamePacket.PayloadOneofCase.NotiGameStart:
                GameLogger.Info($"Client[{ctx.ClientIndex}]", "게임 시작!");
                return true;

            // 게임 입장 — 몬스터 목록 수집 후 행동 루프 시작
            case GamePacket.PayloadOneofCase.ResEnterGame:
            {
                var res = packet.ResEnterGame;
                foreach (var m in res.Monsters)
                    _monsterIds.Add(m.MonsterId);

                GameLogger.Info($"Client[{ctx.ClientIndex}]",
                    $"게임 입장: 플레이어 {res.Players.Count}명, 몬스터 {res.Monsters.Count}마리");

                _ = RunGameActionsAsync(channel, ctx);
                return true;
            }

            // 인게임 브로드캐스트 — 통계/로그 외 별도 처리 불필요
            case GamePacket.PayloadOneofCase.NotiMove:
            case GamePacket.PayloadOneofCase.NotiHpChange:
            case GamePacket.PayloadOneofCase.NotiCombat:
            case GamePacket.PayloadOneofCase.NotiMonsterAttack:
            case GamePacket.PayloadOneofCase.NotiDeath:
            case GamePacket.PayloadOneofCase.NotiRespawn:
            case GamePacket.PayloadOneofCase.NotiExpGain:
            case GamePacket.PayloadOneofCase.NotiLevelUp:
            case GamePacket.PayloadOneofCase.NotiGameChat:
            case GamePacket.PayloadOneofCase.NotiRoomExit:
                return true;

            // 게임 종료 → 연결 해제
            case GamePacket.PayloadOneofCase.NotiGameEnd:
            {
                _gameEnded = true;
                var result = packet.NotiGameEnd.IsClear ? "클리어" : "전멸";
                GameLogger.Info($"Client[{ctx.ClientIndex}]", $"게임 종료: {result}");
                await channel.CloseAsync();
                return true;
            }

            default:
                return false;
        }
    }

    // ──────────────────────────────────────────────────────
    // 룸 목록 처리 (홀수 클라이언트 전용)
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
            // 입장 가능한 방이 없으면 2초 후 재시도
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

            // 이동 3회 (500ms 간격)
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

            // 공격 5회 (1.1초 간격 — 서버 쿨다운 1초 초과)
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

            // 채팅 1회
            if (channel.Active && !_gameEnded)
            {
                await channel.WriteAndFlushAsync(new GamePacket
                {
                    ReqGameChat = new ReqGameChat
                    {
                        Message = $"[Bot{ctx.ClientIndex}] GG!"
                    }
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
