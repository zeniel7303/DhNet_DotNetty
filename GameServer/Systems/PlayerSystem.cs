using System.Collections.Concurrent;
using Common.Logging;
using Common.Server;
using Common.Server.Component;
using GameServer.Component.Player;
using GameServer.Protocol;

namespace GameServer.Systems;

public class PlayerSystem
{
    public static readonly PlayerSystem Instance = new();

    public int MaxPlayers { get; private set; } = ServerConstants.MaxPlayers;

    private readonly WorkerSystem<PlayerComponent> _workers = new(workerCount: 2, intervalMs: 100);
    private readonly ConcurrentDictionary<ulong, PlayerComponent> _players = new();

    // 로그인 진행 중인 account_id 예약 — DB Insert 전에 중복 로그인 차단
    // Add() 시 _players로 이전되고, 실패/종료 경로에서는 Remove()가 정리
    private readonly ConcurrentDictionary<ulong, byte> _reservedAccounts = new();

    public int Count => _players.Count;

    // LoginProcessor에서 DB Insert 전 호출 — account_id 중복 로그인 차단
    // 이미 활성(_players) 또는 로그인 진행 중(_reservedAccounts)이면 false 반환
    //
    // 동시성 안전성:
    //   1) 첫 ContainsKey: 빠른 조기 거부 (이미 로그인 완료된 경우)
    //   2) TryAdd: 원자적 예약 — 동시 호출 시 단 하나만 성공
    //   3) 둘째 ContainsKey: Add()가 TryAdd 사이에 완료된 경우 감지 후 예약 롤백
    //
    // Race 시나리오 분석:
    //   - 두 스레드가 동시에 TryReserveLogin 호출
    //     → TryAdd 단계에서 하나만 성공, 나머지는 line 35에서 false 반환 ✓
    //
    //   - TryReserveLogin과 Add() 동시 실행
    //     → Add()가 _players.TryAdd 완료 시, line 38 재검증에서 감지하여 예약 롤백 ✓
    //     → TryReserveLogin이 먼저 예약 완료 시, Add()의 _players.TryAdd가 실패하여 차단 ✓
    //
    // Add() 순서 의존성: _players.TryAdd → _reservedAccounts.TryRemove 순서 필수
    // (역순 시 TryReserveLogin이 사이 구간을 통과할 수 있음)
    public bool TryReserveLogin(ulong accountId)
    {
        if (_players.ContainsKey(accountId))
        {
            return false;
        }
        if (!_reservedAccounts.TryAdd(accountId, 0))
        {
            return false;
        }
        if (_players.ContainsKey(accountId))
        {
            _reservedAccounts.TryRemove(accountId, out _);
            return false;
        }
        return true;
    }

    public void Add(PlayerComponent player)
    {
        // _players 추가 후 reservation 제거 — 순서 중요
        // 역순(TryRemove → TryAdd) 시 사이 구간에서 TryReserveLogin이 통과하는 TOCTOU 발생 가능
        if (!_players.TryAdd(player.AccountId, player))
        {
            GameLogger.Error("PlayerSystem", $"중복 AccountId 감지: {player.AccountId} — workers 등록 건너뜀");
            return;
        }
        _reservedAccounts.TryRemove(player.AccountId, out _);
        _workers.Add(player);
    }

    public void Remove(PlayerComponent player)
    {
        // ImmediateFinalize 경로 등에서 reservation이 남아있을 경우 안전망으로 정리
        _reservedAccounts.TryRemove(player.AccountId, out _);
        _players.TryRemove(player.AccountId, out _);
        _workers.Remove(player);
        // worker 제거 후 Dispose 위임 — DisconnectForNextTick 경로에서 Dispose 자동 완결
        player.Dispose();
    }

    public PlayerComponent? TryGet(ulong id) => _players.GetValueOrDefault(id);

    public IReadOnlyList<PlayerComponent> GetAll() => _players.Values.ToList();

    // 전체 접속자에게 시스템 공지 전송
    public void BroadcastAll(string message)
    {
        var noti = new GamePacket { NotiSystem = new NotiSystem { Message = message } };

        foreach (var player in _players.Values)
        {
            _ = player.Session.SendAsync(noti);
        }
    }

    // DisconnectAsync는 flush 시도가 끝난 뒤 Remove한다.
    // 전송이 끝나지 않은 flush는 플레이어를 붙잡아 두므로, Count == 0은 그 시도가 모두 돌아왔다는 뜻이다.
    public async Task WaitUntilEmptyAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (_players.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }

        if (_players.Count > 0)
        {
            GameLogger.Warn("PlayerSystem", $"WaitUntilEmptyAsync 타임아웃: {_players.Count}명 미정리");
        }
    }

    public void Initialize(int maxPlayers) => MaxPlayers = maxPlayers;

    public void StartSystem() => _workers.StartSystem();

    public void Stop() => _workers.Stop();
}
