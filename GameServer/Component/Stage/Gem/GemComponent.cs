using Common.Server.Component;

namespace GameServer.Component.Stage.Gem;

/// <summary>
/// 경험치 젬 관리. StageComponent 단일 틱 스레드에서만 접근된다.
/// </summary>
public class GemComponent : BaseComponent
{
    public override void Initialize() => _gems.Clear();
    protected override void OnDispose() => _gems.Clear();
    private static long _gemIdSeq;
    private static ulong NextGemId() => (ulong)Interlocked.Increment(ref _gemIdSeq);

    private const float DefaultPickupRadius = 50f;

    public record Gem(ulong Id, float X, float Y, int ExpValue);

    private readonly Dictionary<ulong, Gem> _gems = new();

    /// <summary>젬을 스폰하고 반환한다 (브로드캐스트는 호출자 책임).</summary>
    public Gem Spawn(float x, float y, int expValue)
    {
        var gem = new Gem(NextGemId(), x, y, expValue);
        _gems[gem.Id] = gem;
        return gem;
    }

    /// <summary>
    /// 플레이어 위치에서 픽업 범위 내 젬을 수집한다.
    /// 수집된 젬 목록을 반환 (호출자가 EXP 지급 및 브로드캐스트 처리).
    /// </summary>
    public List<Gem> CollectNearby(float playerX, float playerY, float radiusBonus = 0f)
    {
        float radius    = DefaultPickupRadius + radiusBonus;
        float radiusSq  = radius * radius;
        var   collected = new List<Gem>();

        foreach (var gem in _gems.Values)
        {
            float dx = gem.X - playerX;
            float dy = gem.Y - playerY;
            if (dx * dx + dy * dy <= radiusSq)
                collected.Add(gem);
        }

        foreach (var gem in collected)
            _gems.Remove(gem.Id);

        return collected;
    }

    public void Clear() => _gems.Clear();
}
