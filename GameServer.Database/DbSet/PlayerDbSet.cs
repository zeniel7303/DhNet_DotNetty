using Dapper;
using GameServer.Database.Rows;
using GameServer.Database.System;
using MySqlConnector;

namespace GameServer.Database.DbSet;

/// <summary>players 테이블 데이터 접근 객체.</summary>
public class PlayerDbSet
{
    private readonly DbConnector _conn;

    public PlayerDbSet(DbConnector conn) => _conn = conn;

    /// <summary>
    /// 로그인 시 플레이어 레코드 삽입.
    /// INSERT IGNORE: player_id 중복(서버 재시작)은 조용히 무시한다.
    /// </summary>
    public Task<int> InsertAsync(PlayerRow row)
    {
        const string sql = @"
            INSERT IGNORE INTO `players`
                (`player_id`, `player_name`, `login_at`, `ip_address`, `account_id`)
            VALUES
                (@player_id, @player_name, @login_at, @ip_address, @account_id)";
        return _conn.ExecuteAsync(sql, row);
    }

    /// <summary>접속 종료 시 logout_at 업데이트.</summary>
    public Task<int> UpdateLogoutAsync(ulong playerId, DateTime logoutAt)
    {
        const string sql = @"
            UPDATE `players`
            SET `logout_at` = @logout_at
            WHERE `player_id` = @player_id AND `logout_at` IS NULL";
        return _conn.ExecuteAsync(sql, new { player_id = playerId, logout_at = logoutAt });
    }

    /// <summary>서버 시작 시 IdGenerator 초기화에 사용.</summary>
    public Task<ulong> GetMaxPlayerIdAsync()
    {
        const string sql = "SELECT COALESCE(MAX(`player_id`), 0) FROM `players`";
        return _conn.ExecuteScalarAsync<ulong>(sql)!;
    }

    // TODO [미래]: GetByNameAsync(string name) - 로그인 시 계정 인증
    // TODO [미래]: GetOnlinePlayersAsync()      - 온라인 플레이어 목록 조회 (logout_at IS NULL)
}
