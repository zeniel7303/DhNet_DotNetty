using Common.Server.Component;
using GameServer.Database.Rows;
using GameServer.Resources;

namespace GameServer.Component.Player;

public class PlayerCharacterComponent(PlayerComponent player) : BaseComponent
{
    public override void Initialize() { }
    protected override void OnDispose() { }

    // 인게임 스탯 — 게임마다 초기값으로 시작, DB에 저장하지 않음
    public int  Level   { get; private set; } = 1;
    public long Exp     { get; private set; } = 0;
    // StageComponent 단일 틱 스레드에서만 수정됨 — volatile 불필요
    private int _hp = GameDataTable.Player.InitialHp;
    public int  Hp      => _hp;
    public int  MaxHp   { get; private set; } = GameDataTable.Player.InitialHp;
    public int  Attack  { get; private set; } = GameDataTable.Player.InitialAttack;
    public int  Defense { get; private set; } = GameDataTable.Player.InitialDefense;

    // 레벨업 선택 스탯 보너스 — 게임마다 초기화
    public float ExpMultiplier  { get; private set; } = 1f;
    public float ExpRadiusBonus { get; private set; } = 0f;

    // 영속 데이터 — 세션 간 유지, DB에 저장
    public int Gold { get; private set; } = 0;

    public bool IsAlive => _hp > 0;
    public long NextLevelExp => Level * (long)GameDataTable.Player.LevelExpCoeff;

    public void LoadFrom(CharacterRow row) => Gold = row.gold;
    public CharacterRow ToRow() => new() { account_id = player.AccountId, gold = Gold };

    public void AddGold(int amount)
    {
        if (amount <= 0)
        {
            return;
        }
        Gold += amount;
        player.Save.MarkDirty();
    }

    public void ApplyAttackUp()
    {
        int cap = GameDataTable.Player.AttackUpCap;
        if (Attack >= cap)
        {
            return;
        }
        Attack = Math.Min(Attack + GameDataTable.Player.AttackUpAmount, cap);
    }

    public void ApplyMaxHpUp()
    {
        int cap  = GameDataTable.Player.MaxHpUpCap;
        if (MaxHp >= cap)
        {
            return;
        }
        int gain = Math.Min(GameDataTable.Player.MaxHpUpAmount, cap - MaxHp);
        MaxHp += gain;
        _hp    = Math.Min(_hp + gain, MaxHp);
    }

    public void ApplyExpMultiUp()
        => ExpMultiplier = Math.Min(
            ExpMultiplier * GameDataTable.Player.ExpMultiUpFactor,
            GameDataTable.Player.ExpMultiUpCap);

    public void ApplyExpRadiusUp()
        => ExpRadiusBonus = Math.Min(
            ExpRadiusBonus + GameDataTable.Player.ExpRadiusUpAmount,
            GameDataTable.Player.ExpRadiusUpCap);

    public void RestoreFullHp() => _hp = MaxHp;

    public bool TakeDamage(int damage)
    {
        damage = Math.Max(1, damage);
        _hp    = Math.Max(0, _hp - damage);
        return _hp == 0;
    }

    public int GainExp(int exp)
    {
        Exp += exp;
        return TryLevelUp();
    }

    private int TryLevelUp()
    {
        int levelUps = 0;
        while (Exp >= NextLevelExp)
        {
            Exp -= NextLevelExp;
            Level++;
            _hp = MaxHp;
            levelUps++;
        }
        return levelUps;
    }
}
