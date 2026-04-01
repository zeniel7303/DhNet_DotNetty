using GameServer.Component.Player;
using GameServer.Component.Stage.Monster;
using GameServer.Protocol;

namespace GameServer.Component.Stage;

/// <summary>
/// Protocol Buffer 메시지 빌더 정적 유틸리티.
/// 순수 변환(데이터 → 패킷 메시지)만 담당하며 부수 효과가 없다.
/// </summary>
internal static class StageBroadcastHelper
{
    internal static PlayerInfo BuildPlayerInfo(PlayerComponent p) => new()
    {
        PlayerId = p.AccountId, Name = p.Name,
        X = p.World.X, Y = p.World.Y,
        Level = p.Character.Level, Hp = p.Character.Hp, MaxHp = p.Character.MaxHp
    };

    internal static MonsterInfo BuildMonsterInfo(MonsterComponent m) => new()
    {
        MonsterId = m.MonsterId, MonsterType = (int)m.Type,
        X = m.X, Y = m.Y, Hp = m.Hp, MaxHp = m.MaxHp
    };
}
