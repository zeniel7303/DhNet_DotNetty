namespace GameServer.Database.Gateway;

/// <summary>
/// 큐에 넣는 쓰기 작업. 캐릭터 영속분은 account_id와 gold, 로그아웃 시각뿐이다.
/// 위치·전투 스탯은 여기 들어오지 않는다.
/// </summary>
internal abstract record SaveWork
{
    public sealed record Character(ulong AccountId, int Gold) : SaveWork;

    public sealed record Logout(ulong AccountId, DateTime LogoutAt) : SaveWork;

    public sealed record Login(ulong AccountId, string PlayerName, string? IpAddress, DateTime LoginAt) : SaveWork;

    public sealed record Room(ulong AccountId, ulong RoomId, string Action, DateTime CreatedAt) : SaveWork;

    public sealed record Stat(int PlayerCount, DateTime CreatedAt) : SaveWork;
}
