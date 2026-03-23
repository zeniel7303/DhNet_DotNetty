using Dapper;
using GameServer.Database.Rows;
using GameServer.Database.System;

namespace GameServer.Database.DbSet;

/// <summary>players 테이블 데이터 접근 객체.</summary>
public class PlayerDbSet
{
    private readonly DbConnector _conn;

    public PlayerDbSet(DbConnector conn) => _conn = conn;

    /// <summary>
    /// 로그인 시 플레이어 레코드 삽입.
    /// INSERT IGNORE: account_id 중복(서버 재시작)은 조용히 무시한다.
    /// </summary>
    public Task<int> InsertAsync(PlayerRow row)
    {
        const string sql = @"
            INSERT IGNORE INTO `players`
                (`account_id`, `player_name`, `login_at`, `ip_address`)
            VALUES
                (@account_id, @player_name, @login_at, @ip_address)";
        return _conn.ExecuteAsync(sql, row);
    }

    /// <summary>접속 종료 시 logout_at 업데이트.</summary>
    public Task<int> UpdateLogoutAsync(ulong accountId, DateTime logoutAt)
    {
        const string sql = @"
            UPDATE `players`
            SET `logout_at` = @logout_at
            WHERE `account_id` = @account_id AND `logout_at` IS NULL";
        return _conn.ExecuteAsync(sql, new { account_id = accountId, logout_at = logoutAt });
    }

    // TODO [미래]: GetByNameAsync(string name) - 로그인 시 계정 인증
    // TODO [미래]: GetOnlinePlayersAsync()      - 온라인 플레이어 목록 조회 (logout_at IS NULL)
}
