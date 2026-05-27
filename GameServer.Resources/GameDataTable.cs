using Common.Logging;
using System.Text.Json;

namespace GameServer.Resources;

/// <summary>
/// JSON 기반 게임 데이터 테이블.
/// ServerStartup에서 Load()를 1회 호출한 뒤 각 컴포넌트가 정적 프로퍼티로 읽는다.
/// Load() 완료 이후의 읽기 접근은 thread-safe하다 (쓰기 없는 불변 참조).
/// </summary>
public static class GameDataTable
{
    // ── 레코드 정의 ──────────────────────────────────────────────────

    public record MonsterStat(
        int   MaxHp, int Atk, int Def, int ExpReward, int GoldReward,
        float Speed, float AttackRange, float HitRadius, float RespawnDelay, float AttackInterval);

    public record WeaponStat(
        int   Damage, float CooldownSec,
        float UpgradeMultDamage, float UpgradeMultCooldown, float CooldownMin,
        float[][]? SpreadOffsets = null,
        // 투사체 공통
        float? HitRadius          = null,
        float? ProjectileSpeed    = null,
        float? ProjectileLifetime = null,
        int?   MaxProjectiles     = null,
        // 마늘 전용
        float? AuraRadius         = null,
        float? KnockbackDist      = null,
        float? UpgradeAuraRadius  = null,
        // 성경 전용
        float? OrbitRadius        = null,
        float? PerEnemyCooldown   = null,
        float? AngularSpeedRad    = null,
        // 도끼 전용
        float? Gravity            = null,
        float? HorizontalSpeed    = null,
        float? VerticalSpeed      = null,
        // 십자가 전용
        float? MaxDist            = null);

    public record WaveEntry(int WaveNumber, string MonsterType, int Count);

    /// <summary>맵 물리 설정 — config.json에서 로드.</summary>
    public record MapConfig(
        float MapWidth, float MapHeight,
        float WaveScalePerWave,
        float MaxDtSec);

    /// <summary>플레이어 초기 스탯 및 업그레이드 파라미터 — player.json에서 로드.</summary>
    public record PlayerConfig(
        int   InitialHp,
        int   InitialAttack,
        int   InitialDefense,
        float InitialMoveSpeed,
        float MaxMoveSpeed,
        int   LevelExpCoeff,
        int   AttackUpAmount,
        int   AttackUpCap,
        int   MaxHpUpAmount,
        int   MaxHpUpCap,
        float MoveSpeedUpAmount,
        float ExpMultiUpFactor,
        float ExpMultiUpCap,
        float ExpRadiusUpAmount,
        float ExpRadiusUpCap);

    // ── 정적 프로퍼티 ─────────────────────────────────────────────────

    public static IReadOnlyDictionary<string, MonsterStat> Monsters { get; private set; }
        = new Dictionary<string, MonsterStat>();

    public static IReadOnlyDictionary<string, WeaponStat> Weapons { get; private set; }
        = new Dictionary<string, WeaponStat>();

    public static WaveEntry[][] Waves { get; private set; } = [];

    /// <summary>웨이브 트리거 간격 (초). config.json의 waveInterval 값.</summary>
    public static float WaveInterval { get; private set; } = 8f;

    public static MapConfig Map { get; private set; }
        = new(3200f, 2400f, 0.08f, 0.1f);

    public static PlayerConfig Player { get; private set; }
        = new(500, 20, 10, 300f, 500f, 15, 2, 80, 25, 1000, 25f, 1.10f, 2.5f, 15f, 120f);

    // ── 내부 DTO ─────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
    };

    private record ConfigDto(
        float? WaveInterval      = null,
        float? MapWidth          = null,
        float? MapHeight         = null,
        float? WaveScalePerWave  = null,
        float? MaxDtSec          = null);

    // ── 로드 ─────────────────────────────────────────────────────────

    /// <summary>
    /// JSON 파일에서 게임 데이터를 로드한다. 실패 시 예외를 throw하여 서버 시작을 중단시킨다.
    /// </summary>
    /// <param name="resourceDir">리소스 JSON 파일들이 있는 디렉토리 경로.</param>
    public static void Load(string resourceDir)
    {
        Monsters = LoadDict<MonsterStat>(Path.Combine(resourceDir, "monsters.json"));
        Weapons  = LoadDict<WeaponStat>(Path.Combine(resourceDir, "weapons.json"));
        Waves    = LoadWaves(Path.Combine(resourceDir, "waves.json"));
        LoadConfig(Path.Combine(resourceDir, "config.json"));
        LoadPlayer(Path.Combine(resourceDir, "player.json"));
    }

    private static void LoadConfig(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"GameDataTable: 파일 없음 — {path}");

        var json   = File.ReadAllText(path);
        var config = JsonSerializer.Deserialize<ConfigDto>(json, JsonOpts)
            ?? throw new InvalidOperationException($"GameDataTable: 파싱 실패 — {path}");

        float waveInterval     = config.WaveInterval     ?? WaveInterval;
        float mapWidth         = config.MapWidth         ?? Map.MapWidth;
        float mapHeight        = config.MapHeight        ?? Map.MapHeight;
        float waveScalePerWave = config.WaveScalePerWave ?? Map.WaveScalePerWave;
        float maxDtSec         = config.MaxDtSec         ?? Map.MaxDtSec;

        if (!config.WaveInterval.HasValue)
            GameLogger.Warn("GameDataTable", "config.json에 'waveInterval'이 없어 기본값 8초를 사용합니다.");

        WaveInterval = waveInterval;
        Map          = new MapConfig(mapWidth, mapHeight, waveScalePerWave, maxDtSec);
    }

    private static void LoadPlayer(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"GameDataTable: 파일 없음 — {path}");

        var json = File.ReadAllText(path);
        var p    = JsonSerializer.Deserialize<PlayerConfig>(json, JsonOpts)
            ?? throw new InvalidOperationException($"GameDataTable: 파싱 실패 — {path}");

        Player = p;
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
