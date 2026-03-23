using Dapper;
using GameServer.Database.Rows;
using GameServer.Database.System;

namespace GameServer.Database.DbSet;

/// <summary>accounts 테이블 데이터 접근 객체.</summary>
public class AccountDbSet
{
    private readonly DbConnector _conn;

    public AccountDbSet(DbConnector conn) => _conn = conn;

    /// <summary>
    /// 계정 생성. account_id는 호출자가 IdGenerators.Account.Next()로 생성하여 전달한다.
    /// INSERT IGNORE: username 중복 시 0 반환 (rows affected).
    /// </summary>
    public Task<int> InsertAsync(AccountRow row)
    {
        const string sql = @"
            INSERT IGNORE INTO `accounts`
                (`account_id`, `username`, `password_hash`, `created_at`)
            VALUES
                (@account_id, @username, @password_hash, @created_at)";
        return _conn.ExecuteAsync(sql, row);
    }

    /// <summary>username으로 계정 조회. 없으면 null 반환.</summary>
    public Task<AccountRow?> SelectByUsernameAsync(string username)
    {
        const string sql = @"
            SELECT `account_id`, `username`, `password_hash`, `created_at`
            FROM `accounts`
            WHERE `username` = @username
            LIMIT 1";
        return _conn.QuerySingleOrDefaultAsync<AccountRow>(sql, new { username });
    }

    /// <summary>서버 시작 시 IdGenerators.Account 초기화에 사용.</summary>
    public Task<ulong> GetMaxAccountIdAsync()
    {
        const string sql = "SELECT COALESCE(MAX(`account_id`), 0) FROM `accounts`";
        return _conn.ExecuteScalarAsync<ulong>(sql)!;
    }
}
