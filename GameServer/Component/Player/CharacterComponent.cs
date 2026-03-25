using GameServer.Database.Rows;

namespace GameServer.Component.Player;

public class CharacterComponent(PlayerComponent player)
{
    // 인게임 스탯 — 게임마다 초기값으로 시작, DB에 저장하지 않음
    public int  Level   { get; private set; } = 1;
    public long Exp     { get; private set; } = 0;
    // _stateLock (GameStage) 하에서만 수정됨 — volatile 불필요
    private int _hp = 500;
    public int  Hp      => _hp;
    public int  MaxHp   { get; private set; } = 500;
    public int  Attack  { get; private set; } = 20;
    public int  Defense { get; private set; } = 10;

    // 영속 데이터 — 세션 간 유지, DB에 저장
    public int Gold { get; private set; } = 0;

    public bool IsAlive => _hp > 0;
    public long NextLevelExp => Level * 15L;

    private const int MaxLevel = 50;

    // gold만 로드 — 인게임 스탯은 항상 초기값으로 시작
    public void LoadFrom(CharacterRow row) => Gold = row.gold;

    // gold만 저장
    public CharacterRow ToRow() => new() { account_id = player.AccountId, gold = Gold };

    public void AddGold(int amount)
    {
        if (amount <= 0) return;
        Gold += amount;
    }

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
