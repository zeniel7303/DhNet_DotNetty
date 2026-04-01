using Common;
using Common.Logging;

namespace GameServer.Systems;

// 모든 게임 시스템의 생명주기(시작/종료)를 한 곳에서 관리
internal static class GameSystems
{
    // 서버 바인딩 전 호출 — 모든 시스템 초기화 및 백그라운드 스레드 시작
    public static void Start(GameServerSettings settings, CancellationTokenSource cts)
    {
        ShutdownSystem.Instance.Initialize(cts);
        PlayerSystem.Instance.Initialize(settings.MaxPlayers);
        LobbySystem.Instance.Initialize(lobbyCount: 1, lobbyCapacity: settings.MaxPlayers,
            maxPlayersPerRoom: settings.MaxPlayersPerRoom);
        
        SessionSystem.Instance.StartSystem();
        PlayerSystem.Instance.StartSystem();
        RoomSystem.Instance.StartSystem();
    }

    // 서버 바인딩 해제 후 호출 — 세션/플레이어 정리 및 DB 동기화 대기
    public static async Task StopAsync()
    {
        GameLogger.Info("GameSystems", "[Shutdown] 세션 정리...");
        SessionSystem.Instance.Stop();

        // RoomSystem을 먼저 중단 — 이후 PlayerSystem 정리 중 StageComponent.Update()가
        // 이미 종료된 PlayerComponent 세션에 접근하는 것을 방지
        GameLogger.Info("GameSystems", "[Shutdown] 룸 시스템 정리...");
        RoomSystem.Instance.Stop();

        GameLogger.Info("GameSystems", "[Shutdown] 플레이어 DB 동기화 대기...");
        await PlayerSystem.Instance.WaitUntilEmptyAsync(TimeSpan.FromSeconds(30));
        PlayerSystem.Instance.Stop();
    }
}
