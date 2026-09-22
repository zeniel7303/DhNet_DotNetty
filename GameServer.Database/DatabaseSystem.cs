using Common;
using Common.Logging;
using GameServer.Database.System;
using MySqlConnector;

namespace GameServer.Database;

/// <summary>
/// DB 레이어의 싱글톤 진입점.
/// 기존 PlayerSystem / LobbySystem 패턴과 동일하게 Instance로 접근한다.
/// TODO [미래]: GlobalDbContext Global 추가 (계정 인증, 블랙리스트 등)
/// TODO [미래]: 멀티월드 지원 시 ConcurrentDictionary 기반 컨텍스트 관리
/// </summary>
public class DatabaseSystem
{
    public static readonly DatabaseSystem Instance = new();

    public GameDbContext Game { get; private set; } = null!;
    public GameLogContext GameLog { get; private set; } = null!;

    private DbConnector? _gameConnector;
    private DbConnector? _logConnector;

    private DatabaseSystem() { }

    /// <summary>
    /// 서버 시작 시 한 번 호출. DB 연결을 초기화하고 IdGenerator 초기화에 필요한 max 값을 반환한다.
    /// </summary>
    /// <returns>DB에서 읽은 max account_id, max room_id</returns>
    public async Task<DbInitResult> InitializeAsync(DatabaseSettings settings)
    {
        var gameConnStr = BuildConnectionString(settings, settings.Database);
        var logConnStr  = BuildConnectionString(settings, settings.LogDatabase);

        var connector = new DbConnector(gameConnStr);
        var logConnector = new DbConnector(logConnStr);
        _gameConnector = connector;
        _logConnector = logConnector;

        Game = new GameDbContext(connector);
        GameLog = new GameLogContext(logConnector);

        // 두 DB 연결 병렬 확인
        await Task.WhenAll(
            PingAsync(connector, settings.Database, settings.RequireConnection),
            PingAsync(logConnector, settings.LogDatabase, settings.RequireConnection));

        return await ReadIdSeedsAsync();
    }

    /// <summary>게임 서버가 ID를 이어받을 때 호출한다. 실패하면 0이다.</summary>
    public async Task<DbInitResult> ReadIdSeedsAsync()
    {
        ulong maxAccountId = 0;
        ulong maxRoomId = 0;
        try
        {
            maxAccountId = await Game.Accounts.GetMaxAccountIdAsync();
            maxRoomId    = await GameLog.RoomLogs.GetMaxRoomIdAsync();
        }
        catch (Exception ex)
        {
            GameLogger.Warn("DatabaseSystem", $"max ID 조회 실패 — IdGenerators는 1부터 시작합니다. ({ex.Message})");
        }

        return new DbInitResult(maxAccountId, maxRoomId);
    }

    /// <summary>게임 DB와 로그 DB가 모두 응답하면 true.</summary>
    public async Task<bool> CheckHealthAsync()
    {
        if (_gameConnector == null || _logConnector == null)
        {
            return false;
        }

        var game = await _gameConnector.PingAsync();
        var log = await _logConnector.PingAsync();
        return game && log;
    }

    private static async Task PingAsync(DbConnector connector, string dbName, bool requireConnection)
    {
        if (await connector.PingAsync())
        {
            GameLogger.Info("DatabaseSystem", $"DB 연결 성공: {dbName}");
        }
        else
        {
            var msg = $"DB 연결 실패: {dbName}";
            if (requireConnection)
            {
                throw new InvalidOperationException(msg);
            }
            GameLogger.Warn("DatabaseSystem", msg + " - 서버는 계속 실행됩니다.");
        }
    }

    private static string BuildConnectionString(DatabaseSettings s, string database)
    {
        var builder = new MySqlConnectionStringBuilder
        {
            Server                  = s.Host,
            Port                    = (uint)s.Port,
            Database                = database,
            UserID                  = s.UserId,
            Password                = s.Password,
            CharacterSet            = "utf8mb4",
            ConvertZeroDateTime     = true,
            AllowPublicKeyRetrieval = true,
            SslMode                 = MySqlSslMode.None,
            Pooling                 = true,
            MinimumPoolSize         = (uint)Math.Clamp(s.MinPoolSize, 0, 1000),
            MaximumPoolSize         = (uint)Math.Clamp(s.MaxPoolSize, 1, 10_000),
            ConnectionTimeout       = (uint)Math.Clamp(s.ConnectionTimeoutSeconds, 1, 120)
        };
        return builder.ConnectionString;
    }
}

/// <summary>서버 시작 시 DB에서 읽어온 max ID 값. Program.cs에서 IdGenerators 초기화에 사용.</summary>
public record DbInitResult(ulong MaxAccountId, ulong MaxRoomId);
