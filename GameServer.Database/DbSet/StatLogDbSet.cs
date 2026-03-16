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
}
