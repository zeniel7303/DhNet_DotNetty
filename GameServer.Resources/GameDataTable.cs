using Common.Logging;
using System.Text.Json;

namespace GameServer.Resources;

/// <summary>
/// JSON 기반 게임 데이터 테이블.
/// ServerStartup에서 Load()를 1회 호출한 뒤 각 컴포넌트가 정적 프로퍼티로 읽는다.
/// Load() 완료 이후의 읽기 접근은 thread-safe하다 (쓰기 없는 불변 참조).
/// 운영 중 hot-reload가 필요한 경우 Interlocked.Exchange 기반 스냅샷 교체 패턴으로 전환해야 한다.
/// </summary>
public static class GameDataTable
{
    public record MonsterStat(
        int   MaxHp, int Atk, int Def, int ExpReward, int GoldReward,
        float Speed, float AttackRange, float HitRadius, float RespawnDelay, float AttackInterval);

    public record WeaponStat(
        int   Damage, float CooldownSec,
        float UpgradeMultDamage, float UpgradeMultCooldown, float CooldownMin);

    public record WaveEntry(int WaveNumber, string MonsterType, int Count);

    public static IReadOnlyDictionary<string, MonsterStat> Monsters { get; private set; }
        = new Dictionary<string, MonsterStat>();

    public static IReadOnlyDictionary<string, WeaponStat> Weapons { get; private set; }
        = new Dictionary<string, WeaponStat>();

    /// <summary>
    /// Waves[0] = 1웨이브 항목 배열, Waves[4] = 5웨이브 항목 배열.
    /// waveNumber 기반 조회: Waves[waveNumber - 1]
    /// </summary>
    public static WaveEntry[][] Waves { get; private set; } = [];

    /// <summary>웨이브 트리거 간격 (초). config.json의 waveInterval 값.</summary>
    public static float WaveInterval { get; private set; } = 8f;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// JSON 파일에서 게임 데이터를 로드한다. 실패 시 예외를 throw하여 서버 시작을 중단시킨다.
    /// </summary>
    /// <param name="resourceDir">monsters.json, weapons.json, waves.json이 있는 디렉토리 경로.</param>
    public static void Load(string resourceDir)
    {
        Monsters = LoadDict<MonsterStat>(Path.Combine(resourceDir, "monsters.json"));
        Weapons  = LoadDict<WeaponStat>(Path.Combine(resourceDir, "weapons.json"));
        Waves    = LoadWaves(Path.Combine(resourceDir, "waves.json"));
        LoadConfig(Path.Combine(resourceDir, "config.json"));
    }

    // config.json 역직렬화 전용 레코드. 키 누락 시 null → 기본값 폴백 + 경고.
    private record ConfigDto(float? WaveInterval = null);

    private static void LoadConfig(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"GameDataTable: 파일 없음 — {path}");

        var json   = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ConfigDto>(json, JsonOpts)
            ?? throw new InvalidOperationException($"GameDataTable: 파싱 실패 — {path}");

        if (config.WaveInterval.HasValue)
            WaveInterval = config.WaveInterval.Value;
        else
            GameLogger.Warn("GameDataTable", "config.json에 'waveInterval'이 없어 기본값 8초를 사용합니다.");
    }

    private static IReadOnlyDictionary<string, T> LoadDict<T>(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"GameDataTable: 파일 없음 — {path}");

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Dictionary<string, T>>(json, JsonOpts)
            ?? throw new InvalidOperationException($"GameDataTable: 파싱 실패 — {path}");
    }

    private static WaveEntry[][] LoadWaves(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"GameDataTable: 파일 없음 — {path}");

        var json    = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<WaveEntry[]>(json, JsonOpts)
            ?? throw new InvalidOperationException($"GameDataTable: 파싱 실패 — {path}");

        int maxWave = entries.Length > 0 ? entries.Max(e => e.WaveNumber) : 0;
        var result  = new WaveEntry[maxWave][];
        for (int i = 0; i < maxWave; i++)
        {
            int waveNum = i + 1;
            result[i] = entries.Where(e => e.WaveNumber == waveNum).ToArray();
            if (result[i].Length == 0)
                GameLogger.Warn("GameDataTable", $"웨이브 {waveNum}에 몬스터 항목이 없습니다. waves.json을 확인하세요.");
        }
        return result;
    }
}
