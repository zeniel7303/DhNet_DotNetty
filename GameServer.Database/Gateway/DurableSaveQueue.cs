using System.Text.Json;
using System.Threading.Channels;
using Common.Logging;

namespace GameServer.Database.Gateway;

/// <summary>
/// 캐릭터 단위로 저장을 직렬화하고, 실패 시 지수 백오프로 최대 3회 재시도한 뒤 WAL에 남긴다.
/// 계정마다 <see cref="Channel"/> 소비자가 하나라서 같은 계정의 저장과 로그아웃은 겹치지 않는다.
/// 연속 실패 60초, 버퍼 한도, dirty 세션 한도 중 하나라도 넘으면 신규 로그인을 막는다.
/// WAL 기록 자체가 실패하면 그 임계치까지 세션을 남기지 않고 해당 계정을 즉시 알린다.
/// </summary>
internal sealed class DurableSaveQueue : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly DurableWal _wal;
    private readonly Func<SaveWork, CancellationToken, Task> _sink;
    private readonly DurableSaveOptions _options;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _sync = new();
    private readonly Dictionary<ulong, AccountLane> _lanes = new();
    private readonly LogLane _logs;
    private readonly Dictionary<string, int> _bytesByKey = new();
    private readonly Dictionary<string, byte[]> _payloads = new();
    private readonly HashSet<ulong> _parked = new();

    private long _seq;
    private int _count;
    private long _bytes;
    private DateTimeOffset? _failureSince;
    private int _blocked;
    private Task _hold = Task.CompletedTask;

    public DurableSaveQueue(
        string walDirectory,
        Func<SaveWork, CancellationToken, Task> sink,
        DurableSaveOptions? options = null,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _wal = new DurableWal(walDirectory);
        _sink = sink;
        _options = options ?? DurableSaveOptions.Default;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? ((time, token) => Task.Delay(time, token));
        _logs = new LogLane(this);
    }

    public Action<IReadOnlyList<ulong>>? OnSustainedOutage { get; set; }

    /// <summary>WAL에 쓰지 못하면 해당 계정을 즉시 알린다. 테스트에서는 기록을 실패시키는 예외.</summary>
    public Action<ulong>? OnWalWriteFailed { get; set; }

    /// <summary>로그인 차단 플래그가 켜지거나 꺼질 때 호출된다.</summary>
    public Action<bool>? OnLoginBlockedChanged { get; set; }

    internal Exception? WalFault { get; set; }
    public Func<int>? OnlineCount { get; set; }

    public bool IsLoginBlocked => Volatile.Read(ref _blocked) == 1;

    public int PendingCount
    {
        get { lock (_sync) return _count; }
    }

    public int ParkedAccountCount
    {
        get { lock (_sync) return _parked.Count; }
    }

    public IReadOnlyList<ulong> ParkedAccounts
    {
        get { lock (_sync) return _parked.ToArray(); }
    }

    /// <summary>테스트용. 게이트가 끝나기 전에는 배치를 DB로 넘기지 않는다.</summary>
    public void Hold(Task gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        Volatile.Write(ref _hold, gate);
    }

    public void EnqueueCharacter(ulong accountId, int gold)
    {
        var work = new SaveWork.Character(accountId, gold);
        var payload = Serialize(work);
        Start(accountId, lane =>
        {
            RememberLocked(CharacterKey(accountId), payload);
            lane.Gold = gold;
            lane.GoldPayload = payload;
        });
    }

    public void EnqueueLogout(ulong accountId, DateTime logoutAt)
    {
        var work = new SaveWork.Logout(accountId, logoutAt);
        var payload = Serialize(work);
        Start(accountId, lane =>
        {
            RememberLocked(LogoutKey(accountId), payload);
            lane.LogoutAt = logoutAt;
            lane.LogoutPayload = payload;
        });
    }

    public void EnqueueLogin(ulong accountId, string playerName, string? ipAddress, DateTime loginAt)
    {
        var work = new SaveWork.Login(accountId, playerName, ipAddress, loginAt);
        var payload = Serialize(work);
        var key = NextKey("n");
        Start(accountId, lane =>
        {
            RememberLocked(key, payload);
            lane.Logins.Enqueue(new Pending(key, payload, work));
        });
    }

    public void EnqueueRoom(ulong accountId, ulong roomId, string action, DateTime createdAt)
    {
        var work = new SaveWork.Room(accountId, roomId, action, createdAt);
        EnqueueLog(work);
    }

    public void EnqueueStat(int playerCount, DateTime createdAt)
    {
        EnqueueLog(new SaveWork.Stat(playerCount, createdAt));
    }

    public Task FlushAccountAsync(ulong accountId)
    {
        AccountLane? lane;
        Task wait;
        lock (_sync)
        {
            if (!_lanes.TryGetValue(accountId, out lane))
            {
                return Task.CompletedTask;
            }

            wait = lane.ArmFlush();
            lane.MarkWake();
        }

        lane.Wake();
        return wait;
    }

    public void Recover()
    {
        var startLanes = new List<AccountLane>();
        var startLogs = false;
        foreach (var (key, payload) in _wal.ReadAll())
        {
            SaveWork? work;
            try
            {
                work = Deserialize(payload);
            }
            catch (Exception ex)
            {
                GameLogger.Warn("DbGateway", $"WAL 항목을 읽지 못했습니다. key={key}. {ex.Message}");
                continue;
            }

            if (work == null)
            {
                continue;
            }

            lock (_sync)
            {
                RememberLocked(key, payload);
                NoteSeq(key);
                switch (work)
                {
                    case SaveWork.Character character:
                        var characterLane = GetLane(character.AccountId);
                        characterLane.Gold = character.Gold;
                        characterLane.GoldPayload = payload;
                        break;
                    case SaveWork.Logout logout:
                        var logoutLane = GetLane(logout.AccountId);
                        logoutLane.LogoutAt = logout.LogoutAt;
                        logoutLane.LogoutPayload = payload;
                        break;
                    case SaveWork.Login login:
                        GetLane(login.AccountId).Logins.Enqueue(new Pending(key, payload, login));
                        break;
                    default:
                        _logs.Pending.Enqueue(new Pending(key, payload, work));
                        break;
                }
            }
        }

        lock (_sync)
        {
            foreach (var lane in _lanes.Values)
            {
                if (lane.HasPending())
                {
                    lane.MarkWake();
                    startLanes.Add(lane);
                }
            }

            startLogs = _logs.TryMarkRunning();
        }

        foreach (var lane in startLanes)
        {
            lane.Wake();
        }

        if (startLogs)
        {
            _ = Task.Run(() => _logs.RunAsync());
        }
    }

    public void RetryParked()
    {
        var lanes = new List<AccountLane>();
        var startLogs = false;
        lock (_sync)
        {
            foreach (var lane in _lanes.Values)
            {
                if (lane.HasPending())
                {
                    lane.MarkWake();
                    lanes.Add(lane);
                }
            }

            startLogs = _logs.TryMarkRunning();
        }

        foreach (var lane in lanes)
        {
            lane.Wake();
        }

        if (startLogs)
        {
            _ = Task.Run(() => _logs.RunAsync());
        }
    }

    public void StartMonitor(TimeSpan? interval = null)
    {
        _ = MonitorAsync(interval ?? TimeSpan.FromSeconds(1));
    }

    public void Evaluate()
    {
        var online = 0;
        try
        {
            online = OnlineCount?.Invoke() ?? 0;
        }
        catch (Exception ex)
        {
            GameLogger.Warn("DbGateway", $"온라인 인원 조회 실패. dirty 비율은 이번 판정에서 제외합니다. {ex.Message}");
        }

        List<ulong>? parked = null;
        var fire = false;
        bool? blockedEdge = null;
        lock (_sync)
        {
            var failureFor = _failureSince is { } since ? _utcNow() - since : TimeSpan.Zero;
            var bufferHot = _count > 0
                && (_count >= _options.MaxRecords || _bytes >= _options.BufferPressureBytes());
            var dirty = _parked.Count;
            // 비율은 천분율로 비교해 0.2가 이진 소수로 흔들리지 않게 한다.
            var ratioPerMille = (int)Math.Round(_options.DirtySessionRatio * 1000, MidpointRounding.AwayFromZero);
            var dirtyHot = dirty >= _options.DirtySessionCount
                || (online > 0 && ratioPerMille > 0 && dirty * 1000 >= online * ratioPerMille);
            var trip = failureFor >= _options.FailureWindow || bufferHot || dirtyHot;

            if (trip && _blocked == 0)
            {
                _blocked = 1;
                fire = true;
                blockedEdge = true;
                parked = _parked.ToList();
                GameLogger.Warn(
                    "DbGateway",
                    $"DB 지속 장애로 신규 로그인을 차단합니다. failure={(int)failureFor.TotalSeconds}s buffer={_count}/{_bytes}B dirty={dirty} online={online}");
            }
            else if (!trip && _blocked == 1)
            {
                _blocked = 0;
                blockedEdge = false;
                GameLogger.Info("DbGateway", "DB 장애가 해소되어 신규 로그인을 다시 허용합니다.");
            }
        }

        if (blockedEdge is bool blocked)
        {
            try
            {
                OnLoginBlockedChanged?.Invoke(blocked);
            }
            catch (Exception ex)
            {
                GameLogger.Error("DbGateway", "로그인 차단 콜백 실행 실패", ex);
            }
        }

        if (!fire || parked == null)
        {
            return;
        }

        try
        {
            OnSustainedOutage?.Invoke(parked);
        }
        catch (Exception ex)
        {
            GameLogger.Error("DbGateway", "지속 장애 콜백 실행 실패", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await Task.CompletedTask;
    }

    private async Task MonitorAsync(TimeSpan interval)
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _delay(interval, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                RetryParked();
                Evaluate();
            }
            catch (Exception ex)
            {
                GameLogger.Error("DbGateway", "장애 감시 루프 오류", ex);
            }
        }
    }

    private void EnqueueLog(SaveWork work)
    {
        var payload = Serialize(work);
        var key = NextKey(work is SaveWork.Stat ? "s" : "r");
        var start = false;
        lock (_sync)
        {
            RememberLocked(key, payload);
            _logs.Pending.Enqueue(new Pending(key, payload, work));
            start = _logs.TryMarkRunning();
        }

        if (start)
        {
            _ = Task.Run(() => _logs.RunAsync());
        }

        Evaluate();
    }

    private void Start(ulong accountId, Action<AccountLane> mutate)
    {
        AccountLane lane;
        lock (_sync)
        {
            lane = GetLane(accountId);
            mutate(lane);
            lane.MarkWake();
        }

        lane.Wake();
        Evaluate();
    }

    private AccountLane GetLane(ulong accountId)
    {
        if (!_lanes.TryGetValue(accountId, out var lane))
        {
            lane = new AccountLane(this, accountId);
            _lanes.Add(accountId, lane);
        }

        return lane;
    }

    private void RememberLocked(string key, byte[] payload)
    {
        if (_bytesByKey.TryGetValue(key, out var old))
        {
            _bytes += payload.Length - old;
        }
        else
        {
            _bytes += payload.Length;
            _count++;
        }

        _bytesByKey[key] = payload.Length;
        _payloads[key] = payload;
    }

    private void Finish(string key, byte[] committed)
    {
        byte[]? rewrite = null;
        var delete = false;
        lock (_sync)
        {
            if (_payloads.TryGetValue(key, out var latest) && !latest.AsSpan().SequenceEqual(committed))
            {
                rewrite = latest;
            }
            else
            {
                if (_bytesByKey.Remove(key, out var size))
                {
                    _bytes -= size;
                    _count--;
                }

                _payloads.Remove(key);
                delete = true;
            }
        }

        if (delete)
        {
            TryDelete(key);
        }
        else if (rewrite != null)
        {
            TryWrite(key, rewrite, work: null);
        }
    }

    private async Task<bool> CommitOne(Pending pending)
    {
        if (!TryWrite(pending.Key, pending.Payload, pending.Work))
        {
            return false;
        }

        var wait = _options.InitialBackoff;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _sink(pending.Work, _cts.Token);
                Finish(pending.Key, pending.Payload);
                NoteSuccess();
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                NoteFailure();
                if (attempt >= _options.MaxRetries)
                {
                    GameLogger.Error("DbGateway", $"저장 실패. 재시도 {_options.MaxRetries}회를 모두 사용했습니다. key={pending.Key}", ex);
                    return false;
                }

                GameLogger.Warn("DbGateway", $"저장 재시도 {attempt + 1}/{_options.MaxRetries} key={pending.Key}");
                try
                {
                    await _delay(wait, _cts.Token);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }

                var nextMs = Math.Min(wait.TotalMilliseconds * 2, 30_000);
                wait = TimeSpan.FromMilliseconds(nextMs);
            }
        }
    }

    private bool TryWrite(string key, byte[] payload, SaveWork? work)
    {
        try
        {
            if (WalFault != null)
            {
                throw WalFault;
            }

            _wal.Write(key, payload);
            return true;
        }
        catch (Exception ex)
        {
            NoteFailure();
            GameLogger.Error("DbGateway", $"WAL 기록 실패 key={key}", ex);
            NotifyWalWriteFailed(work, key);
            return false;
        }
    }

    private void NotifyWalWriteFailed(SaveWork? work, string key)
    {
        var accountId = work?.SessionAccountId ?? AccountFromDurableKey(key);
        if (accountId is not ulong id)
        {
            return;
        }

        try
        {
            OnWalWriteFailed?.Invoke(id);
        }
        catch (Exception ex)
        {
            GameLogger.Error("DbGateway", $"WAL 실패 세션 종료 콜백 오류 AccountId={id}", ex);
        }
    }

    private static ulong? AccountFromDurableKey(string key)
    {
        if (!(key.StartsWith("c-", StringComparison.Ordinal) || key.StartsWith("o-", StringComparison.Ordinal)))
        {
            return null;
        }

        return ulong.TryParse(key[2..], out var accountId) ? accountId : null;
    }

    private void TryDelete(string key)
    {
        try
        {
            _wal.Delete(key);
        }
        catch (Exception ex)
        {
            GameLogger.Warn("DbGateway", $"WAL 삭제 실패 key={key}. {ex.Message}");
        }
    }

    private void NoteSuccess()
    {
        lock (_sync)
        {
            _failureSince = null;
        }
    }

    private void NoteFailure()
    {
        lock (_sync)
        {
            _failureSince ??= _utcNow();
        }
    }

    private Task WaitHold() => Volatile.Read(ref _hold) ?? Task.CompletedTask;

    private string NextKey(string prefix)
    {
        var seq = Interlocked.Increment(ref _seq);
        return $"{prefix}-{seq:D16}";
    }

    private void NoteSeq(string key)
    {
        var dash = key.IndexOf('-');
        if (dash <= 0)
        {
            return;
        }

        var head = key[..dash];
        if (head is not ("n" or "r" or "s"))
        {
            return;
        }

        if (!long.TryParse(key[(dash + 1)..], out var seq))
        {
            return;
        }

        while (true)
        {
            var current = Interlocked.Read(ref _seq);
            if (seq <= current)
            {
                return;
            }

            if (Interlocked.CompareExchange(ref _seq, seq, current) == current)
            {
                return;
            }
        }
    }

    private static string CharacterKey(ulong accountId) => $"c-{accountId}";
    private static string LogoutKey(ulong accountId) => $"o-{accountId}";

    private static byte[] Serialize(SaveWork work)
    {
        var dto = work switch
        {
            SaveWork.Character character => new WalDto
            {
                Kind = "character",
                AccountId = character.AccountId,
                Gold = character.Gold
            },
            SaveWork.Logout logout => new WalDto
            {
                Kind = "logout",
                AccountId = logout.AccountId,
                AtTicks = logout.LogoutAt.Ticks
            },
            SaveWork.Login login => new WalDto
            {
                Kind = "login",
                AccountId = login.AccountId,
                PlayerName = login.PlayerName,
                IpAddress = login.IpAddress,
                AtTicks = login.LoginAt.Ticks
            },
            SaveWork.Room room => new WalDto
            {
                Kind = "room",
                AccountId = room.AccountId,
                RoomId = room.RoomId,
                Action = room.Action,
                AtTicks = room.CreatedAt.Ticks
            },
            SaveWork.Stat stat => new WalDto
            {
                Kind = "stat",
                PlayerCount = stat.PlayerCount,
                AtTicks = stat.CreatedAt.Ticks
            },
            _ => throw new InvalidOperationException("알 수 없는 저장 작업입니다.")
        };

        return JsonSerializer.SerializeToUtf8Bytes(dto, JsonOptions);
    }

    private static SaveWork? Deserialize(byte[] payload)
    {
        var dto = JsonSerializer.Deserialize<WalDto>(payload, JsonOptions);
        if (dto == null || string.IsNullOrEmpty(dto.Kind))
        {
            return null;
        }

        var at = new DateTime(dto.AtTicks, DateTimeKind.Utc);
        return dto.Kind switch
        {
            "character" => new SaveWork.Character(dto.AccountId, dto.Gold),
            "logout" => new SaveWork.Logout(dto.AccountId, at),
            "login" => new SaveWork.Login(dto.AccountId, dto.PlayerName ?? "", dto.IpAddress, at),
            "room" => new SaveWork.Room(dto.AccountId, dto.RoomId, dto.Action ?? "", at),
            "stat" => new SaveWork.Stat(dto.PlayerCount, at),
            _ => null
        };
    }

    private readonly record struct Pending(string Key, byte[] Payload, SaveWork Work);

    private sealed class AccountLane
    {
        private readonly DurableSaveQueue _owner;
        private readonly List<TaskCompletionSource> _waiters = new();

        private readonly Channel<byte> _mailbox = Channel.CreateUnbounded<byte>(
            new UnboundedChannelOptions { SingleReader = true });
        private int _consumerStarted;
        private bool _wokeAfterPark;

        public AccountLane(DurableSaveQueue owner, ulong accountId)
        {
            _owner = owner;
            AccountId = accountId;
        }

        public ulong AccountId { get; }
        public bool Running { get; private set; }
        public int? Gold { get; set; }
        public byte[]? GoldPayload { get; set; }
        public DateTime? LogoutAt { get; set; }
        public byte[]? LogoutPayload { get; set; }
        public Queue<Pending> Logins { get; } = new();

        public bool HasPending() => GoldPayload != null || LogoutPayload != null || Logins.Count > 0;

        public bool HasSessionPending() => GoldPayload != null || LogoutPayload != null;

        /// <summary>락을 잡은 쪽에서 호출한다. 소비자가 멈춰 있을 때만 실패 후 재시작 대상으로 표시한다.</summary>
        public void MarkWake()
        {
            if (!Running)
            {
                _wokeAfterPark = true;
            }
        }

        /// <summary>계정 채널에 처리 신호를 넣는다. 소비자는 하나다.</summary>
        public void Wake()
        {
            _mailbox.Writer.TryWrite(0);
            if (Interlocked.CompareExchange(ref _consumerStarted, 1, 0) == 0)
            {
                _ = Task.Run(ConsumeAsync);
            }
        }

        private async Task ConsumeAsync()
        {
            try
            {
                await foreach (var kick in _mailbox.Reader.ReadAllAsync(_owner._cts.Token))
                {
                    _ = kick;
                    while (_mailbox.Reader.TryRead(out var extra))
                    {
                        _ = extra;
                    }

                    var run = false;
                    lock (_owner._sync)
                    {
                        if (!Running && HasPending())
                        {
                            Running = true;
                            _wokeAfterPark = false;
                            run = true;
                        }
                    }

                    if (!run)
                    {
                        continue;
                    }

                    var parked = await RunAsync();
                    if (!parked)
                    {
                        continue;
                    }

                    // 처리 중에 쌓인 신호는 실패 배치를 즉시 다시 돌리지 않도록 버린다.
                    // 소비자가 멈춘 뒤에 들어온 저장은 다시 신호를 넣어 놓치지 않는다.
                    while (_mailbox.Reader.TryRead(out var extra))
                    {
                        _ = extra;
                    }

                    var again = false;
                    lock (_owner._sync)
                    {
                        if (_wokeAfterPark && !Running && HasPending())
                        {
                            again = true;
                        }

                        _wokeAfterPark = false;
                    }

                    if (again)
                    {
                        _mailbox.Writer.TryWrite(0);
                    }
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        public Task ArmFlush()
        {
            if (!Running && !HasPending())
            {
                return Task.CompletedTask;
            }

            var waiter = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add(waiter);
            return waiter.Task;
        }

        public async Task<bool> RunAsync()
        {
            List<TaskCompletionSource>? waiters = null;
            var evaluate = false;
            var parked = false;
            try
            {
                while (!_owner._cts.IsCancellationRequested)
                {
                    await _owner.WaitHold();
                    if (_owner._cts.IsCancellationRequested)
                    {
                        lock (_owner._sync)
                        {
                            Running = false;
                            waiters = DetachWaiters();
                        }

                        break;
                    }

                    Batch batch;
                    lock (_owner._sync)
                    {
                        batch = Take();
                        if (batch.IsEmpty)
                        {
                            if (!HasSessionPending())
                            {
                                _owner._parked.Remove(AccountId);
                            }

                            Running = false;
                            waiters = DetachWaiters();
                            evaluate = true;
                            break;
                        }
                    }

                    var failed = await Commit(batch);
                    if (failed != null)
                    {
                        lock (_owner._sync)
                        {
                            Restore(failed);
                            Running = false;
                            if (failed.DirtiesSession)
                            {
                                _owner._parked.Add(AccountId);
                            }

                            waiters = DetachWaiters();
                        }

                        evaluate = true;
                        parked = true;
                        break;
                    }

                    lock (_owner._sync)
                    {
                        if (!HasSessionPending())
                        {
                            _owner._parked.Remove(AccountId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                GameLogger.Error("DbGateway", $"계정 저장 루프 오류 AccountId={AccountId}", ex);
                lock (_owner._sync)
                {
                    Running = false;
                    waiters = DetachWaiters();
                }

                parked = true;
            }

            if (evaluate)
            {
                _owner.Evaluate();
            }

            Signal(waiters);
            return parked;
        }

        private Batch Take()
        {
            var batch = new Batch();
            while (Logins.Count > 0)
            {
                batch.Logins.Add(Logins.Dequeue());
            }

            if (GoldPayload != null && Gold.HasValue)
            {
                batch.Character = new Pending(CharacterKey(AccountId), GoldPayload, new SaveWork.Character(AccountId, Gold.Value));
                Gold = null;
                GoldPayload = null;
            }

            if (LogoutPayload != null && LogoutAt.HasValue)
            {
                batch.Logout = new Pending(LogoutKey(AccountId), LogoutPayload, new SaveWork.Logout(AccountId, LogoutAt.Value));
                LogoutAt = null;
                LogoutPayload = null;
            }

            return batch;
        }

        private void Restore(Batch failed)
        {
            if (failed.Logins.Count > 0)
            {
                var newer = Logins.ToArray();
                Logins.Clear();
                foreach (var login in failed.Logins)
                {
                    Logins.Enqueue(login);
                }

                foreach (var login in newer)
                {
                    Logins.Enqueue(login);
                }
            }

            if (failed.Character is { } character && GoldPayload == null)
            {
                GoldPayload = character.Payload;
                if (character.Work is SaveWork.Character row)
                {
                    Gold = row.Gold;
                }
            }

            if (failed.Logout is { } logout && LogoutPayload == null)
            {
                LogoutPayload = logout.Payload;
                if (logout.Work is SaveWork.Logout row)
                {
                    LogoutAt = row.LogoutAt;
                }
            }
        }

        private async Task<Batch?> Commit(Batch batch)
        {
            for (var i = 0; i < batch.Logins.Count; i++)
            {
                if (await _owner.CommitOne(batch.Logins[i]))
                {
                    continue;
                }

                batch.Logins.RemoveRange(0, i);
                return batch;
            }

            batch.Logins.Clear();

            if (batch.Character is { } character && !await _owner.CommitOne(character))
            {
                return batch;
            }

            batch.Character = null;

            if (batch.Logout is { } logout && !await _owner.CommitOne(logout))
            {
                return batch;
            }

            return null;
        }

        private List<TaskCompletionSource> DetachWaiters()
        {
            var copy = _waiters.ToList();
            _waiters.Clear();
            return copy;
        }
    }

    private sealed class Batch
    {
        public List<Pending> Logins { get; } = new();
        public Pending? Character { get; set; }
        public Pending? Logout { get; set; }
        public bool IsEmpty => Logins.Count == 0 && Character == null && Logout == null;
        public bool DirtiesSession => Character != null || Logout != null;
    }

    private sealed class LogLane
    {
        private readonly DurableSaveQueue _owner;

        public LogLane(DurableSaveQueue owner) => _owner = owner;

        public Queue<Pending> Pending { get; } = new();
        public Pending? Retry { get; set; }
        public bool Running { get; private set; }

        public bool TryMarkRunning()
        {
            if (Running || (Retry == null && Pending.Count == 0))
            {
                return false;
            }

            Running = true;
            return true;
        }

        public async Task RunAsync()
        {
            var idle = false;
            try
            {
                while (!_owner._cts.IsCancellationRequested)
                {
                    await _owner.WaitHold();
                    if (_owner._cts.IsCancellationRequested)
                    {
                        break;
                    }

                    Pending item;
                    lock (_owner._sync)
                    {
                        if (Retry != null)
                        {
                            item = Retry.Value;
                            Retry = null;
                        }
                        else if (Pending.Count > 0)
                        {
                            item = Pending.Dequeue();
                        }
                        else
                        {
                            Running = false;
                            idle = true;
                            break;
                        }
                    }

                    if (await _owner.CommitOne(item))
                    {
                        continue;
                    }

                    lock (_owner._sync)
                    {
                        Retry = item;
                        Running = false;
                    }

                    _owner.Evaluate();
                    return;
                }
            }
            catch (Exception ex)
            {
                GameLogger.Error("DbGateway", "로그 저장 루프 오류", ex);
                lock (_owner._sync)
                {
                    Running = false;
                }

                return;
            }

            if (idle)
            {
                _owner.Evaluate();
            }
        }
    }

    private static void Signal(List<TaskCompletionSource>? waiters)
    {
        if (waiters == null)
        {
            return;
        }

        foreach (var waiter in waiters)
        {
            waiter.TrySetResult();
        }
    }

    private sealed class WalDto
    {
        public string Kind { get; set; } = "";
        public ulong AccountId { get; set; }
        public int Gold { get; set; }
        public long AtTicks { get; set; }
        public string? PlayerName { get; set; }
        public string? IpAddress { get; set; }
        public ulong RoomId { get; set; }
        public string? Action { get; set; }
        public int PlayerCount { get; set; }
    }
}
