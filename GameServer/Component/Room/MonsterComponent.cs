namespace GameServer.Component.Room;

public enum MonsterType { Slime = 0, Orc = 1, Dragon = 2 }

/// <summary>
/// 몬스터 상태 컴포넌트. GameSessionComponent._stateLock 하에서만 변경된다.
/// </summary>
public class MonsterComponent
{
    public ulong       MonsterId   { get; }
    public MonsterType Type        { get; }
    public float       X           { get; private set; }
    public float       Y           { get; private set; }
    public int         MaxHp       { get; }
    public int         Atk         { get; }
    public int         Def         { get; }
    public int         ExpReward   { get; }
    public bool        IsBoss      => Type == MonsterType.Dragon;

    private int   _hp;
    private float _deadElapsed;
    private float _attackElapsed;

    private readonly float _respawnDelay;   // 0 = 리스폰 없음 (보스)
    private readonly float _attackInterval;

    public int  Hp      => _hp;
    public bool IsAlive => _hp > 0;

    private static readonly (float ox, float oy)[] SlimeOffsets  = { (200, 150), (600, 150) };
    private static readonly (float ox, float oy)[] OrcOffsets    = { (400, 350) };
    private static readonly (float ox, float oy)[] DragonOffsets = { (400, 500) };

    public MonsterComponent(ulong monsterId, MonsterType type, float x, float y)
    {
        MonsterId = monsterId;
        Type      = type;
        X         = x;
        Y         = y;

        (MaxHp, Atk, Def, ExpReward, _respawnDelay, _attackInterval) = type switch
        {
            MonsterType.Slime  => (30,  5,  0, 20,  10f, 3.0f),
            MonsterType.Orc    => (80,  12, 3, 50,  20f, 2.0f),
            MonsterType.Dragon => (500, 30, 10, 500, 0f,  1.5f),
            _                  => (30,  5,  0, 20,  10f, 3.0f)
        };

        _hp = MaxHp;
    }

    // true = 이번 틱에 리스폰됨
    public bool Tick(float dt)
    {
        _attackElapsed += dt;

        if (IsAlive) return false;
        if (_respawnDelay <= 0) return false; // 보스는 리스폰 없음

        _deadElapsed += dt;
        if (_deadElapsed < _respawnDelay) return false;

        _hp          = MaxHp;
        _deadElapsed = 0;
        return true;
    }

    // true = 이번 틱에 공격 가능
    public bool ShouldAttack()
    {
        if (!IsAlive || _attackElapsed < _attackInterval) return false;
        _attackElapsed = 0;
        return true;
    }

    // 데미지 적용. 사망 시 true 반환.
    public bool TakeDamage(int damage)
    {
        if (!IsAlive) return false;
        damage = Math.Max(1, damage);
        _hp    = Math.Max(0, _hp - damage);
        if (_hp == 0) _deadElapsed = 0;
        return _hp == 0;
    }
}
