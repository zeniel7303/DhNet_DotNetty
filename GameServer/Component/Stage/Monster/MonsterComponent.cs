using Common.Server.Component;
using GameServer.Resources;

namespace GameServer.Component.Stage.Monster;

public enum MonsterType
{
    // 기존 (호환 유지)
    Slime      = 0,
    Orc        = 1,
    Dragon     = 2,
    // VS 스타일 신규
    Bat        = 3,
    Zombie     = 4,
    Skeleton   = 5,
    Ghost      = 6,
    GiantZombie = 7,
    Reaper     = 8,
}

/// <summary>
/// 몬스터 상태 컴포넌트. StageComponent 단일 틱 스레드에서만 변경된다.
/// Update() 호출 전 TargetX/TargetY를 설정하면 AI 추적 후 WasRespawned/WasMoved로 결과를 읽을 수 있다.
/// </summary>
public class MonsterComponent : BaseComponent
{
    // 맵 경계
    private const float MapWidth  = 3200f;
    private const float MapHeight = 2400f;

    public ulong       MonsterId   { get; }
    public MonsterType Type        { get; }
    public float       X           { get; private set; }
    public float       Y           { get; private set; }
    public int         MaxHp       { get; }
    public int         Atk         { get; }
    public int         Def         { get; }
    public int         ExpReward   { get; }
    public int         GoldReward  { get; }
    public float       Speed       { get; }
    public float       AttackRange { get; }
    public float       HitRadius   { get; }
    public bool        IsBoss      => Type is MonsterType.Dragon or MonsterType.Reaper;

    private int   _hp;
    private float _deadElapsed;
    private float _attackElapsed;

    private readonly float _respawnDelay;
    private readonly float _attackInterval;

    public int  Hp         => _hp;
    public bool IsAlive    => _hp > 0;
    public bool CanRespawn => _respawnDelay > 0;

    /// <summary>Update() 호출 전 StageComponent가 추적 목표 좌표를 설정한다.</summary>
    public float TargetX { get; set; }
    public float TargetY { get; set; }

    /// <summary>Update() 이후 이번 틱 리스폰 여부.</summary>
    public bool WasRespawned { get; private set; }
    /// <summary>Update() 이후 이번 틱 이동 여부.</summary>
    public bool WasMoved { get; private set; }

    public MonsterComponent(ulong monsterId, MonsterType type, float x, float y, int waveNumber = 1)
    {
        MonsterId = monsterId;
        Type      = type;
        X         = x;
        Y         = y;
        TargetX   = x;
        TargetY   = y;

        if (!GameDataTable.Monsters.TryGetValue(type.ToString(), out var stat))
            throw new InvalidOperationException($"MonsterType '{type}'을(를) GameDataTable에서 찾을 수 없습니다. monsters.json을 확인하세요.");

        MaxHp           = stat.MaxHp;
        Atk             = stat.Atk;
        Def             = stat.Def;
        ExpReward       = stat.ExpReward;
        GoldReward      = stat.GoldReward;
        Speed           = stat.Speed;
        AttackRange     = stat.AttackRange;
        HitRadius       = stat.HitRadius;
        _respawnDelay   = stat.RespawnDelay;
        _attackInterval = stat.AttackInterval;

        // 웨이브 기반 스탯 스케일링: wave 1 = 1.0x, wave 50 ≈ 4.9x (HP/ATK만 적용)
        if (waveNumber > 1)
        {
            float mult = 1f + (waveNumber - 1) * 0.08f;
            MaxHp = (int)(MaxHp * mult);
            Atk   = (int)(Atk   * mult);
        }

        _hp = MaxHp;
    }

    public override void Initialize()
    {
        _deadElapsed   = 0f;
        _attackElapsed = 0f;
    }

    protected override void OnDispose() { }

    /// <summary>
    /// AI 틱 처리. 호출 전 TargetX/TargetY 설정 필요.
    /// 결과는 WasRespawned, WasMoved로 읽는다.
    /// </summary>
    public override void Update(float dt)
    {
        base.Update(dt);
        WasRespawned = false;
        WasMoved     = false;

        _attackElapsed += dt;

        if (!IsAlive)
        {
            if (_respawnDelay <= 0) return;
            _deadElapsed += dt;
            if (_deadElapsed < _respawnDelay) return;

            _hp            = MaxHp;
            _deadElapsed   = 0;
            _attackElapsed = 0;
            WasRespawned   = true;
            return;
        }

        // Chase AI — TargetX/Y는 StageComponent가 wrap-aware offset으로 계산해 주입
        float dx   = TargetX - X;
        float dy   = TargetY - Y;
        float dist = MathF.Sqrt(dx * dx + dy * dy);

        if (dist > AttackRange && dist > 0.1f)
        {
            float step = Speed * dt;
            float nx = ((X + dx / dist * step) % MapWidth  + MapWidth)  % MapWidth;
            float ny = ((Y + dy / dist * step) % MapHeight + MapHeight) % MapHeight;

            if (MathF.Abs(nx - X) > 0.5f || MathF.Abs(ny - Y) > 0.5f)
            {
                X       = nx;
                Y       = ny;
                WasMoved = true;
            }
        }
    }

    /// <summary>이번 틱에 공격 가능하면 true 반환. 호출 시 쿨다운 리셋.</summary>
    public bool ShouldAttack()
    {
        if (!IsAlive || _attackElapsed < _attackInterval) return false;
        _attackElapsed = 0;
        return true;
    }

    /// <summary>넉백 — 플레이어 반대 방향으로 밀어냄. 맵 순환 적용. 사망 상태면 무시.</summary>
    public void Knockback(float pushX, float pushY)
    {
        if (!IsAlive) return;
        X = ((X + pushX) % MapWidth  + MapWidth)  % MapWidth;
        Y = ((Y + pushY) % MapHeight + MapHeight) % MapHeight;
    }

    /// <summary>데미지 적용. 사망 시 true 반환.</summary>
    public bool TakeDamage(int damage)
    {
        if (!IsAlive) return false;
        damage = Math.Max(1, damage);
        _hp    = Math.Max(0, _hp - damage);
        if (_hp == 0) _deadElapsed = 0;
        return _hp == 0;
    }
}
