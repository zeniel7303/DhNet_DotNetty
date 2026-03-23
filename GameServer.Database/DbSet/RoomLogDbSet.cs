using Dapper;
using GameServer.Database.Rows;
using GameServer.Database.System;

namespace GameServer.Database.DbSet;

/// <summary>room_logs 테이블 데이터 접근 객체.</summary>
public class RoomLogDbSet
{
    private readonly DbConnector _conn;

    public RoomLogDbSet(DbConnector conn) => _conn = conn;

    /// <summary>룸 입퇴장 이벤트 기록.</summary>
    public Task<int> InsertAsync(RoomLogRow row)
    {
        const string sql = @"
            INSERT INTO `room_logs`
                (`account_id`, `room_id`, `action`, `created_at`)
            VALUES
                (@account_id, @room_id, @action, @created_at)";
        return _conn.ExecuteAsync(sql, row);
    }

    /// <summary>서버 시작 시 IdGenerators.Room 초기화에 사용.</summary>
    public Task<ulong> GetMaxRoomIdAsync()
    {
        const string sql = "SELECT COALESCE(MAX(`room_id`), 0) FROM `room_logs`";
        return _conn.ExecuteScalarAsync<ulong>(sql)!;
    }

    /// <summary>룸 이벤트 로그 조회. 모든 필터는 선택사항이며 최대 100건 반환.</summary>
    public Task<IEnumerable<RoomLogRow>> QueryAsync(
        ulong? accountId = null,
        ulong? roomId = null,
        string? action = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 100);
        var where = new List<string>();
        var param = new DynamicParameters();

        if (accountId.HasValue)             { where.Add("`account_id` = @account_id");  param.Add("account_id", accountId.Value); }
        if (roomId.HasValue)                { where.Add("`room_id` = @room_id");        param.Add("room_id",    roomId.Value); }
        if (!string.IsNullOrEmpty(action))  { where.Add("`action` = @action");          param.Add("action",     action); }
        if (startTime.HasValue)             { where.Add("`created_at` >= @start_time"); param.Add("start_time", startTime.Value); }
        if (endTime.HasValue)               { where.Add("`created_at` <= @end_time");   param.Add("end_time",   endTime.Value); }

        var sql = "SELECT `account_id`, `room_id`, `action`, `created_at` FROM `room_logs`";
        if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
        sql += " ORDER BY `created_at` DESC LIMIT @limit";
        param.Add("limit", limit);

        return _conn.QueryAsync<RoomLogRow>(sql, param);
    }
}
