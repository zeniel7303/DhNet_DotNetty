using GameServer.Database.Grpc;
using GameServer.Database.Rows;

namespace GameServer.Database;

/// <summary>Row와 gRPC 메시지 사이의 변환. 시각은 틱을 그대로 옮긴다.</summary>
public static class DbGatewayMapper
{
    public static long Ticks(DateTime value) => value.Ticks;

    public static DateTime FromTicks(long ticks) => new(ticks, DateTimeKind.Utc);

    public static DateTime? OptionalTicks(bool hasValue, long ticks)
        => hasValue ? FromTicks(ticks) : null;

    public static AccountMessage ToMessage(AccountRow row)
    {
        var message = new AccountMessage
        {
            AccountId = row.account_id,
            Username = row.username,
            PasswordHash = row.password_hash,
            CreatedAtTicks = Ticks(row.created_at)
        };
        if (row.email != null)
        {
            message.Email = row.email;
        }

        return message;
    }

    public static AccountRow ToRow(AccountMessage message) => new()
    {
        account_id = message.AccountId,
        username = message.Username,
        password_hash = message.PasswordHash,
        email = message.HasEmail ? message.Email : null,
        created_at = FromTicks(message.CreatedAtTicks)
    };

    public static CharacterMessage ToMessage(CharacterRow row) => new()
    {
        AccountId = row.account_id,
        Gold = row.gold
    };

    public static CharacterRow ToRow(CharacterMessage message) => new()
    {
        account_id = message.AccountId,
        gold = message.Gold
    };

    public static PlayerMessage ToMessage(PlayerRow row)
    {
        var message = new PlayerMessage
        {
            AccountId = row.account_id,
            PlayerName = row.player_name,
            LoginAtTicks = Ticks(row.login_at)
        };
        if (row.logout_at is DateTime logoutAt)
        {
            message.LogoutAtTicks = Ticks(logoutAt);
        }

        if (row.ip_address != null)
        {
            message.IpAddress = row.ip_address;
        }

        return message;
    }

    public static PlayerRow ToRow(PlayerMessage message) => new()
    {
        account_id = message.AccountId,
        player_name = message.PlayerName,
        login_at = FromTicks(message.LoginAtTicks),
        logout_at = OptionalTicks(message.HasLogoutAtTicks, message.LogoutAtTicks),
        ip_address = message.HasIpAddress ? message.IpAddress : null
    };

    public static PasswordResetTokenMessage ToMessage(PasswordResetTokenRow row)
    {
        var message = new PasswordResetTokenMessage
        {
            TokenId = row.token_id,
            AccountId = row.account_id,
            Token = row.token,
            ExpiresAtTicks = Ticks(row.expires_at)
        };
        if (row.used_at is DateTime usedAt)
        {
            message.UsedAtTicks = Ticks(usedAt);
        }

        return message;
    }

    public static PasswordResetTokenRow ToRow(PasswordResetTokenMessage message) => new()
    {
        token_id = message.TokenId,
        account_id = message.AccountId,
        token = message.Token,
        expires_at = FromTicks(message.ExpiresAtTicks),
        used_at = OptionalTicks(message.HasUsedAtTicks, message.UsedAtTicks)
    };

    public static ChatLogMessage ToMessage(ChatLogRow row)
    {
        var message = new ChatLogMessage
        {
            AccountId = row.account_id,
            Channel = row.channel,
            Message = row.message,
            CreatedAtTicks = Ticks(row.created_at)
        };
        if (row.room_id is ulong roomId)
        {
            message.RoomId = roomId;
        }

        return message;
    }

    public static ChatLogRow ToRow(ChatLogMessage message) => new()
    {
        account_id = message.AccountId,
        room_id = message.HasRoomId ? message.RoomId : null,
        channel = message.Channel,
        message = message.Message,
        created_at = FromTicks(message.CreatedAtTicks)
    };

    public static LoginLogMessage ToMessage(LoginLogRow row)
    {
        var message = new LoginLogMessage
        {
            AccountId = row.account_id,
            PlayerName = row.player_name,
            LoginAtTicks = Ticks(row.login_at)
        };
        if (row.ip_address != null)
        {
            message.IpAddress = row.ip_address;
        }

        if (row.logout_at is DateTime logoutAt)
        {
            message.LogoutAtTicks = Ticks(logoutAt);
        }

        return message;
    }

    public static LoginLogRow ToRow(LoginLogMessage message) => new()
    {
        account_id = message.AccountId,
        player_name = message.PlayerName,
        ip_address = message.HasIpAddress ? message.IpAddress : null,
        login_at = FromTicks(message.LoginAtTicks),
        logout_at = OptionalTicks(message.HasLogoutAtTicks, message.LogoutAtTicks)
    };

    public static RoomLogMessage ToMessage(RoomLogRow row) => new()
    {
        AccountId = row.account_id,
        RoomId = row.room_id,
        Action = row.action,
        CreatedAtTicks = Ticks(row.created_at)
    };

    public static RoomLogRow ToRow(RoomLogMessage message) => new()
    {
        account_id = message.AccountId,
        room_id = message.RoomId,
        action = message.Action,
        created_at = FromTicks(message.CreatedAtTicks)
    };

    public static StatLogMessage ToMessage(StatLogRow row) => new()
    {
        PlayerCount = row.player_count,
        CreatedAtTicks = Ticks(row.created_at)
    };

    public static StatLogRow ToRow(StatLogMessage message) => new()
    {
        player_count = message.PlayerCount,
        created_at = FromTicks(message.CreatedAtTicks)
    };
}
