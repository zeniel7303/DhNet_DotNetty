using Common.Logging;
using GameServer.Database;
using GameServer.Database.Rows;
using GameServer.Protocol;

namespace GameServer.Network;

/// <summary>
/// 회원가입 처리. ReqRegister 패킷을 받아 계정을 생성하고 ResRegister를 반환한다.
/// DB에는 Phase 2에서 평문 password를 저장하며, Phase 3(BCrypt)에서 해시로 교체된다.
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

        // username 중복 확인 (SELECT 먼저 → 에러 메시지를 더 빠르게 반환)
        AccountRow? existing;
        try
        {
            existing = await DatabaseSystem.Instance.Game.Accounts.SelectByUsernameAsync(username);
        }
        catch (Exception ex)
        {
            GameLogger.Error("Register", $"DB 조회 실패: {username}", ex);
            await session.SendAsync(new GamePacket
            {
                ResRegister = new ResRegister { AccountId = 0, ErrorCode = ErrorCode.DbError }
            });
            return;
        }

        if (existing != null)
        {
            GameLogger.Warn("Register", $"중복 username: {username}");
            await session.SendAsync(new GamePacket
            {
                ResRegister = new ResRegister { AccountId = 0, ErrorCode = ErrorCode.UsernameTaken }
            });
            return;
        }

        // 계정 삽입. INSERT IGNORE: 동시 요청에 의한 중복도 안전하게 처리.
        int inserted;
        try
        {
            inserted = await DatabaseSystem.Instance.Game.Accounts.InsertAsync(new AccountRow
            {
                username      = username,
                password_hash = password,  // Phase 3에서 BCrypt.HashPassword(password, 11)로 교체
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

        // 생성된 account_id 조회
        AccountRow? created;
        try
        {
            created = await DatabaseSystem.Instance.Game.Accounts.SelectByUsernameAsync(username);
        }
        catch (Exception ex)
        {
            GameLogger.Error("Register", $"account_id 조회 실패: {username}", ex);
            await session.SendAsync(new GamePacket
            {
                ResRegister = new ResRegister { AccountId = 0, ErrorCode = ErrorCode.DbError }
            });
            return;
        }

        if (created == null)
        {
            GameLogger.Error("Register", $"INSERT 성공 후 SELECT 실패: {username}");
            await session.SendAsync(new GamePacket
            {
                ResRegister = new ResRegister { AccountId = 0, ErrorCode = ErrorCode.DbError }
            });
            return;
        }

        GameLogger.Info("Register", $"계정 생성 완료: {username} (account_id={created.account_id})");
        await session.SendAsync(new GamePacket
        {
            ResRegister = new ResRegister
            {
                AccountId = created.account_id,
                ErrorCode = ErrorCode.Success
            }
        });
    }
}
