using GameServer.Database.Rows;
using GameServer.Database.System;

namespace GameServer.Database.DbSet;

/// <summary>stat_logs 테이블 데이터 접근 객체.</summary>
public class StatLogDbSet
{
    private readonly DbConnector _conn;

    public StatLogDbSet(DbConnector conn) => _conn = conn;

    /// <summary>룸 세션 종료 시 통계 기록.</summary>
    public Task<int> InsertAsync(StatLogRow row)
    {
        const string sql = @"
            INSERT INTO `stat_logs`
                (`player_count`, `created_at`)
            VALUES
                (@player_count, @created_at)";
        return _conn.ExecuteAsync(sql, row);
    }

    /// <summary>최근 N개의 플레이어 수 시계열 기록을 반환한다.</summary>
    public Task<IEnumerable<StatLogRow>> GetHistoryAsync(int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 100);
        const string sql = @"
            SELECT `player_count`, `created_at`
            FROM   `stat_logs`
            ORDER  BY `created_at` DESC
            LIMIT  @limit";
        return _conn.QueryAsync<StatLogRow>(sql, new { limit });
    }
}
