using System.Collections.Concurrent;
using System.Threading.Channels;
using Common.Logging;
using GameServer.Database.Gateway;
using GameServer.Database.Grpc;
using GameServer.Database.Rows;
using Grpc.Core;
using Grpc.Net.Client;

namespace GameServer.Database;

/// <summary>
/// DBServer로 요청을 보내는 <see cref="IDbGateway"/>.
/// 틱에서 호출되는 저장은 채널에만 넣고, 계정 순서대로 전송한다.
/// 동기 호출은 로그인·가입 3초, 관리 API 5초를 넘기면 <see cref="TimeoutException"/>이다.
/// </summary>
public sealed class GrpcDbGateway : IDbGateway, IAsyncDisposable
{
    private readonly DbGatewayApi.DbGatewayApiClient _client;
    private readonly GrpcChannel _channel;
    private readonly ConcurrentDictionary<ulong, AccountSender> _accounts = new();
    private readonly Channel<IOutgoing> _logs = Channel.CreateUnbounded<IOutgoing>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private int _logsStarted;
    private readonly TimeSpan _loginTimeout;
    private readonly TimeSpan _adminTimeout;
    private int _loginBlocked;
    private Task? _sendLoop;
    private Task? _watchLoop;
    private Task? _onlineLoop;

    public GrpcDbGateway(string address, TimeSpan? loginTimeout = null, TimeSpan? adminTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        _loginTimeout = loginTimeout ?? DbGatewayTimeout.LoginAndRegister;
        _adminTimeout = adminTimeout ?? DbGatewayTimeout.AdminApi;
        _channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = new SocketsHttpHandler
            {
                EnableMultipleHttp2Connections = true,
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(10),
                PooledConnectionIdleTimeout = Timeout.InfiniteTimeSpan
            }
        });
        _client = new DbGatewayApi.DbGatewayApiClient(_channel);
    }

    public Action<IReadOnlyList<ulong>>? OnSustainedOutage { get; set; }
    public Action<ulong>? OnWalWriteFailed { get; set; }
    public Action<bool>? OnLoginBlockedChanged { get; set; }
    public Func<int>? OnlineCount { get; set; }
    public bool IsLoginBlocked => Volatile.Read(ref _loginBlocked) == 1;
    public IReadOnlyList<ulong> ParkedAccounts => Array.Empty<ulong>();

    /// <summary>신호 수신과 저장 전송을 시작하고 ID 시드를 읽는다.</summary>
    public async Task<DbIdSeeds> StartAsync(bool requireConnection, CancellationToken cancellationToken = default)
    {
        _watchLoop = Task.Run(() => WatchLoopAsync(_cts.Token));
        _onlineLoop = Task.Run(() => OnlineLoopAsync(_cts.Token));

        Exception? last = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                return await GetIdSeedsAsync(cancellationToken);
            }
            catch (Exception ex) when (attempt < 5 && !cancellationToken.IsCancellationRequested)
            {
                last = ex;
                GameLogger.Warn("DbGateway", $"DBServer 연결 재시도 {attempt}/5. {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (Exception ex)
            {
                last = ex;
            }
        }

        if (requireConnection)
        {
            throw new InvalidOperationException("DBServer에 연결하지 못했습니다.", last);
        }

        GameLogger.Warn("DbGateway", $"DBServer에 연결하지 못했습니다. ID는 1부터 시작합니다. {last?.Message}");
        return new DbIdSeeds(0, 0);
    }

    public Task<DbIdSeeds> GetIdSeedsAsync(CancellationToken cancellationToken = default)
        => Call(_loginTimeout, cancellationToken, async ct =>
        {
            var seeds = await _client.GetIdSeedsAsync(new Google.Protobuf.WellKnownTypes.Empty(), Options(_loginTimeout, ct));
            return new DbIdSeeds(seeds.MaxAccountId, seeds.MaxRoomId);
        });

    public Task<int> RegisterAccountAsync(AccountRow account, CancellationToken cancellationToken = default)
        => Call(_loginTimeout, cancellationToken, async ct =>
        {
            var count = await _client.RegisterAccountAsync(DbGatewayMapper.ToMessage(account), Options(_loginTimeout, ct));
            return count.Value;
        });

    public Task<AuthenticateAndLoadResult?> AuthenticateAndLoadAsync(string username, CancellationToken cancellationToken = default)
        => Call(_loginTimeout, cancellationToken, async ct =>
        {
            var response = await _client.AuthenticateAndLoadAsync(
                new UsernameRequest { Username = username },
                Options(_loginTimeout, ct));
            if (!response.Found)
            {
                return (AuthenticateAndLoadResult?)null;
            }

            var character = response.HasCharacter ? DbGatewayMapper.ToRow(response.Character) : null;
            return new AuthenticateAndLoadResult(DbGatewayMapper.ToRow(response.Account), character);
        });

    public Task<CharacterRow> CreateDefaultCharacterAsync(ulong accountId, CancellationToken cancellationToken = default)
        => Call(_loginTimeout, cancellationToken, async ct =>
        {
            var row = await _client.CreateDefaultCharacterAsync(
                new AccountIdRequest { AccountId = accountId },
                Options(_loginTimeout, ct));
            return DbGatewayMapper.ToRow(row);
        });

    public Task InsertPlayerSessionAsync(PlayerRow player, CancellationToken cancellationToken = default)
        => Call(_loginTimeout, cancellationToken, async ct =>
        {
            await _client.InsertPlayerSessionAsync(DbGatewayMapper.ToMessage(player), Options(_loginTimeout, ct));
            return 0;
        });

    public Task DeleteExpiredPasswordResetTokensAsync(CancellationToken cancellationToken = default)
        => Call(_loginTimeout, cancellationToken, async ct =>
        {
            await _client.DeleteExpiredPasswordResetTokensAsync(new Google.Protobuf.WellKnownTypes.Empty(), Options(_loginTimeout, ct));
            return 0;
        });

    public Task<AccountRow?> FindAccountByUsernameAsync(string username, CancellationToken cancellationToken = default)
        => Call(_loginTimeout, cancellationToken, async ct =>
        {
            var response = await _client.FindAccountByUsernameAsync(
                new UsernameRequest { Username = username },
                Options(_loginTimeout, ct));
            return response.Found ? DbGatewayMapper.ToRow(response.Account) : null;
        });

    public Task InsertPasswordResetTokenAsync(PasswordResetTokenRow row, CancellationToken cancellationToken = default)
        => Call(_loginTimeout, cancellationToken, async ct =>
        {
            await _client.InsertPasswordResetTokenAsync(DbGatewayMapper.ToMessage(row), Options(_loginTimeout, ct));
            return 0;
        });

    public Task<PasswordResetTokenRow?> FindPasswordResetTokenAsync(string token, CancellationToken cancellationToken = default)
        => Call(_loginTimeout, cancellationToken, async ct =>
        {
            var response = await _client.FindPasswordResetTokenAsync(
                new TokenRequest { Token = token },
                Options(_loginTimeout, ct));
            return response.Found ? DbGatewayMapper.ToRow(response.Token) : null;
        });

    public Task<int> ConsumePasswordResetTokenAsync(ulong tokenId, CancellationToken cancellationToken = default)
        => Call(_loginTimeout, cancellationToken, async ct =>
        {
            var count = await _client.ConsumePasswordResetTokenAsync(
                new TokenIdRequest { TokenId = tokenId },
                Options(_loginTimeout, ct));
            return count.Value;
        });

    public Task UpdatePasswordHashAsync(ulong accountId, string passwordHash, CancellationToken cancellationToken = default)
        => Call(_loginTimeout, cancellationToken, async ct =>
        {
            await _client.UpdatePasswordHashAsync(new UpdatePasswordRequest
            {
                AccountId = accountId,
                PasswordHash = passwordHash
            }, Options(_loginTimeout, ct));
            return 0;
        });

    public Task<IReadOnlyList<ChatLogRow>> QueryChatLogsAsync(
        ulong? accountId, ulong? roomId, DateTime? startTime, DateTime? endTime, int limit,
        CancellationToken cancellationToken = default)
        => Call(_adminTimeout, cancellationToken, async ct =>
        {
            var query = new ChatLogQuery { Limit = limit };
            if (accountId is ulong account) query.AccountId = account;
            if (roomId is ulong room) query.RoomId = room;
            if (startTime is DateTime start) query.StartTicks = DbGatewayMapper.Ticks(start);
            if (endTime is DateTime end) query.EndTicks = DbGatewayMapper.Ticks(end);
            var response = await _client.QueryChatLogsAsync(query, Options(_adminTimeout, ct));
            return (IReadOnlyList<ChatLogRow>)response.Rows.Select(DbGatewayMapper.ToRow).ToArray();
        });

    public Task<IReadOnlyList<LoginLogRow>> QueryLoginLogsAsync(
        ulong? accountId, DateTime? startTime, DateTime? endTime, int limit,
        CancellationToken cancellationToken = default)
        => Call(_adminTimeout, cancellationToken, async ct =>
        {
            var query = new LoginLogQuery { Limit = limit };
            if (accountId is ulong account) query.AccountId = account;
            if (startTime is DateTime start) query.StartTicks = DbGatewayMapper.Ticks(start);
            if (endTime is DateTime end) query.EndTicks = DbGatewayMapper.Ticks(end);
            var response = await _client.QueryLoginLogsAsync(query, Options(_adminTimeout, ct));
            return (IReadOnlyList<LoginLogRow>)response.Rows.Select(DbGatewayMapper.ToRow).ToArray();
        });

    public Task<IReadOnlyList<RoomLogRow>> QueryRoomLogsAsync(
        ulong? accountId, ulong? roomId, string? action, DateTime? startTime, DateTime? endTime, int limit,
        CancellationToken cancellationToken = default)
        => Call(_adminTimeout, cancellationToken, async ct =>
        {
            var query = new RoomLogQuery { Limit = limit };
            if (accountId is ulong account) query.AccountId = account;
            if (roomId is ulong room) query.RoomId = room;
            if (action != null) query.Action = action;
            if (startTime is DateTime start) query.StartTicks = DbGatewayMapper.Ticks(start);
            if (endTime is DateTime end) query.EndTicks = DbGatewayMapper.Ticks(end);
            var response = await _client.QueryRoomLogsAsync(query, Options(_adminTimeout, ct));
            return (IReadOnlyList<RoomLogRow>)response.Rows.Select(DbGatewayMapper.ToRow).ToArray();
        });

    public Task<IReadOnlyList<StatLogRow>> QueryStatHistoryAsync(int limit, CancellationToken cancellationToken = default)
        => Call(_adminTimeout, cancellationToken, async ct =>
        {
            var response = await _client.QueryStatHistoryAsync(new StatQuery { Limit = limit }, Options(_adminTimeout, ct));
            return (IReadOnlyList<StatLogRow>)response.Rows.Select(DbGatewayMapper.ToRow).ToArray();
        });

    public void UpsertCharacter(CharacterRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Account(row.account_id).Enqueue(new OutCharacter(row));
    }

    public void UpdateLogout(ulong accountId, DateTime logoutAt)
        => Account(accountId).Enqueue(new OutLogout(accountId, logoutAt));

    public void WriteLoginLog(LoginLogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Account(row.account_id).Enqueue(new OutLogin(row));
    }

    public void WriteRoomLog(RoomLogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Logs(new OutRoom(row));
    }

    public void WriteStatLog(StatLogRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        Logs(new OutStat(row));
    }

    public Task FlushAccountAsync(ulong accountId)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Account(accountId).Enqueue(new OutFlush(accountId, done));
        return done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _logs.Writer.TryComplete();
        foreach (var sender in _accounts.Values)
        {
            sender.Complete();
        }

        var loops = _accounts.Values.Select(sender => sender.Completion)
            .Append(_sendLoop)
            .Append(_watchLoop)
            .Append(_onlineLoop)
            .Where(task => task != null)
            .Cast<Task>()
            .ToArray();
        try
        {
            await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception)
        {
            // 종료 중 전송 실패는 이미 로그로 남는다.
        }

        _channel.Dispose();
        _cts.Dispose();
    }

    private AccountSender Account(ulong accountId)
        => _accounts.GetOrAdd(accountId, static (id, gateway) => new AccountSender(gateway), this);

    private void Logs(IOutgoing outgoing)
    {
        if (!_logs.Writer.TryWrite(outgoing))
        {
            throw new InvalidOperationException("DB 전송 채널이 닫혀 있습니다.");
        }

        if (Interlocked.CompareExchange(ref _logsStarted, 1, 0) == 0)
        {
            _sendLoop = Task.Run(() => SendLoopAsync(_logs, _cts.Token));
        }
    }

    private async Task SendLoopAsync(Channel<IOutgoing> channel, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await SendOneAsync(item, cancellationToken);
                }
                catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
                {
                    GameLogger.Error("DbGateway", "DBServer로 기록을 보내지 못했습니다.", ex);
                }
                finally
                {
                    if (item is OutFlush flush)
                    {
                        flush.Done.TrySetResult();
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task SendOneAsync(IOutgoing item, CancellationToken cancellationToken)
    {
        // flush는 서버가 MySQL 재시도와 WAL까지 끝낸 뒤 반환한다. 응답을 놓쳐도 한 번만 더 보낸다.
        var attempts = item is OutFlush ? 2 : 5;
        var timeout = item is OutFlush ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(3);
        var delay = TimeSpan.FromMilliseconds(200);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await DispatchAsync(item, timeout, cancellationToken);
                return;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && attempt + 1 < attempts)
            {
                GameLogger.Warn("DbGateway", $"DBServer 전송 재시도 {attempt + 1}/{attempts - 1}. {ex.Message}");
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 5_000));
            }
        }
    }

    private async Task DispatchAsync(IOutgoing item, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var options = Options(timeout, cancellationToken);
        switch (item)
        {
            case OutCharacter character:
                await _client.UpsertCharacterAsync(DbGatewayMapper.ToMessage(character.Row), options);
                break;
            case OutLogout logout:
                await _client.UpdateLogoutAsync(new LogoutRequest
                {
                    AccountId = logout.AccountId,
                    LogoutAtTicks = DbGatewayMapper.Ticks(logout.LogoutAt)
                }, options);
                break;
            case OutLogin login:
                await _client.WriteLoginLogAsync(DbGatewayMapper.ToMessage(login.Row), options);
                break;
            case OutRoom room:
                await _client.WriteRoomLogAsync(DbGatewayMapper.ToMessage(room.Row), options);
                break;
            case OutStat stat:
                await _client.WriteStatLogAsync(DbGatewayMapper.ToMessage(stat.Row), options);
                break;
            case OutFlush flush:
                await _client.FlushAccountAsync(new AccountIdRequest { AccountId = flush.AccountId }, options);
                break;
            default:
                throw new InvalidOperationException("알 수 없는 저장 작업입니다.");
        }
    }

    private async Task WatchLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var call = _client.WatchServer(new Google.Protobuf.WellKnownTypes.Empty(), cancellationToken: cancellationToken);
                await foreach (var signal in call.ResponseStream.ReadAllAsync(cancellationToken))
                {
                    switch (signal.EventCase)
                    {
                        case ServerSignal.EventOneofCase.LoginBlocked:
                            var blocked = signal.LoginBlocked.Blocked;
                            Volatile.Write(ref _loginBlocked, blocked ? 1 : 0);
                            try
                            {
                                OnLoginBlockedChanged?.Invoke(blocked);
                            }
                            catch (Exception ex)
                            {
                                GameLogger.Error("DbGateway", "로그인 차단 콜백 실행 실패", ex);
                            }

                            break;
                        case ServerSignal.EventOneofCase.SustainedOutage:
                            try
                            {
                                OnSustainedOutage?.Invoke(signal.SustainedOutage.AccountIds.Select(id => (ulong)id).ToArray());
                            }
                            catch (Exception ex)
                            {
                                GameLogger.Error("DbGateway", "지속 장애 콜백 실행 실패", ex);
                            }

                            break;
                        case ServerSignal.EventOneofCase.WalWriteFailedAccount:
                            try
                            {
                                OnWalWriteFailed?.Invoke(signal.WalWriteFailedAccount);
                            }
                            catch (Exception ex)
                            {
                                GameLogger.Error("DbGateway", "WAL 실패 콜백 실행 실패", ex);
                            }

                            break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                GameLogger.Warn("DbGateway", $"DBServer 신호 스트림이 끊겼습니다. 다시 연결합니다. {ex.Message}");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task OnlineLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var count = 0;
                try
                {
                    count = OnlineCount?.Invoke() ?? 0;
                }
                catch (Exception ex)
                {
                    GameLogger.Warn("DbGateway", $"온라인 인원 조회 실패. {ex.Message}");
                }

                await _client.ReportOnlineCountAsync(
                    new OnlineCountRequest { Count = count },
                    Options(TimeSpan.FromSeconds(3), cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                GameLogger.Warn("DbGateway", $"온라인 인원 보고 실패. {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<T> Call<T>(TimeSpan timeout, CancellationToken cancellationToken, Func<CancellationToken, Task<T>> call)
    {
        return await DbGatewayTimeout.Run(async ct =>
        {
            try
            {
                return await call(ct);
            }
            catch (RpcException ex) when (ex.StatusCode is StatusCode.DeadlineExceeded or StatusCode.Cancelled)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new TimeoutException($"DB 응답이 {timeout.TotalSeconds:0}초를 초과했습니다.");
            }
            catch (RpcException ex)
            {
                throw new InvalidOperationException(string.IsNullOrEmpty(ex.Status.Detail) ? "DB 처리에 실패했습니다." : ex.Status.Detail, ex);
            }
        }, timeout, cancellationToken);
    }

    private static CallOptions Options(TimeSpan timeout, CancellationToken cancellationToken)
        => new(deadline: DateTime.UtcNow.Add(timeout), cancellationToken: cancellationToken);

    private sealed class AccountSender
    {
        private readonly GrpcDbGateway _owner;
        private readonly Channel<IOutgoing> _channel = Channel.CreateUnbounded<IOutgoing>(
            new UnboundedChannelOptions { SingleReader = true });
        private int _started;

        public AccountSender(GrpcDbGateway owner) => _owner = owner;

        public Task? Completion { get; private set; }

        public void Enqueue(IOutgoing item)
        {
            if (!_channel.Writer.TryWrite(item))
            {
                throw new InvalidOperationException("DB 전송 채널이 닫혀 있습니다.");
            }

            if (Interlocked.CompareExchange(ref _started, 1, 0) == 0)
            {
                Completion = Task.Run(() => _owner.SendLoopAsync(_channel, _owner._cts.Token));
            }
        }

        public void Complete() => _channel.Writer.TryComplete();
    }

    private interface IOutgoing;

    private sealed record OutCharacter(CharacterRow Row) : IOutgoing;
    private sealed record OutLogout(ulong AccountId, DateTime LogoutAt) : IOutgoing;
    private sealed record OutLogin(LoginLogRow Row) : IOutgoing;
    private sealed record OutRoom(RoomLogRow Row) : IOutgoing;
    private sealed record OutStat(StatLogRow Row) : IOutgoing;
    private sealed record OutFlush(ulong AccountId, TaskCompletionSource Done) : IOutgoing;
}
