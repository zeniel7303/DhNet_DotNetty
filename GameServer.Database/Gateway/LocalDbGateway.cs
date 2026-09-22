using GameServer.Database.Rows;

namespace GameServer.Database.Gateway;

/// <summary>
/// DBServer 프로세스 안에서 <see cref="DatabaseSystem"/>에 위임하는 게이트웨이.
/// SQL은 DbSet에 그대로 두고, 틱에서 기다리면 안 되는 쓰기만 큐와 WAL로 넘긴다.
/// GameServer는 이 클래스를 쓰지 않고 gRPC 클라이언트로 여기를 호출한다.
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

    public Action<ulong>? OnWalWriteFailed
    {
        get => _queue.OnWalWriteFailed;
        set => _queue.OnWalWriteFailed = value;
    }

    public Func<int>? OnlineCount
    {
        get => _queue.OnlineCount;
        set => _queue.OnlineCount = value;
    }

    public Action<bool>? OnLoginBlockedChanged
    {
        get => _queue.OnLoginBlockedChanged;
        set => _queue.OnLoginBlockedChanged = value;
    }

    public IReadOnlyList<ulong> ParkedAccounts => _queue.ParkedAccounts;

    public bool IsLoginBlocked => _queue.IsLoginBlocked;

    public Task<int> RegisterAccountAsync(AccountRow account, CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(
            ct => _database.Game.Accounts.InsertAsync(account, ct),
            DbGatewayTimeout.LoginAndRegister,
            cancellationToken);

    public Task<AuthenticateAndLoadResult?> AuthenticateAndLoadAsync(string username, CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(async ct =>
        {
            var account = await _database.Game.Accounts.SelectByUsernameAsync(username, ct);
            if (account == null)
            {
                return (AuthenticateAndLoadResult?)null;
            }

            var character = await _database.Game.Characters.SelectAsync(account.account_id, ct);
            return new AuthenticateAndLoadResult(account, character);
        }, DbGatewayTimeout.LoginAndRegister, cancellationToken);

    public Task<CharacterRow> CreateDefaultCharacterAsync(ulong accountId, CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(async ct =>
        {
            var row = new CharacterRow { account_id = accountId };
            await _database.Game.Characters.UpsertAsync(row, ct);
            return row;
        }, DbGatewayTimeout.LoginAndRegister, cancellationToken);

    public Task InsertPlayerSessionAsync(PlayerRow player, CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(
            ct => _database.Game.Players.InsertAsync(player, ct),
            DbGatewayTimeout.LoginAndRegister,
            cancellationToken);

    public Task DeleteExpiredPasswordResetTokensAsync(CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(
            ct => _database.Game.PasswordResetTokens.DeleteExpiredAsync(ct),
            DbGatewayTimeout.LoginAndRegister,
            cancellationToken);

    public Task<AccountRow?> FindAccountByUsernameAsync(string username, CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(
            ct => _database.Game.Accounts.SelectByUsernameAsync(username, ct),
            DbGatewayTimeout.LoginAndRegister,
            cancellationToken);

    public Task InsertPasswordResetTokenAsync(PasswordResetTokenRow row, CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(
            ct => _database.Game.PasswordResetTokens.InsertAsync(row, ct),
            DbGatewayTimeout.LoginAndRegister,
            cancellationToken);

    public Task<PasswordResetTokenRow?> FindPasswordResetTokenAsync(string token, CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(
            ct => _database.Game.PasswordResetTokens.SelectByTokenAsync(token, ct),
            DbGatewayTimeout.LoginAndRegister,
            cancellationToken);

    public Task<int> ConsumePasswordResetTokenAsync(ulong tokenId, CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(
            ct => _database.Game.PasswordResetTokens.MarkUsedConditionalAsync(tokenId, ct),
            DbGatewayTimeout.LoginAndRegister,
            cancellationToken);

    public Task UpdatePasswordHashAsync(ulong accountId, string passwordHash, CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(
            ct => _database.Game.Accounts.UpdatePasswordHashAsync(accountId, passwordHash, ct),
            DbGatewayTimeout.LoginAndRegister,
            cancellationToken);

    public Task<IReadOnlyList<ChatLogRow>> QueryChatLogsAsync(
        ulong? accountId, ulong? roomId, DateTime? startTime, DateTime? endTime, int limit,
        CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(async ct =>
        {
            var rows = await _database.GameLog.ChatLogs.QueryAsync(accountId, roomId, startTime, endTime, limit, ct);
            return (IReadOnlyList<ChatLogRow>)rows.ToArray();
        }, DbGatewayTimeout.AdminApi, cancellationToken);

    public Task<IReadOnlyList<LoginLogRow>> QueryLoginLogsAsync(
        ulong? accountId, DateTime? startTime, DateTime? endTime, int limit,
        CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(async ct =>
        {
            var rows = await _database.GameLog.LoginLogs.QueryAsync(accountId, startTime, endTime, limit, ct);
            return (IReadOnlyList<LoginLogRow>)rows.ToArray();
        }, DbGatewayTimeout.AdminApi, cancellationToken);

    public Task<IReadOnlyList<RoomLogRow>> QueryRoomLogsAsync(
        ulong? accountId, ulong? roomId, string? action, DateTime? startTime, DateTime? endTime, int limit,
        CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(async ct =>
        {
            var rows = await _database.GameLog.RoomLogs.QueryAsync(accountId, roomId, action, startTime, endTime, limit, ct);
            return (IReadOnlyList<RoomLogRow>)rows.ToArray();
        }, DbGatewayTimeout.AdminApi, cancellationToken);

    public Task<IReadOnlyList<StatLogRow>> QueryStatHistoryAsync(int limit, CancellationToken cancellationToken = default)
        => DbGatewayTimeout.Run(async ct =>
        {
            var rows = await _database.GameLog.StatLogs.GetHistoryAsync(limit, ct);
            return (IReadOnlyList<StatLogRow>)rows.ToArray();
        }, DbGatewayTimeout.AdminApi, cancellationToken);

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
