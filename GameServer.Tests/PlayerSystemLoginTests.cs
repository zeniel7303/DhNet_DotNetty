using System.Collections.Concurrent;
using System.Reflection;
using Common.Server.Component;
using DotNetty.Transport.Channels.Embedded;
using GameServer.Component.Player;
using GameServer.Network;
using GameServer.Systems;
using Xunit;

namespace GameServer.Tests;

/// <summary>
/// PlayerSystem.TryReserveLogin 중복 로그인 차단 검증.
/// BUG-2 검증: 동일 account_id로 동시 로그인 시도 시 하나만 성공하고 나머지는 거부되어야 함.
///
/// 테스트 격리:
/// - PlayerSystem.Instance는 싱글톤이므로 테스트마다 _players, _reservedAccounts를 리플렉션으로 초기화.
/// - WorkerSystem 시작 불필요 (TryReserveLogin, Add, Remove만 테스트).
/// </summary>
public class PlayerSystemLoginTests
{
    private static readonly FieldInfo PlayersField =
        typeof(PlayerSystem).GetField("_players",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo ReservedAccountsField =
        typeof(PlayerSystem).GetField("_reservedAccounts",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private void ClearPlayerSystem()
    {
        var players = (ConcurrentDictionary<ulong, PlayerComponent>)PlayersField.GetValue(PlayerSystem.Instance)!;
        var reserved = (ConcurrentDictionary<ulong, byte>)ReservedAccountsField.GetValue(PlayerSystem.Instance)!;

        players.Clear();
        reserved.Clear();
    }

    [Fact]
    public void TryReserveLogin_Success_WhenAccountNotReserved()
    {
        ClearPlayerSystem();

        const ulong accountId = 1ul;
        var result = PlayerSystem.Instance.TryReserveLogin(accountId);

        Assert.True(result, "예약되지 않은 계정은 TryReserveLogin이 성공해야 함");
    }

    [Fact]
    public void TryReserveLogin_Fails_WhenAlreadyReserved()
    {
        ClearPlayerSystem();

        const ulong accountId = 2ul;

        var first = PlayerSystem.Instance.TryReserveLogin(accountId);
        Assert.True(first, "첫 번째 TryReserveLogin은 성공해야 함");

        var second = PlayerSystem.Instance.TryReserveLogin(accountId);
        Assert.False(second, "동일 계정의 두 번째 TryReserveLogin은 실패해야 함");
    }

    [Fact]
    public void TryReserveLogin_Fails_WhenPlayerAlreadyAdded()
    {
        ClearPlayerSystem();

        const ulong accountId = 3ul;
        var channel = new EmbeddedChannel();
        var session = new SessionComponent(channel);
        var player = new PlayerComponent(session, "testuser", accountId);

        PlayerSystem.Instance.TryReserveLogin(accountId);
        PlayerSystem.Instance.Add(player);

        var result = PlayerSystem.Instance.TryReserveLogin(accountId);

        Assert.False(result, "플레이어가 이미 추가된 계정은 TryReserveLogin이 실패해야 함");

        PlayerSystem.Instance.Remove(player);
    }

    [Fact]
    public async Task TryReserveLogin_ConcurrentAttempts_OnlyOneSucceeds()
    {
        ClearPlayerSystem();

        const ulong accountId = 4ul;
        const int concurrentAttempts = 100;

        var successCount = 0;
        var tasks = new Task[concurrentAttempts];

        for (int i = 0; i < concurrentAttempts; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                if (PlayerSystem.Instance.TryReserveLogin(accountId))
                {
                    Interlocked.Increment(ref successCount);
                }
            });
        }

        await Task.WhenAll(tasks);

        Assert.Equal(1, successCount);
    }

    [Fact]
    public void Add_ClearsReservation()
    {
        ClearPlayerSystem();

        const ulong accountId = 5ul;
        var channel = new EmbeddedChannel();
        var session = new SessionComponent(channel);
        var player = new PlayerComponent(session, "testuser", accountId);

        PlayerSystem.Instance.TryReserveLogin(accountId);

        var reserved = (ConcurrentDictionary<ulong, byte>)ReservedAccountsField.GetValue(PlayerSystem.Instance)!;
        Assert.True(reserved.ContainsKey(accountId), "TryReserveLogin 후 _reservedAccounts에 존재해야 함");

        PlayerSystem.Instance.Add(player);

        Assert.False(reserved.ContainsKey(accountId), "Add 후 _reservedAccounts에서 제거되어야 함");

        PlayerSystem.Instance.Remove(player);
    }

    [Fact]
    public void Remove_ClearsReservation_IfPresent()
    {
        ClearPlayerSystem();

        const ulong accountId = 6ul;
        var channel = new EmbeddedChannel();
        var session = new SessionComponent(channel);
        var player = new PlayerComponent(session, "testuser", accountId);

        PlayerSystem.Instance.TryReserveLogin(accountId);

        var reserved = (ConcurrentDictionary<ulong, byte>)ReservedAccountsField.GetValue(PlayerSystem.Instance)!;
        Assert.True(reserved.ContainsKey(accountId));

        PlayerSystem.Instance.Remove(player);

        Assert.False(reserved.ContainsKey(accountId), "Remove 시 _reservedAccounts가 정리되어야 함");
    }

    [Fact]
    public async Task TryReserveLogin_RaceWithAdd_BlocksDuplicate()
    {
        ClearPlayerSystem();

        const ulong accountId = 7ul;
        var channel = new EmbeddedChannel();
        var session = new SessionComponent(channel);
        var player = new PlayerComponent(session, "testuser", accountId);

        var addStarted = new TaskCompletionSource();
        var addCompleted = new TaskCompletionSource();

        var addTask = Task.Run(async () =>
        {
            PlayerSystem.Instance.TryReserveLogin(accountId);
            addStarted.SetResult();
            PlayerSystem.Instance.Add(player);
            addCompleted.SetResult();
        });

        await addStarted.Task;

        var reserveSuccess = PlayerSystem.Instance.TryReserveLogin(accountId);

        await addCompleted.Task;
        await addTask;

        Assert.False(reserveSuccess, "Add 진행 중 TryReserveLogin은 실패해야 함");

        PlayerSystem.Instance.Remove(player);
    }

    [Fact]
    public void TryReserveLogin_DoubleCheckPattern_DetectsRace()
    {
        ClearPlayerSystem();

        const ulong accountId = 8ul;
        var channel = new EmbeddedChannel();
        var session = new SessionComponent(channel);
        var player = new PlayerComponent(session, "testuser", accountId);

        var reserved = (ConcurrentDictionary<ulong, byte>)ReservedAccountsField.GetValue(PlayerSystem.Instance)!;

        reserved.TryAdd(accountId, 0);

        PlayerSystem.Instance.Add(player);

        var result = PlayerSystem.Instance.TryReserveLogin(accountId);

        Assert.False(result, "Add 완료 후 예약 시도는 _players 체크로 실패해야 함");

        PlayerSystem.Instance.Remove(player);
    }
}
