using GameServer.Database.Rows;

namespace GameServer.Database.Gateway;

/// <summary>
/// 같은 프로세스의 <see cref="DatabaseSystem"/>에 위임하는 게이트웨이.
/// SQL은 DbSet에 그대로 두고, 틱에서 기다리면 안 되는 쓰기만 큐와 WAL로 넘긴다.
/// </summary>
public sealed class LocalDbGateway : IDbGateway, IAsyncDisposable
{
    private readonly DatabaseSystem _database;
    private readonly DurableSaveQueue _queue;

    private LocalDbGateway(DatabaseSystem database, DurableSaveQueue queue)
    {
        _database = database;
        _queue = queue;
    }

    public static LocalDbGateway Create(DatabaseSystem database, string walDirectory)
    {
        ArgumentNullException.ThrowIfNull(database);
        var queue = new DurableSaveQueue(walDirectory, (work, _) => DispatchAsync(database, work));
        return new LocalDbGateway(database, queue);
    }

    /// <summary>WAL을 복구하고 장애 감시를 시작한다. 콜백을 연결한 뒤에 호출한다.</summary>
    public void Start()
    {
        _queue.Recover();
        _queue.StartMonitor();
    }

    public Action<IReadOnlyList<ulong>>? OnSustainedOutage
    {
        get => _queue.OnSustainedOutage;
        set => _queue.OnSustainedOutage = value;
    }

    public Func<int>? OnlineCount
    {
        get => _queue.OnlineCount;
        set => _queue.OnlineCount = value;
    }

    public bool IsLoginBlocked => _queue.IsLoginBlocked;

    public Task<int> RegisterAccountAsync(AccountRow account)
        => _database.Game.Accounts.InsertAsync(account);

    public async Task<AuthenticateAndLoadResult?> AuthenticateAndLoadAsync(string username)
    {
        var account = await _database.Game.Accounts.SelectByUsernameAsync(username);
        if (account == null)
        {
            return null;
        }

        var character = await _database.Game.Characters.SelectAsync(account.account_id);
        return new AuthenticateAndLoadResult(account, character);
    }

    public async Task<CharacterRow> CreateDefaultCharacterAsync(ulong accountId)
    {
        var row = new CharacterRow { account_id = accountId };
        await _database.Game.Characters.UpsertAsync(row);
        return row;
    }

    public Task InsertPlayerSessionAsync(PlayerRow player)
        => _database.Game.Players.InsertAsync(player);

    public Task DeleteExpiredPasswordResetTokensAsync()
        => _database.Game.PasswordResetTokens.DeleteExpiredAsync();

    public Task<AccountRow?> FindAccountByUsernameAsync(string username)
        => _database.Game.Accounts.SelectByUsernameAsync(username);

    public Task InsertPasswordResetTokenAsync(PasswordResetTokenRow row)
        => _database.Game.PasswordResetTokens.InsertAsync(row);

    public Task<PasswordResetTokenRow?> FindPasswordResetTokenAsync(string token)
        => _database.Game.PasswordResetTokens.SelectByTokenAsync(token);

    public Task<int> ConsumePasswordResetTokenAsync(ulong tokenId)
        => _database.Game.PasswordResetTokens.MarkUsedConditionalAsync(tokenId);

    public Task UpdatePasswordHashAsync(ulong accountId, string passwordHash)
        => _database.Game.Accounts.UpdatePasswordHashAsync(accountId, passwordHash);

    public async Task<IReadOnlyList<ChatLogRow>> QueryChatLogsAsync(
        ulong? accountId, ulong? roomId, DateTime? startTime, DateTime? endTime, int limit)
    {
        var rows = await _database.GameLog.ChatLogs.QueryAsync(accountId, roomId, startTime, endTime, limit);
        return rows.ToArray();
    }

    public async Task<IReadOnlyList<LoginLogRow>> QueryLoginLogsAsync(
        ulong? accountId, DateTime? startTime, DateTime? endTime, int limit)
    {
        var rows = await _database.GameLog.LoginLogs.QueryAsync(accountId, startTime, endTime, limit);
        return rows.ToArray();
    }

    public async Task<IReadOnlyList<RoomLogRow>> QueryRoomLogsAsync(
        ulong? accountId, ulong? roomId, string? action, DateTime? startTime, DateTime? endTime, int limit)
    {
        var rows = await _database.GameLog.RoomLogs.QueryAsync(accountId, roomId, action, startTime, endTime, limit);
        return rows.ToArray();
    }

    public async Task<IReadOnlyList<StatLogRow>> QueryStatHistoryAsync(int limit)
    {
        var rows = await _database.GameLog.StatLogs.GetHistoryAsync(limit);
        return rows.ToArray();
    }

    public void UpsertCharacter(CharacterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _queue.EnqueueCharacter(row.account_id, row.gold);
    }

    public void UpdateLogout(ulong accountId, DateTime logoutAt)
        => _queue.EnqueueLogout(accountId, logoutAt);

    public void WriteLoginLog(LoginLogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _queue.EnqueueLogin(row.account_id, row.player_name, row.ip_address, row.login_at);
    }

    public void WriteRoomLog(RoomLogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _queue.EnqueueRoom(row.account_id, row.room_id, row.action, row.created_at);
    }

    public void WriteStatLog(StatLogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _queue.EnqueueStat(row.player_count, row.created_at);
    }

    public Task FlushAccountAsync(ulong accountId)
        => _queue.FlushAccountAsync(accountId);

    public ValueTask DisposeAsync() => _queue.DisposeAsync();

    private static async Task DispatchAsync(DatabaseSystem database, SaveWork work)
    {
        switch (work)
        {
            case SaveWork.Character character:
                await database.Game.Characters.UpsertAsync(new CharacterRow
                {
                    account_id = character.AccountId,
                    gold = character.Gold
                });
                break;
            case SaveWork.Logout logout:
                await database.Game.Players.UpdateLogoutAsync(logout.AccountId, logout.LogoutAt);
                await database.GameLog.LoginLogs.UpdateLogoutAsync(logout.AccountId, logout.LogoutAt);
                break;
            case SaveWork.Login login:
                await database.GameLog.LoginLogs.InsertAsync(new LoginLogRow
                {
                    account_id = login.AccountId,
                    player_name = login.PlayerName,
                    ip_address = login.IpAddress,
                    login_at = login.LoginAt
                });
                break;
            case SaveWork.Room room:
                await database.GameLog.RoomLogs.InsertAsync(new RoomLogRow
                {
                    account_id = room.AccountId,
                    room_id = room.RoomId,
                    action = room.Action,
                    created_at = room.CreatedAt
                });
                break;
            case SaveWork.Stat stat:
                await database.GameLog.StatLogs.InsertAsync(new StatLogRow
                {
                    player_count = stat.PlayerCount,
                    created_at = stat.CreatedAt
                });
                break;
            default:
                throw new InvalidOperationException("알 수 없는 저장 작업입니다.");
        }
    }
}
