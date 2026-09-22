using Common.Logging;
using GameServer.Component.Player;
using GameServer.Systems;

namespace GameServer.World;

/// <summary>
/// 존에 있는 계정의 끊김을 Session이 조율한다.
/// World는 스냅샷 적재까지, FlushAccount와 Remove는 여기서만 호출한다.
/// </summary>
public static class SessionZoneDisconnect
{
    private static DisconnectOrchestrator _orchestrator = new(TimeSpan.FromSeconds(3));

    public static void Configure(TimeSpan snapshotTimeout)
        => _orchestrator = new DisconnectOrchestrator(snapshotTimeout);

    public static async Task RunFromSessionAsync(PlayerComponent player)
    {
        try
        {
            if (!player.TryBeginDisconnectCleanup())
                return;

            await CompleteAsync(player, alreadyOnPlayerWorker: false);
        }
        catch (Exception ex)
        {
            GameLogger.Error("SessionZoneDisconnect",
                $"끊김 조율 실패 (AccountId={player.AccountId})", ex);
            TryRemove(player);
        }
    }

    public static async Task CompleteAsync(PlayerComponent player, bool alreadyOnPlayerWorker)
    {
        player.Save.MarkDisconnecting();
        try
        {
            if (alreadyOnPlayerWorker)
                player.CleanupPresence();
            else
                await player.EnqueueEventAsync(player.CleanupPresence);
        }
        catch (Exception ex)
        {
            GameLogger.Error("SessionZoneDisconnect",
                $"존 퇴장 전 정리 실패 (AccountId={player.AccountId})", ex);
        }

        await _orchestrator.RunAsync(new DisconnectWork
        {
            AccountId = player.AccountId,
            CharacterId = player.AccountId,
            World = ContentZone.Shared,
            Flush = () => player.Save.FlushSessionAsync(DateTime.UtcNow),
            Remove = () => PlayerSystem.Instance.Remove(player),
        });
    }

    private static void TryRemove(PlayerComponent player)
    {
        try
        {
            PlayerSystem.Instance.Remove(player);
        }
        catch (Exception ex)
        {
            GameLogger.Error("SessionZoneDisconnect",
                $"제거 실패 (AccountId={player.AccountId})", ex);
        }
    }
}
