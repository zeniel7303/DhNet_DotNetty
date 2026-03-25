using GameServer.Database.Rows;

namespace GameServer.Component.Player;

public class CharacterComponent(PlayerComponent player)
{
    public int  Level   { get; private set; } = 1;
    public long Exp     { get; private set; } = 0;
    // _stateLock (GameSessionComponent) 하에서만 수정됨 — volatile 불필요
    private int _hp = 500;
    public int  Hp      => _hp;
    public int  MaxHp   { get; private set; } = 500;
    public int  Attack  { get; private set; } = 20;
    public int  Defense { get; private set; } = 10;

    public bool IsAlive => _hp > 0;
    public long NextLevelExp => Level * 100L;

    private const int MaxLevel = 50;

    public void LoadFrom(CharacterRow row)
    {
        Level   = row.level;
        Exp     = row.exp;
        _hp     = row.hp;
        MaxHp   = row.max_hp;
        Attack  = row.attack;
        Defense = row.defense;
    }

    public CharacterRow ToRow() => new()
    {
        account_id = player.AccountId,
        level      = Level,
        exp        = Exp,
        hp         = Hp,
        max_hp     = MaxHp,
        attack     = Attack,
        defense    = Defense,
        x          = player.World.X,
        y          = player.World.Y,
    };

    // 게임 시작 시 HP 완전 회복.
    public void RestoreFullHp() => _hp = MaxHp;

    // 데미지 적용. 사망 시 true 반환.
    public bool TakeDamage(int damage)
    {
        damage = Math.Max(1, damage);
        _hp    = Math.Max(0, _hp - damage);
        return _hp == 0;
    }

    // EXP 획득 + 레벨업 처리. 레벨 변경 시 true 반환.
    public bool GainExp(int exp)
    {
        Exp += exp;
        return TryLevelUp();
    }

    private bool TryLevelUp()
    {
        bool leveled = false;
        while (Level < MaxLevel && Exp >= NextLevelExp)
        {
            Exp    -= NextLevelExp;
            Level++;
            MaxHp  += 20;
            _hp     = MaxHp; // 레벨업 시 풀힐
            Attack  += 3;
            Defense += 1;
            leveled  = true;
        }
        return leveled;
    }
}
