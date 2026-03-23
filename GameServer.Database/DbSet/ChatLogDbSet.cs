using Dapper;
using GameServer.Database.Rows;
using GameServer.Database.System;

namespace GameServer.Database.DbSet;

/// <summary>chat_logs 테이블 데이터 접근 객체.</summary>
public class ChatLogDbSet
{
    private readonly DbConnector _conn;

    public ChatLogDbSet(DbConnector conn) => _conn = conn;

    /// <summary>채팅 메시지 기록. 로비/룸 모두 이 메서드를 사용한다.</summary>
    public Task<int> InsertAsync(ChatLogRow row)
    {
        const string sql = @"
            INSERT INTO `chat_logs`
                (`account_id`, `room_id`, `channel`, `message`, `created_at`)
            VALUES
                (@account_id, @room_id, @channel, @message, @created_at)";
        return _conn.ExecuteAsync(sql, row);
    }

    /// <summary>채팅 로그 조회. 모든 필터는 선택사항이며 최대 100건 반환.</summary>
    public Task<IEnumerable<ChatLogRow>> QueryAsync(
        ulong? accountId = null,
        ulong? roomId = null,
        DateTime? startTime = null,
        DateTime? endTime = null,
        int limit = 100)
    {
        limit = Math.Clamp(limit, 1, 100);
        var where = new List<string>();
        var param = new DynamicParameters();

        if (accountId.HasValue)  { where.Add("`account_id` = @account_id");      param.Add("account_id", accountId.Value); }
        if (roomId.HasValue)     { where.Add("`room_id` = @room_id");            param.Add("room_id",    roomId.Value); }
        if (startTime.HasValue)  { where.Add("`created_at` >= @start_time");     param.Add("start_time", startTime.Value); }
        if (endTime.HasValue)    { where.Add("`created_at` <= @end_time");       param.Add("end_time",   endTime.Value); }

        var sql = "SELECT `account_id`, `room_id`, `channel`, `message`, `created_at` FROM `chat_logs`";
        if (where.Count > 0) sql += " WHERE " + string.Join(" AND ", where);
        sql += " ORDER BY `created_at` DESC LIMIT @limit";
        param.Add("limit", limit);

        return _conn.QueryAsync<ChatLogRow>(sql, param);
    }
}
