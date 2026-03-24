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

    /// <summary>캐릭터 저장. 최초 생성 및 로그아웃 시 호출 (INSERT ON DUPLICATE KEY UPDATE).</summary>
    public Task<int> UpsertAsync(CharacterRow row)
    {
        const string sql = @"
            INSERT INTO `characters`
                (`account_id`, `level`, `exp`, `hp`, `max_hp`, `attack`, `defense`, `x`, `y`)
            VALUES
                (@account_id, @level, @exp, @hp, @max_hp, @attack, @defense, @x, @y)
            ON DUPLICATE KEY UPDATE
                `level`   = VALUES(`level`),
                `exp`     = VALUES(`exp`),
                `hp`      = VALUES(`hp`),
                `max_hp`  = VALUES(`max_hp`),
                `attack`  = VALUES(`attack`),
                `defense` = VALUES(`defense`),
                `x`       = VALUES(`x`),
                `y`       = VALUES(`y`)";
        return _conn.ExecuteAsync(sql, row);
    }
}
