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
                (`player_id`, `room_id`, `action`, `created_at`)
            VALUES
                (@player_id, @room_id, @action, @created_at)";
        return _conn.ExecuteAsync(sql, row);
    }

    /// <summary>서버 시작 시 IdGenerators.Room 초기화에 사용.</summary>
    public Task<ulong> GetMaxRoomIdAsync()
    {
        const string sql = "SELECT COALESCE(MAX(`room_id`), 0) FROM `room_logs`";
        return _conn.ExecuteScalarAsync<ulong>(sql)!;
    }

    // TODO [미래]: GetByPlayerAsync(ulong playerId) - 플레이어별 룸 이력 조회
    // TODO [미래]: GetByRoomAsync(ulong roomId)     - 특정 룸의 입퇴장 이력 조회
}
