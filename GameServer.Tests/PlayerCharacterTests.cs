using GameServer.Component.Player;
using GameServer.Resources;
using Xunit;

namespace GameServer.Tests;

/// <summary>
/// PlayerCharacterComponent — 레벨업·데미지·스탯 업그레이드 검증.
/// 기대값은 GameDataTable.Player에서 읽어 player.json 변경 시 자동으로 동기화된다.
/// </summary>
public class PlayerCharacterTests : IClassFixture<GameDataFixture>
{
    public PlayerCharacterTests(GameDataFixture fixture) { _ = fixture; }

    private static PlayerCharacterComponent Create() => new(null!);

    // ── 레벨업 ────────────────────────────────────────────────────────────────

    [Fact]
    public void GainExp_NoLevelUp_WhenExpInsufficient()
    {
        var c = Create(); // Lv.1
        long need = c.NextLevelExp;

        int levelUps = c.GainExp((int)(need - 1));

        Assert.Equal(0, levelUps);
        Assert.Equal(1, c.Level);
        Assert.Equal(need - 1, c.Exp);
    }

    [Fact]
    public void GainExp_OneLevelUp_WhenExpExact()
    {
        var c = Create();
        int need = (int)c.NextLevelExp; // LevelExpCoeff * Level

        int levelUps = c.GainExp(need);

        Assert.Equal(1, levelUps);
        Assert.Equal(2, c.Level);
        Assert.Equal(0, c.Exp);
    }

    [Fact]
    public void GainExp_OneLevelUp_ExcessExpCarriedOver()
    {
        var c = Create();
        int need = (int)c.NextLevelExp;
        int excess = 5;

        c.GainExp(need + excess);

        Assert.Equal(2, c.Level);
        Assert.Equal(excess, c.Exp);
    }

    [Fact]
    public void GainExp_MultiLevelUp_InOneCall()
    {
        var c    = Create();
        int coef = GameDataTable.Player.LevelExpCoeff;
        // Lv1→2: coef*1, Lv2→3: coef*2 → 합계 = coef*3
        int levelUps = c.GainExp(coef * 3);

        Assert.Equal(2, levelUps);
        Assert.Equal(3, c.Level);
        Assert.Equal(0, c.Exp);
    }

    [Fact]
    public void GainExp_LevelUp_RestoresFullHp()
    {
        var c = Create();
        int dmg = GameDataTable.Player.InitialHp / 2;
        c.TakeDamage(dmg);
        Assert.Equal(GameDataTable.Player.InitialHp - dmg, c.Hp);

        c.GainExp((int)c.NextLevelExp);

        Assert.Equal(c.MaxHp, c.Hp);
    }

    [Fact]
    public void GainExp_NoMaxLevelCap()
    {
        var c    = Create();
        int coef = GameDataTable.Player.LevelExpCoeff;
        // Lv1→100: 합산 = coef * (1+2+...+99) = coef * 4950
        c.GainExp(coef * 4950);

        Assert.Equal(100, c.Level);
    }

    // ── 데미지 ────────────────────────────────────────────────────────────────

    [Fact]
    public void TakeDamage_ReducesHpByAmount()
    {
        var c   = Create();
        int dmg = 100;

        c.TakeDamage(dmg);

        Assert.Equal(GameDataTable.Player.InitialHp - dmg, c.Hp);
    }

    [Fact]
    public void TakeDamage_MinimumOneDamage()
    {
        var c      = Create();
        int before = c.Hp;

        c.TakeDamage(0); // 0 → 최소 1

        Assert.Equal(before - 1, c.Hp);
    }

    [Fact]
    public void TakeDamage_ReturnsTrueOnDeath()
    {
        var c = Create();

        bool died = c.TakeDamage(9999);

        Assert.True(died);
        Assert.Equal(0, c.Hp);
        Assert.False(c.IsAlive);
    }

    [Fact]
    public void TakeDamage_DoesNotGoBelowZero()
    {
        var c = Create();
        c.TakeDamage(9999);

        Assert.Equal(0, c.Hp);
    }

    [Fact]
    public void TakeDamage_SurvivalReturnsFalse()
    {
        var c = Create();

        bool died = c.TakeDamage(1);

        Assert.False(died);
        Assert.True(c.IsAlive);
    }

    // ── 스탯 업그레이드 ───────────────────────────────────────────────────────

    [Fact]
    public void ApplyAttackUp_IncreasesByConfiguredAmount()
    {
        var c      = Create();
        int before = c.Attack;

        c.ApplyAttackUp();

        Assert.Equal(before + GameDataTable.Player.AttackUpAmount, c.Attack);
    }

    [Fact]
    public void ApplyAttackUp_CapsAtConfiguredMax()
    {
        var c = Create();
        for (int i = 0; i < 100; i++) c.ApplyAttackUp();

        Assert.Equal(GameDataTable.Player.AttackUpCap, c.Attack);
    }

    [Fact]
    public void ApplyMaxHpUp_IncreasesMaxHpAndCurrentHp()
    {
        var c      = Create();
        int before = c.MaxHp;

        c.ApplyMaxHpUp();

        Assert.Equal(before + GameDataTable.Player.MaxHpUpAmount, c.MaxHp);
        Assert.Equal(before + GameDataTable.Player.MaxHpUpAmount, c.Hp);
    }

    [Fact]
    public void ApplyMaxHpUp_CapsAtConfiguredMax()
    {
        var c = Create();
        for (int i = 0; i < 100; i++) c.ApplyMaxHpUp();

        Assert.Equal(GameDataTable.Player.MaxHpUpCap, c.MaxHp);
    }

    [Fact]
    public void ApplyExpMultiUp_MultipliesByConfiguredFactor()
    {
        var c     = Create();
        float before = c.ExpMultiplier;

        c.ApplyExpMultiUp();

        Assert.Equal(before * GameDataTable.Player.ExpMultiUpFactor, c.ExpMultiplier, 4);
    }

    [Fact]
    public void ApplyExpMultiUp_CapsAtConfiguredMax()
    {
        var c = Create();
        for (int i = 0; i < 100; i++) c.ApplyExpMultiUp();

        Assert.Equal(GameDataTable.Player.ExpMultiUpCap, c.ExpMultiplier, 3);
    }

    [Fact]
    public void ApplyExpRadiusUp_IncreasesByConfiguredAmount()
    {
        var c = Create();

        c.ApplyExpRadiusUp();

        Assert.Equal(GameDataTable.Player.ExpRadiusUpAmount, c.ExpRadiusBonus, 3);
    }

    [Fact]
    public void ApplyExpRadiusUp_CapsAtConfiguredMax()
    {
        var c = Create();
        for (int i = 0; i < 100; i++) c.ApplyExpRadiusUp();

        Assert.Equal(GameDataTable.Player.ExpRadiusUpCap, c.ExpRadiusBonus, 3);
    }

    // ── 기타 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void RestoreFullHp_AfterDamage_ResetsToMaxHp()
    {
        var c = Create();
        c.TakeDamage(GameDataTable.Player.InitialHp / 2);

        c.RestoreFullHp();

        Assert.Equal(c.MaxHp, c.Hp);
    }

    [Fact]
    public void NextLevelExp_IsLevelTimesCoeff()
    {
        var c    = Create();
        int coef = GameDataTable.Player.LevelExpCoeff;
        Assert.Equal((long)coef, c.NextLevelExp); // Lv.1 → 1*coef

        c.GainExp(coef); // Lv.2
        Assert.Equal((long)coef * 2, c.NextLevelExp); // Lv.2 → 2*coef
    }
}
