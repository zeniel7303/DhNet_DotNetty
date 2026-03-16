using GameServer.Database.DbSet;

namespace GameServer.Database.System;

/// <summary>
/// 로그 데이터 컨텍스트. 이벤트 이력 등 로그성 데이터 DbSet을 보유한다.
/// 게임 데이터(GameDbContext)와 분리하여 부하 및 보존 정책을 독립적으로 관리한다.
/// </summary>
public class GameLogContext
{
    public RoomLogDbSet RoomLogs { get; }
    public LoginLogDbSet LoginLogs { get; }
    public ChatLogDbSet ChatLogs { get; }
    public StatLogDbSet StatLogs { get; }

    public GameLogContext(DbConnector connector)
    {
        RoomLogs = new RoomLogDbSet(connector);
        LoginLogs = new LoginLogDbSet(connector);
        ChatLogs = new ChatLogDbSet(connector);
        StatLogs = new StatLogDbSet(connector);
    }
}
