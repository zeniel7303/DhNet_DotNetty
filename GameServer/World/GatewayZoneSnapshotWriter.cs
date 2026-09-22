using GameServer.Database.Gateway;
using GameServer.Database.Rows;

namespace GameServer.World;

/// <summary>기존 IDbGateway.UpsertCharacter만 호출한다. FlushAccount는 Session이 호출한다.</summary>
public sealed class GatewayZoneSnapshotWriter : IZoneSnapshotWriter
{
    public void Enqueue(CharacterRow row) => DbGateway.Current.UpsertCharacter(row);
}
