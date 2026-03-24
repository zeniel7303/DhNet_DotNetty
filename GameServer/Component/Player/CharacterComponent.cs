using Common.Server.Component;
using GameServer.Database.Rows;

namespace GameServer.Component.Player;

public class CharacterComponent(PlayerComponent player) : BaseComponent
{
    public int  Level   { get; private set; } = 1;
    public long Exp     { get; private set; } = 0;
    public int  Hp      { get; private set; } = 100;
    public int  MaxHp   { get; private set; } = 100;
    public int  Attack  { get; private set; } = 15;
    public int  Defense { get; private set; } = 5;

    public bool IsAlive    => Hp > 0;
    public long NextLevelExp => Level * 100L;

    private const int MaxLevel = 50;

    public override void Initialize() { }

    public void LoadFrom(CharacterRow row)
    {
        Level   = row.level;
        Exp     = row.exp;
        Hp      = row.hp;
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
    public void RestoreFullHp() => Hp = MaxHp;

    // 데미지 적용. 사망 시 true 반환.
    public bool TakeDamage(int damage)
    {
        damage = Math.Max(1, damage);
        Hp     = Math.Max(0, Hp - damage);
        return Hp == 0;
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
            Hp      = MaxHp; // 레벨업 시 풀힐
            Attack  += 3;
            Defense += 1;
            leveled  = true;
        }
        return leveled;
    }

    protected override void OnDispose() { }
}
