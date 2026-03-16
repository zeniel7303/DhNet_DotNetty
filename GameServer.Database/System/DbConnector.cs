using System.Data;
using Dapper;
using MySqlConnector;

namespace GameServer.Database.System;

/// <summary>
/// MySqlConnector + Dapper 래퍼.
/// 연결 생성, 쿼리 실행, 트랜잭션 처리를 담당한다.
/// </summary>
public class DbConnector
{
    private readonly string _connectionString;

    public DbConnector(string connectionString)
    {
        _connectionString = connectionString;
    }

    private MySqlConnection CreateConnection() => new(_connectionString);

    // ──────────────────────────────────────────────
    // 단순 쿼리 (자동 연결 열기/닫기)
    // ──────────────────────────────────────────────

    public async Task<IEnumerable<T>> QueryAsync<T>(string sql, object? param = null)
    {
        await using var conn = CreateConnection();
        return await conn.QueryAsync<T>(sql, param);
    }

    public async Task<T?> QuerySingleOrDefaultAsync<T>(string sql, object? param = null)
    {
        await using var conn = CreateConnection();
        return await conn.QuerySingleOrDefaultAsync<T>(sql, param);
    }

    public async Task<int> ExecuteAsync(string sql, object? param = null)
    {
        await using var conn = CreateConnection();
        return await conn.ExecuteAsync(sql, param);
    }

    public async Task<T?> ExecuteScalarAsync<T>(string sql, object? param = null)
    {
        await using var conn = CreateConnection();
        return await conn.ExecuteScalarAsync<T>(sql, param);
    }

    // ──────────────────────────────────────────────
    // 연결 재사용 (단일 연결로 여러 쿼리 실행)
    // ──────────────────────────────────────────────

    public async Task WithConnAsync(Func<MySqlConnection, Task> handler)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await handler(conn);
    }

    public async Task<T> WithConnAsync<T>(Func<MySqlConnection, Task<T>> handler)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        return await handler(conn);
    }

    // ──────────────────────────────────────────────
    // 트랜잭션
    // TODO [미래]: 아이템 거래, 계정 삭제 등 복합 로직에서 사용
    // ──────────────────────────────────────────────

    public async Task WithTransactAsync(Func<MySqlConnection, MySqlTransaction, Task> handler)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await handler(conn, tx);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    public async Task<T> WithTransactAsync<T>(Func<MySqlConnection, MySqlTransaction, Task<T>> handler)
    {
        await using var conn = CreateConnection();
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            var result = await handler(conn, tx);
            await tx.CommitAsync();
            return result;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>연결 테스트. 서버 시작 시 DB 연결 가능 여부 확인에 사용.</summary>
    public async Task<bool> PingAsync()
    {
        try
        {
            await using var conn = CreateConnection();
            await conn.OpenAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
