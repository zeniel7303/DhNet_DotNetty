using System.Threading.Channels;
using GameServer.Database.Grpc;
using Grpc.Core;

namespace DBServer;

/// <summary>
/// 로그인 차단, 지속 장애, WAL 기록 실패를 구독 중인 GameServer로 보낸다.
/// 스트림 쓰기는 구독자마다 채널로 직렬화한다.
/// </summary>
public sealed class GatewaySignalHub
{
    private readonly List<Subscriber> _subscribers = new();
    private readonly object _sync = new();

    public void PublishLoginBlocked(bool blocked)
        => Publish(new ServerSignal { LoginBlocked = new LoginBlocked { Blocked = blocked } });

    public void PublishOutage(IReadOnlyList<ulong> accountIds)
    {
        var signal = new ServerSignal { SustainedOutage = new AccountList() };
        signal.SustainedOutage.AccountIds.AddRange(accountIds.Select(id => id));
        Publish(signal);
    }

    public void PublishWalFailure(ulong accountId)
        => Publish(new ServerSignal { WalWriteFailedAccount = accountId });

    public async Task ServeAsync(IServerStreamWriter<ServerSignal> stream, CancellationToken cancellationToken)
    {
        var subscriber = new Subscriber(stream);
        lock (_sync)
        {
            _subscribers.Add(subscriber);
        }

        try
        {
            await subscriber.Completion.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            subscriber.Complete();
            lock (_sync)
            {
                _subscribers.Remove(subscriber);
            }
        }
    }

    private void Publish(ServerSignal signal)
    {
        Subscriber[] snapshot;
        lock (_sync)
        {
            snapshot = _subscribers.ToArray();
        }

        foreach (var subscriber in snapshot)
        {
            subscriber.Enqueue(signal);
        }
    }

    private sealed class Subscriber
    {
        private readonly Channel<ServerSignal> _pending = Channel.CreateUnbounded<ServerSignal>(
            new UnboundedChannelOptions { SingleReader = true });
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Subscriber(IServerStreamWriter<ServerSignal> stream)
        {
            _ = WriteAsync(stream);
        }

        public Task Completion => _completion.Task;

        public void Enqueue(ServerSignal signal) => _pending.Writer.TryWrite(signal);

        public void Complete() => _pending.Writer.TryComplete();

        private async Task WriteAsync(IServerStreamWriter<ServerSignal> stream)
        {
            try
            {
                await foreach (var signal in _pending.Reader.ReadAllAsync())
                {
                    await stream.WriteAsync(signal);
                }
            }
            catch (Exception)
            {
                _pending.Writer.TryComplete();
            }
            finally
            {
                _completion.TrySetResult();
            }
        }
    }
}
