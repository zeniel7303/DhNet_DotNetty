using GameServer.Database;
using GameServer.Database.Gateway;

namespace DBServer;

/// <summary>DBServer가 이미 연 MySQL에서 ID 시드를 읽는다.</summary>
public sealed class DatabaseIdSeeds(DatabaseSystem database) : IDbIdSeeds
{
    public async Task<DbIdSeeds> ReadIdSeedsAsync(CancellationToken cancellationToken = default)
    {
        var seeds = await database.ReadIdSeedsAsync();
        return new DbIdSeeds(seeds.MaxAccountId, seeds.MaxRoomId);
    }
}
