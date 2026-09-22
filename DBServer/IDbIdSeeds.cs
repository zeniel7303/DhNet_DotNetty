using GameServer.Database.Gateway;

namespace DBServer;

/// <summary>게임 서버 재시작 시 account_id, room_id를 이어받기 위한 시드.</summary>
public interface IDbIdSeeds
{
    Task<DbIdSeeds> ReadIdSeedsAsync(CancellationToken cancellationToken = default);
}
