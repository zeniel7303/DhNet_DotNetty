using System.Reflection;
using System.Runtime.CompilerServices;
using GameServer.Component.Player;
using GameServer.Component.Stage.Weapons;
using Xunit;

namespace GameServer.Tests;

/// <summary>
/// WeaponComponent.GenerateChoices 스탯 업그레이드 IsUpgrade 추적 검증.
///
/// 테스트 설정:
///   - RuntimeHelpers.GetUninitializedObject 로 PlayerComponent를 생성자 없이 생성한 뒤 AccountId만 주입.
///     (GenerateChoices는 player.AccountId 외 다른 멤버를 읽지 않는다.)
///   - 리플렉션으로 private 필드(_playerWeapons, _statUpgradeLevels)에 직접 접근해 상태를 초기화.
///   - GenerateChoices가 랜덤으로 3개를 반환하므로, 특정 항목이 나올 때까지 반복 호출한다.
/// </summary>
public class WeaponComponentTests : IClassFixture<GameDataFixture>
{
    public WeaponComponentTests(GameDataFixture fixture) { _ = fixture; }

    // ── 리플렉션 캐시 ───────────────────────────────────────────────
    private static readonly FieldInfo PlayerWeaponsField =
        typeof(WeaponComponent).GetField("_playerWeapons",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo StatLevelsField =
        typeof(WeaponComponent).GetField("_statUpgradeLevels",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static readonly FieldInfo AccountIdBackingField =
        typeof(PlayerComponent).GetField("<AccountId>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance)!;

    // ── 헬퍼 ─────────────────────────────────────────────────────────

    private static PlayerComponent FakePlayer(ulong accountId)
    {
        // 생성자를 호출하지 않고 객체 할당만 수행 — AccountId 이외 필드는 기본값(null/0)
        var p = (PlayerComponent)RuntimeHelpers.GetUninitializedObject(typeof(PlayerComponent));
        AccountIdBackingField.SetValue(p, accountId);
        return p;
    }

    private static WeaponComponent MakeWc(ulong accountId, params WeaponBase[] weapons)
    {
        var wc = new WeaponComponent();
        wc.Initialize();

        var owned = (Dictionary<ulong, List<WeaponBase>>)PlayerWeaponsField.GetValue(wc)!;
        owned[accountId] = new List<WeaponBase>(weapons);
        return wc;
    }

    private static void SetStatLevel(WeaponComponent wc, ulong accountId, int choiceId, int count)
    {
        var outer = (Dictionary<ulong, Dictionary<int, int>>)StatLevelsField.GetValue(wc)!;
        if (!outer.TryGetValue(accountId, out var inner))
            outer[accountId] = inner = new Dictionary<int, int>();
        inner[choiceId] = count;
    }

    /// <summary>
    /// GenerateChoices를 최대 maxTries 회 호출하여 특정 WeaponId의 WeaponChoice를 찾는다.
    /// 랜덤 샘플링(3/n) 특성상 반복 필요. P(not seen in 50 tries) < 1e-8.
    /// </summary>
    private static WeaponChoice? FindChoice(WeaponComponent wc, PlayerComponent player,
        int targetWeaponId, int maxTries = 50)
    {
        for (int i = 0; i < maxTries; i++)
        {
            var found = wc.GenerateChoices(player).FirstOrDefault(c => c.WeaponId == targetWeaponId);
            if (found != null) return found;
        }
        return null;
    }

    // ── 스탯 업그레이드 IsUpgrade 추적 ──────────────────────────────

    [Fact]
    public void GenerateChoices_StatUpgrade_IsUpgradeFalse_WhenNeverChosen()
    {
        const ulong id = 1ul;
        var wc     = MakeWc(id, new KnifeWeapon());
        var player = FakePlayer(id);

        // _statUpgradeLevels에 아무 항목도 없음 → AttackUp은 처음 등장
        var choice = FindChoice(wc, player, (int)StatUpgradeId.AttackUp);

        Assert.NotNull(choice);
        Assert.False(choice.IsUpgrade, "한 번도 선택하지 않은 스탯은 IsUpgrade=false여야 한다");
        Assert.Equal(1, choice.NextLevel);
    }

    [Fact]
    public void GenerateChoices_StatUpgrade_IsUpgradeTrue_WhenChosenBefore()
    {
        const ulong id = 2ul;
        var wc     = MakeWc(id, new KnifeWeapon());
        var player = FakePlayer(id);

        // AttackUp을 1회 선택한 상태 시뮬레이션
        SetStatLevel(wc, id, (int)StatUpgradeId.AttackUp, 1);

        var choice = FindChoice(wc, player, (int)StatUpgradeId.AttackUp);

        Assert.NotNull(choice);
        Assert.True(choice.IsUpgrade, "이미 선택한 스탯은 IsUpgrade=true여야 한다 (버그 수정 검증)");
        Assert.Equal(2, choice.NextLevel);
    }

    [Fact]
    public void GenerateChoices_StatUpgrade_NextLevel_TracksMultipleSelections()
    {
        const ulong id = 3ul;
        var wc     = MakeWc(id, new KnifeWeapon());
        var player = FakePlayer(id);

        SetStatLevel(wc, id, (int)StatUpgradeId.MaxHpUp, 3); // 3회 선택

        var choice = FindChoice(wc, player, (int)StatUpgradeId.MaxHpUp);

        Assert.NotNull(choice);
        Assert.True(choice.IsUpgrade);
        Assert.Equal(4, choice.NextLevel); // curLevel(3) + 1
    }

    [Fact]
    public void GenerateChoices_DifferentStats_IndependentTracking()
    {
        const ulong id = 4ul;
        var wc     = MakeWc(id, new KnifeWeapon());
        var player = FakePlayer(id);

        // AttackUp만 선택함 — MaxHpUp은 미선택
        SetStatLevel(wc, id, (int)StatUpgradeId.AttackUp, 2);

        var attackChoice = FindChoice(wc, player, (int)StatUpgradeId.AttackUp);
        var maxHpChoice  = FindChoice(wc, player, (int)StatUpgradeId.MaxHpUp);

        Assert.NotNull(attackChoice);
        Assert.NotNull(maxHpChoice);
        Assert.True(attackChoice.IsUpgrade,   "선택된 스탯은 IsUpgrade=true");
        Assert.False(maxHpChoice.IsUpgrade,   "선택되지 않은 스탯은 IsUpgrade=false");
        Assert.Equal(3, attackChoice.NextLevel);
        Assert.Equal(1, maxHpChoice.NextLevel);
    }

    // ── 무기 소유 여부 IsUpgrade ─────────────────────────────────────

    [Fact]
    public void GenerateChoices_OwnedWeapon_IsUpgradeTrue()
    {
        const ulong id = 5ul;
        var wc     = MakeWc(id, new KnifeWeapon());
        var player = FakePlayer(id);

        var choice = FindChoice(wc, player, (int)WeaponId.Knife);

        Assert.NotNull(choice);
        Assert.True(choice.IsUpgrade, "이미 보유한 무기는 IsUpgrade=true여야 한다");
        Assert.Equal(2, choice.NextLevel); // Lv.1 → 업그레이드 시 2
    }

    [Fact]
    public void GenerateChoices_NotOwnedWeapon_IsUpgradeFalse()
    {
        const ulong id = 6ul;
        var wc     = MakeWc(id, new KnifeWeapon()); // 단검만 보유
        var player = FakePlayer(id);

        var choice = FindChoice(wc, player, (int)WeaponId.Garlic); // 마늘은 미보유

        Assert.NotNull(choice);
        Assert.False(choice.IsUpgrade, "미보유 무기는 IsUpgrade=false여야 한다");
        Assert.Equal(1, choice.NextLevel);
    }

    // ── GetPrimaryWeaponId ───────────────────────────────────────────

    [Fact]
    public void GetPrimaryWeaponId_ReturnsFirstOwnedWeapon()
    {
        const ulong id = 7ul;
        var wc = MakeWc(id, new GarlicWeapon(), new KnifeWeapon());

        Assert.Equal(WeaponId.Garlic, wc.GetPrimaryWeaponId(id));
    }

    [Fact]
    public void GetPrimaryWeaponId_FallsBackToKnife_WhenUnregistered()
    {
        var wc = new WeaponComponent();
        wc.Initialize();

        Assert.Equal(WeaponId.Knife, wc.GetPrimaryWeaponId(9999ul));
    }

    // ── 언레지스터 후 상태 정리 ──────────────────────────────────────

    [Fact]
    public void Unregister_RemovesPlayerState()
    {
        const ulong id = 8ul;
        var wc     = MakeWc(id, new KnifeWeapon());
        SetStatLevel(wc, id, (int)StatUpgradeId.AttackUp, 1);
        var player = FakePlayer(id);

        wc.Unregister(id);

        // 언레지스터 후 GenerateChoices는 빈 리스트를 반환해야 함
        var choices = wc.GenerateChoices(player);
        Assert.Empty(choices);
    }
}
