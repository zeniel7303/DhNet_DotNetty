namespace GameServer.Database.Gateway;

/// <summary>DBServer가 기동 시 읽어 게임 서버에 넘기는 ID 시드.</summary>
public readonly record struct DbIdSeeds(ulong MaxAccountId, ulong MaxRoomId);
