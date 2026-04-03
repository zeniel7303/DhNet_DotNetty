using Common.Logging;
using GameServer.Database;
using GameServer.Network;
using GameServer.Database.Rows;
using GameServer.Protocol;
using GameServer.Systems;

namespace GameServer.Auth;

/// <summary>
/// 회원가입 처리. ReqRegister 패킷을 받아 계정을 생성하고 ResRegister를 반환한다.
/// account_id는 IdGenerators.Account.Next()로 미리 생성하여 INSERT에 포함하므로
/// INSERT 후 SELECT가 불필요하다.
/// 비밀번호는 BCrypt(workFactor=11)로 해시하여 저장한다.
/// </summary>
internal static class RegisterProcessor
{
    private const int MinLength = 4;
    private const int MaxLength = 16;

    public static async Task ProcessAsync(SessionComponent session, ReqRegister req)
    {
        var username = req.Username.Trim();
        var password = req.Password;

        // username 길이 검증 (4~16자)
        if (username.Length < MinLength || username.Length > MaxLength)
        {
            GameLogger.Warn("Register", $"username 길이 위반: len={username.Length}");
            await session.SendAsync(new GamePacket
            {
                ResRegister = new ResRegister { AccountId = 0, ErrorCode = ErrorCode.InvalidUsernameLength }
            });
            return;
        }

        // 비밀번호 길이 검증 (4~16자)
        if (password.Length < MinLength || password.Length > MaxLength)
        {
            GameLogger.Warn("Register", $"비밀번호 길이 위반: len={password.Length}");
            await session.SendAsync(new GamePacket
            {
                ResRegister = new ResRegister
                {
                    AccountId = 0,
                    ErrorCode = ErrorCode.InvalidPasswordLength
                }
            });
            return;
        }

        // account_id를 미리 생성하여 INSERT에 포함 — SELECT 불필요
        var accountId = IdGenerators.Account.Next();

        // BCrypt.HashPassword는 블로킹 연산(~200ms) — Task.Run으로 ThreadPool 분리
        var passwordHash = await Task.Run(() => BCrypt.Net.BCrypt.HashPassword(password, workFactor: AuthConstants.BcryptWorkFactor));

        // 계정 삽입. INSERT IGNORE: 중복 username이면 rows_affected == 0 반환.
        // SELECT-then-INSERT 패턴은 TOCTOU race condition을 유발하므로 사용하지 않는다.
        int inserted;
        try
        {
            inserted = await DatabaseSystem.Instance.Game.Accounts.InsertAsync(new AccountRow
            {
                account_id    = accountId,
                username      = username,
                password_hash = passwordHash,
                created_at    = DateTime.UtcNow
            });
        }
        catch (Exception ex)
        {
            GameLogger.Error("Register", $"DB 저장 실패: {username}", ex);
            await session.SendAsync(new GamePacket
            {
                ResRegister = new ResRegister { AccountId = 0, ErrorCode = ErrorCode.DbError }
            });
            return;
        }

        // rows_affected == 0 : 동시 요청에 의한 중복 (INSERT IGNORE)
        if (inserted == 0)
        {
            await session.SendAsync(new GamePacket
            {
                ResRegister = new ResRegister { AccountId = 0, ErrorCode = ErrorCode.UsernameTaken }
            });
            return;
        }

        GameLogger.Info("Register", $"계정 생성 완료: {username} (account_id={accountId})");
        await session.SendAsync(new GamePacket
        {
            ResRegister = new ResRegister
            {
                AccountId = accountId,
                ErrorCode = ErrorCode.Success
            }
        });
    }
}
