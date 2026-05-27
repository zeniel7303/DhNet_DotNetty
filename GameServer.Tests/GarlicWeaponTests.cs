using GameServer.Component.Stage.Monster;
using GameServer.Component.Stage.Weapons;
using GameServer.Resources;
using Xunit;

namespace GameServer.Tests;

/// <summary>
/// GarlicWeapon 공격 로직 검증.
/// 기대값은 GameDataTable.Weapons["Garlic"]에서 읽어 weapons.json 변경 시 자동으로 동기화된다.
/// </summary>
public class GarlicWeaponTests : IClassFixture<GameDataFixture>
{
    public GarlicWeaponTests(GameDataFixture fixture) { _ = fixture; }

    // 쿨다운보다 살짝 긴 dt — 공격이 1회 발동됨을 보장
    private static float TriggerDt()
        => GameDataTable.Weapons["Garlic"].CooldownSec + 0.1f;

    private static float DefaultRadius()
        => GameDataTable.Weapons["Garlic"].AuraRadius ?? 80f;

    private static float UpgradeRadiusInc()
        => GameDataTable.Weapons["Garlic"].UpgradeAuraRadius ?? 10f;

    // ── 반경 내 히트 ──────────────────────────────────────────────────

    [Fact]
    public void Tick_MonsterWithinRadius_IsHit()
    {
        float r      = DefaultRadius();
        var   garlic = new GarlicWeapon();
        var   target = MakeMonster(1, 0f, r * 0.5f); // 반경 절반 → 내부

        var hits = garlic.Tick(TriggerDt(), 0f, 0f, [target]);

        Assert.Single(hits);
        Assert.Equal(target.MonsterId, hits[0].MonsterId);
    }

    [Fact]
    public void Tick_MonsterOutsideRadius_NotHit()
    {
        float r      = DefaultRadius();
        var   garlic = new GarlicWeapon();
        var   target = MakeMonster(2, 0f, r + 1f); // 반경 바깥

        var hits = garlic.Tick(TriggerDt(), 0f, 0f, [target]);

        Assert.Empty(hits);
    }

    [Fact]
    public void Tick_MonsterExactlyOnRadius_IsHit()
    {
        float r      = DefaultRadius();
        var   garlic = new GarlicWeapon();
        var   target = MakeMonster(3, r, 0f); // dSq == radSq → 경계는 히트에 포함

        var hits = garlic.Tick(TriggerDt(), 0f, 0f, [target]);

        Assert.Single(hits);
    }

    [Fact]
    public void Tick_DeadMonster_NotHit()
    {
        var garlic = new GarlicWeapon();
        var dead   = MakeMonster(4, 0f, 10f);
        dead.TakeDamage(9999);

        var hits = garlic.Tick(TriggerDt(), 0f, 0f, [dead]);

        Assert.Empty(hits);
    }

    [Fact]
    public void Tick_MultipleMonsters_AllInRadiusHit()
    {
        float r      = DefaultRadius();
        var   garlic = new GarlicWeapon();
        var   m1     = MakeMonster(5, 10f,  0f);
        var   m2     = MakeMonster(6, -30f, 20f);
        var   m3     = MakeMonster(7, 0f,   r + 1f); // 범위 밖

        var hits = garlic.Tick(TriggerDt(), 0f, 0f, [m1, m2, m3]);

        Assert.Equal(2, hits.Count);
        Assert.Contains(hits, h => h.MonsterId == m1.MonsterId);
        Assert.Contains(hits, h => h.MonsterId == m2.MonsterId);
        Assert.DoesNotContain(hits, h => h.MonsterId == m3.MonsterId);
    }

    // ── 쿨다운 ───────────────────────────────────────────────────────

    [Fact]
    public void Tick_BeforeCooldown_NoHits()
    {
        float cooldown = GameDataTable.Weapons["Garlic"].CooldownSec;
        var   garlic   = new GarlicWeapon();
        var   target   = MakeMonster(8, 10f, 0f);

        var hits = garlic.Tick(cooldown * 0.5f, 0f, 0f, [target]);

        Assert.Empty(hits);
    }

    // ── 기본 반경 ─────────────────────────────────────────────────────────

    [Fact]
    public void Radius_Default_MatchesJson()
    {
        var garlic = new GarlicWeapon();
        Assert.Equal(DefaultRadius(), garlic.Radius, 1);
    }

    // ── 업그레이드 반경 ──────────────────────────────────────────────

    [Fact]
    public void Upgrade_IncreasesRadiusByConfiguredAmount()
    {
        var   garlic = new GarlicWeapon();
        float before = garlic.Radius;

        garlic.Upgrade();

        Assert.Equal(before + UpgradeRadiusInc(), garlic.Radius, 1);
    }

    [Fact]
    public void Upgrade_NewRadiusAffectsHitDetection()
    {
        var   garlic    = new GarlicWeapon();
        float r0        = garlic.Radius;
        float inc       = UpgradeRadiusInc();
        garlic.Upgrade(); // radius: r0 → r0 + inc

        // 업그레이드 전 반경 밖, 업그레이드 후 반경 안인 거리
        float testDist = r0 + inc * 0.5f;
        var   target   = MakeMonster(9, 0f, testDist);

        var hits = garlic.Tick(TriggerDt(), 0f, 0f, [target]);

        Assert.Single(hits);
    }

    // ── 넉백 방향 ────────────────────────────────────────────────────

    [Fact]
    public void Tick_Hit_HasNonZeroPush()
    {
        var garlic = new GarlicWeapon();
        var target = MakeMonster(10, 40f, 0f);

        var hits = garlic.Tick(TriggerDt(), 0f, 0f, [target]);

        Assert.Single(hits);
        Assert.True(hits[0].PushX > 0f, $"PushX 가 양수여야 함. 실제: {hits[0].PushX}");
        Assert.Equal(0f, hits[0].PushY, 2);
    }

    // ── 헬퍼 ─────────────────────────────────────────────────────────

    private static MonsterComponent MakeMonster(ulong id, float x, float y)
        => new(id, MonsterType.Slime, x, y);
}
