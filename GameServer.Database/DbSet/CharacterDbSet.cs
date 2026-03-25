using GameServer.Database.Rows;
using GameServer.Database.System;

namespace GameServer.Database.DbSet;

/// <summary>characters 테이블 데이터 접근 객체.</summary>
public class CharacterDbSet
{
    private readonly DbConnector _conn;

    public CharacterDbSet(DbConnector conn) => _conn = conn;

    /// <summary>계정 ID로 캐릭터 로드. 없으면 null 반환.</summary>
    public Task<CharacterRow?> SelectAsync(ulong accountId)
    {
        const string sql = "SELECT * FROM `characters` WHERE `account_id` = @account_id LIMIT 1";
        return _conn.QuerySingleOrDefaultAsync<CharacterRow>(sql, new { account_id = accountId });
    }

    /// <summary>골드 저장. 로그아웃 시 호출 (INSERT ON DUPLICATE KEY UPDATE).</summary>
    public Task<int> UpsertAsync(CharacterRow row)
    {
        const string sql = @"
            INSERT INTO `characters` (`account_id`, `gold`)
            VALUES (@account_id, @gold)
            ON DUPLICATE KEY UPDATE `gold` = VALUES(`gold`)";
        return _conn.ExecuteAsync(sql, row);
    }
}
