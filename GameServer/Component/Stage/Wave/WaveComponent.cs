using Common.Server.Component;
using GameServer.Component.Stage.Monster;

namespace GameServer.Component.Stage.Wave;

/// <summary>
/// 웨이브 기반 몬스터 스포너.
/// Update() 호출 전 MonsterCount를 설정하면, 웨이브 트리거 시 LastSpawns에 스폰 목록이 저장된다.
/// StageComponent 단일 틱 스레드에서 호출된다.
/// </summary>
public class WaveComponent : BaseComponent
{
    private const float MapWidth    = 3200f;
    private const float MapHeight   = 2400f;
    private const float SpawnMargin = 40f;
    private const int   MaxMonsters = 200;

    public int WaveNumber { get; private set; }

    /// <summary>Update() 호출 전 StageComponent가 현재 몬스터 수를 설정한다.</summary>
    public int MonsterCount { get; set; }

    /// <summary>Update() 이후 이번 틱 스폰 목록. 스폰이 없으면 null.</summary>
    public List<(MonsterType Type, float X, float Y)>? LastSpawns { get; private set; }

    private float _elapsed;
    private const float WaveInterval = 8f;

    private static readonly (MonsterType Type, int Count)[][] WaveTable =
    [
        /* Wave 1 */ [(MonsterType.Bat,   5), (MonsterType.Zombie,  3)],
        /* Wave 2 */ [(MonsterType.Bat,   8), (MonsterType.Skeleton, 3)],
        /* Wave 3 */ [(MonsterType.Zombie,6), (MonsterType.Ghost,    3)],
        /* Wave 4 */ [(MonsterType.Bat,  10), (MonsterType.Zombie,   5), (MonsterType.Skeleton, 3)],
        /* Wave 5 */ [(MonsterType.Skeleton,5),(MonsterType.Ghost,   5), (MonsterType.GiantZombie, 1)],
    ];

    private static (MonsterType Type, int Count)[] GetWaveEntries(int waveNumber)
    {
        if (waveNumber <= WaveTable.Length)
            return WaveTable[waveNumber - 1];

        int overage     = waveNumber - WaveTable.Length;
        int batCount    = 8 + overage * 2;
        int zombieCount = 5 + overage;
        var list = new List<(MonsterType, int)>
        {
            (MonsterType.Bat,      Math.Min(batCount, 30)),
            (MonsterType.Zombie,   Math.Min(zombieCount, 15)),
            (MonsterType.Skeleton, 3 + overage / 2),
        };
        if (waveNumber % 5  == 0) list.Add((MonsterType.GiantZombie, 1 + overage / 5));
        if (waveNumber % 10 == 0) list.Add((MonsterType.Reaper, 1));
        return [.. list];
    }

    public override void Initialize()
    {
        _elapsed   = 0f;
        WaveNumber = 0;
        LastSpawns = null;
    }

    protected override void OnDispose() { }

    /// <summary>
    /// 웨이브 틱 처리. 호출 전 MonsterCount 설정 필요.
    /// 웨이브 트리거 시 LastSpawns에 스폰 목록 저장, 아니면 null.
    /// </summary>
    public override void Update(float dt)
    {
        base.Update(dt);
        LastSpawns = null;

        _elapsed += dt;
        if (_elapsed < WaveInterval) return;

        _elapsed -= WaveInterval;
        WaveNumber++;

        var entries   = GetWaveEntries(WaveNumber);
        var spawns    = new List<(MonsterType, float, float)>();
        int remaining = MaxMonsters - MonsterCount;

        foreach (var (type, count) in entries)
        {
            for (int i = 0; i < count && spawns.Count < remaining; i++)
            {
                var (x, y) = RandomEdgePoint();
                spawns.Add((type, x, y));
            }
        }

        LastSpawns = spawns;
    }

    private static (float X, float Y) RandomEdgePoint()
    {
        int edge = Random.Shared.Next(4);
        return edge switch
        {
            0 => (Random.Shared.NextSingle() * MapWidth, -SpawnMargin),
            1 => (Random.Shared.NextSingle() * MapWidth, MapHeight + SpawnMargin),
            2 => (-SpawnMargin,                          Random.Shared.NextSingle() * MapHeight),
            _ => (MapWidth + SpawnMargin,                Random.Shared.NextSingle() * MapHeight),
        };
    }
}
