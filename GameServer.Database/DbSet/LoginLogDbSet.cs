using GameServer.Database.Rows;
using GameServer.Database.System;

namespace GameServer.Database.DbSet;

/// <summary>login_logs 테이블 데이터 접근 객체.</summary>
public class LoginLogDbSet
{
    private readonly DbConnector _conn;

    public LoginLogDbSet(DbConnector conn) => _conn = conn;

    /// <summary>로그인 시 세션 row 삽입.</summary>
    public Task<int> InsertAsync(LoginLogRow row)
    {
        const string sql = @"
            INSERT INTO `login_logs`
                (`player_id`, `player_name`, `ip_address`, `login_at`)
            VALUES
                (@player_id, @player_name, @ip_address, @login_at)";
        return _conn.ExecuteAsync(sql, row);
    }

    /// <summary>접속 종료 시 가장 최근 미완료 세션의 logout_at 업데이트.</summary>
    public Task<int> UpdateLogoutAsync(ulong playerId, DateTime logoutAt)
    {
        const string sql = @"
            UPDATE `login_logs`
            SET    `logout_at` = @logout_at
            WHERE  `player_id` = @player_id AND `logout_at` IS NULL
            ORDER  BY `login_at` DESC
            LIMIT  1";
        return _conn.ExecuteAsync(sql, new { player_id = playerId, logout_at = logoutAt });
    }
}
