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
                (`player_id`, `room_id`, `channel`, `message`, `created_at`)
            VALUES
                (@player_id, @room_id, @channel, @message, @created_at)";
        return _conn.ExecuteAsync(sql, row);
    }
}
