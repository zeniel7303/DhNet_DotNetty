using GameServer.Database.DbSet;

namespace GameServer.Database.System;

/// <summary>
/// 게임 데이터 컨텍스트. 영구 보존이 필요한 게임 데이터 DbSet을 보유한다.
/// TODO [미래]: GlobalDbContext 분리 - 블랙리스트 등 글로벌 공유 데이터
/// TODO [미래]: 멀티월드 지원 시 WorldId별로 GameDbContext 인스턴스 분리
/// </summary>
public class GameDbContext
{
    public PlayerDbSet    Players    { get; }
    public AccountDbSet   Accounts   { get; }
    public CharacterDbSet Characters { get; }

    public GameDbContext(DbConnector connector)
    {
        Players    = new PlayerDbSet(connector);
        Accounts   = new AccountDbSet(connector);
        Characters = new CharacterDbSet(connector);
    }
}
