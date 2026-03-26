namespace GameServer.Component.Stage.Weapons;

/// <summary>
/// 레벨업 시 선택 가능한 스탯 업그레이드 ID.
/// 무기 ID(0~99)와 구분되도록 100번대를 사용한다.
/// </summary>
public enum StatUpgradeId
{
    AttackUp    = 100, // 공격력 +2
    MaxHpUp     = 101, // 최대체력 +25 (현재 HP도 동량 회복)
    MoveSpeedUp = 102, // 이동속도 +15
    ExpMultiUp  = 103, // 경험치 배율 ×1.10
    ExpRadiusUp = 104, // 경험치 수집 반경 +15
}
