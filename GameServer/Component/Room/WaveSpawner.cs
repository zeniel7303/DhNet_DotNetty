namespace GameServer.Component.Room;

/// <summary>
/// 웨이브 기반 몬스터 스포너.
/// 30초 인터벌로 웨이브 번호를 증가시키며 맵 외곽에서 몬스터를 스폰한다.
/// GameSessionComponent._stateLock 하에서 호출된다.
/// </summary>
public class WaveSpawner
{
    // 맵 크기 — Phase 6에서 3200x2400으로 확장 예정
    private const float MapWidth    = 3200f;
    private const float MapHeight   = 2400f;
    private const float SpawnMargin = 40f;   // 맵 외곽 스폰 오프셋
    private const int   MaxMonsters = 200;   // 룸당 동시 최대 몬스터 수

    public int WaveNumber { get; private set; } = 0;

    private float _elapsed = 0f;
    private const float WaveInterval = 30f; // 초

    // 웨이브 테이블: 웨이브 번호 → (MonsterType, 마리 수)[]
    // 홀수 웨이브: 박쥐/좀비 위주, 짝수 웨이브: 스켈레톤/유령, 5의 배수: 미니보스 포함
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
        // 웨이브 5 이후 반복 (5의 배수마다 GiantZombie 추가, 10의 배수마다 Reaper 추가)
        if (waveNumber <= WaveTable.Length)
            return WaveTable[waveNumber - 1];

        // 반복 주기 — 점점 숫자 증가
        int overage    = waveNumber - WaveTable.Length;
        int batCount   = 8 + overage * 2;
        int zombieCount = 5 + overage;
        var list = new List<(MonsterType, int)>
        {
            (MonsterType.Bat,      Math.Min(batCount, 30)),
            (MonsterType.Zombie,   Math.Min(zombieCount, 15)),
            (MonsterType.Skeleton, 3 + overage / 2),
        };
        if (waveNumber % 5 == 0) list.Add((MonsterType.GiantZombie, 1 + overage / 5));
        if (waveNumber % 10 == 0) list.Add((MonsterType.Reaper, 1));
        return [.. list];
    }

    /// <summary>
    /// 틱 처리. 웨이브 트리거 시 스폰할 (MonsterType, 스폰 좌표)[] 반환.
    /// 웨이브가 아직 없으면 null 반환.
    /// </summary>
    public List<(MonsterType Type, float X, float Y)>? Tick(float dt, int currentMonsterCount)
    {
        _elapsed += dt;
        if (_elapsed < WaveInterval) return null;

        _elapsed -= WaveInterval;
        WaveNumber++;

        var entries  = GetWaveEntries(WaveNumber);
        var spawns   = new List<(MonsterType, float, float)>();
        int remaining = MaxMonsters - currentMonsterCount;

        foreach (var (type, count) in entries)
        {
            for (int i = 0; i < count && spawns.Count < remaining; i++)
            {
                var (x, y) = RandomEdgePoint();
                spawns.Add((type, x, y));
            }
        }

        return spawns;
    }

    /// <summary>맵 외곽 4변 중 랜덤 위치를 반환.</summary>
    private static (float X, float Y) RandomEdgePoint()
    {
        int edge = Random.Shared.Next(4);
        return edge switch
        {
            0 => (Random.Shared.NextSingle() * MapWidth, -SpawnMargin),                // 상단
            1 => (Random.Shared.NextSingle() * MapWidth, MapHeight + SpawnMargin),     // 하단
            2 => (-SpawnMargin,                          Random.Shared.NextSingle() * MapHeight), // 좌측
            _ => (MapWidth + SpawnMargin,                Random.Shared.NextSingle() * MapHeight), // 우측
        };
    }
}
