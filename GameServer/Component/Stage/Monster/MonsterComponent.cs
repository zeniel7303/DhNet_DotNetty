using Common.Server.Component;

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

    public MonsterComponent(ulong monsterId, MonsterType type, float x, float y)
    {
        MonsterId = monsterId;
        Type      = type;
        X         = x;
        Y         = y;
        TargetX   = x;
        TargetY   = y;

        // (MaxHp, Atk, Def, ExpReward, GoldReward, Speed, AttackRange, _respawnDelay, _attackInterval, HitRadius)
        (MaxHp, Atk, Def, ExpReward, GoldReward, Speed, AttackRange, _respawnDelay, _attackInterval, HitRadius) = type switch
        {
            MonsterType.Slime       => (50,   10,  0,   20,   2,  60f,  40f, 10f,  3.0f, 12f),
            MonsterType.Orc         => (150,  18,  3,   50,   5,  40f,  48f, 20f,  2.5f, 18f),
            MonsterType.Dragon      => (300,  20,  5,  500,  50,  30f,  64f,  0f,  2.0f, 28f),
            MonsterType.Bat         => (10,    5,  0,    1,   1, 120f,  32f, 8f,   1.5f,  8f),
            MonsterType.Zombie      => (50,    8,  0,    3,   2,  40f,  40f, 15f,  2.0f, 14f),
            MonsterType.Skeleton    => (80,   12,  2,    5,   3,  60f,  44f, 18f,  2.0f, 13f),
            MonsterType.Ghost       => (60,   10,  0,    8,   4, 100f,  36f, 12f,  1.8f, 12f),
            MonsterType.GiantZombie => (300,  30,  5,   20,  15,  25f,  56f, 30f,  3.0f, 30f),
            MonsterType.Reaper      => (500,  50, 10,  100, 100,  80f,  72f,  0f,  1.5f, 22f),
            _                       => (50,   10,  0,   20,   2,  60f,  40f, 10f,  3.0f, 12f),
        };

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
