using Dapper;
using GameServer.Database.Rows;
using GameServer.Database.System;

namespace GameServer.Database.DbSet;

/// <summary>password_reset_tokens 테이블 데이터 접근 객체.</summary>
public class PasswordResetTokenDbSet
{
    private readonly DbConnector _conn;

    public PasswordResetTokenDbSet(DbConnector conn) => _conn = conn;

    /// <summary>새 토큰 삽입.</summary>
    public Task<int> InsertAsync(PasswordResetTokenRow row)
    {
        const string sql = @"
            INSERT INTO `password_reset_tokens`
                (`account_id`, `token`, `expires_at`)
            VALUES
                (@account_id, @token, @expires_at)";
        return _conn.ExecuteAsync(sql, row);
    }

    /// <summary>토큰 문자열로 조회. 없으면 null 반환.</summary>
    public Task<PasswordResetTokenRow?> SelectByTokenAsync(string token)
    {
        const string sql = @"
            SELECT `token_id`, `account_id`, `token`, `expires_at`, `used_at`
            FROM `password_reset_tokens`
            WHERE `token` = @token
            LIMIT 1";
        return _conn.QuerySingleOrDefaultAsync<PasswordResetTokenRow>(sql, new { token });
    }

    /// <summary>
    /// 토큰을 원자적으로 소모. used_at IS NULL AND 미만료인 경우에만 UPDATE.
    /// 반환값 0 → 이미 소모됐거나 만료됨 (동시 요청 방어).
    /// </summary>
    public Task<int> MarkUsedConditionalAsync(ulong tokenId)
    {
        const string sql = @"
            UPDATE `password_reset_tokens`
            SET `used_at` = UTC_TIMESTAMP()
            WHERE `token_id` = @tokenId
              AND `used_at`   IS NULL
              AND `expires_at` > UTC_TIMESTAMP()";
        return _conn.ExecuteAsync(sql, new { tokenId });
    }

    /// <summary>만료된 토큰 정리. 요청마다 호출하여 누적 방지.</summary>
    public Task<int> DeleteExpiredAsync()
    {
        const string sql = @"
            DELETE FROM `password_reset_tokens`
            WHERE `expires_at` < UTC_TIMESTAMP()";
        return _conn.ExecuteAsync(sql);
    }
}
