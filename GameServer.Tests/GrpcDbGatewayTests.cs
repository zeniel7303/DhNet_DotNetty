using System.Net;
using DBServer;
using GameServer.Database;
using GameServer.Database.Gateway;
using GameServer.Database.Rows;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace GameServer.Tests;

/// <summary>
/// GameServer 게이트웨이가 DBServer gRPC로 저장을 넘기고, 끊김 신호를 받는다.
/// </summary>
public class GrpcDbGatewayTests
{
    [Fact(Timeout = 15000)]
    public async Task Upsert_Returns_Before_Flush_And_Preserves_Order()
    {
        var fake = new FakeGateway();
        await using var host = await TestDbServer.StartAsync(fake);
        await using var gateway = new GrpcDbGateway(host.Address, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(5));
        await gateway.StartAsync(requireConnection: true);

        gateway.UpsertCharacter(new CharacterRow { account_id = 7, gold = 1 });
        gateway.UpsertCharacter(new CharacterRow { account_id = 7, gold = 2 });
        var flush = gateway.FlushAccountAsync(7);

        await fake.FlushEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(flush.IsCompleted);
        Assert.Equal(new[] { 1, 2 }, fake.Golds);

        fake.ReleaseFlush();
        await flush.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact(Timeout = 15000)]
    public async Task Sync_Call_Times_Out()
    {
        var fake = new FakeGateway { RegisterHangs = true };
        await using var host = await TestDbServer.StartAsync(fake);
        await using var gateway = new GrpcDbGateway(host.Address, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(5));
        await gateway.StartAsync(requireConnection: true);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            gateway.RegisterAccountAsync(new AccountRow { username = "a", password_hash = "b" }));
    }

    [Fact(Timeout = 15000)]
    public async Task Wal_Failure_And_Login_Block_Cross_The_Stream()
    {
        var fake = new FakeGateway();
        await using var host = await TestDbServer.StartAsync(fake);
        var wal = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var snapshot = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var gateway = new GrpcDbGateway(host.Address);
        gateway.OnWalWriteFailed = id => wal.TrySetResult(id);
        gateway.OnLoginBlockedChanged = value =>
        {
            snapshot.TrySetResult();
            if (value)
            {
                blocked.TrySetResult(true);
            }
        };
        await gateway.StartAsync(requireConnection: true);
        await snapshot.Task.WaitAsync(TimeSpan.FromSeconds(3));

        fake.OnWalWriteFailed?.Invoke(42);
        fake.OnLoginBlockedChanged?.Invoke(true);

        Assert.Equal(42ul, await wal.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(await blocked.Task.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(gateway.IsLoginBlocked);
    }

    [Fact]
    public void GameServer_Does_Not_Reference_DbConnector_Or_DbSet()
    {
        var root = FindRepoRoot();
        var gameServer = Path.Combine(root, "GameServer");
        var csproj = File.ReadAllText(Path.Combine(gameServer, "GameServer.csproj"));
        Assert.DoesNotContain("GameServer.Database.csproj", csproj);

        var sources = Directory.GetFiles(gameServer, "*.cs", SearchOption.AllDirectories);
        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            Assert.DoesNotContain("DbConnector", text);
            Assert.DoesNotContain("DbSet", text);
            Assert.DoesNotContain("DatabaseSystem", text);
            Assert.DoesNotContain("LocalDbGateway", text);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotNetty.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("솔루션 루트를 찾지 못했습니다.");
    }

    private sealed class TestDbServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        private TestDbServer(WebApplication app, string address)
        {
            _app = app;
            Address = address;
        }

        public string Address { get; }

        public static async Task<TestDbServer> StartAsync(FakeGateway gateway)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = "DBServer",
                EnvironmentName = Environments.Development
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options =>
            {
                options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2);
            });
            builder.WebHost.PreferHostingUrls(false);

            var hub = new GatewaySignalHub();
            var online = new OnlineCountSource();
            gateway.OnLoginBlockedChanged = hub.PublishLoginBlocked;
            gateway.OnSustainedOutage = hub.PublishOutage;
            gateway.OnWalWriteFailed = hub.PublishWalFailure;
            builder.Services.AddSingleton<IDbGateway>(gateway);
            builder.Services.AddSingleton<IDbIdSeeds>(new FixedSeeds());
            builder.Services.AddSingleton(hub);
            builder.Services.AddSingleton(online);
            builder.Services.AddGrpc();

            var app = builder.Build();
            app.MapGrpcService<DbGatewayGrpcService>();
            await app.StartAsync();

            var feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            var address = feature?.Addresses.FirstOrDefault()
                ?? throw new InvalidOperationException("테스트 DBServer 주소를 찾지 못했습니다.");
            return new TestDbServer(app, address);
        }

        public async ValueTask DisposeAsync() => await _app.StopAsync();

        private sealed class FixedSeeds : IDbIdSeeds
        {
            public Task<DbIdSeeds> ReadIdSeedsAsync(CancellationToken cancellationToken = default)
                => Task.FromResult(new DbIdSeeds(3, 8));
        }
    }

    private sealed class FakeGateway : IDbGateway
    {
        private readonly TaskCompletionSource _flushRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource FlushEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<int> Golds = new();
        public bool RegisterHangs { get; init; }

        public void ReleaseFlush() => _flushRelease.TrySetResult();

        public Action<IReadOnlyList<ulong>>? OnSustainedOutage { get; set; }
        public Action<ulong>? OnWalWriteFailed { get; set; }
        public Action<bool>? OnLoginBlockedChanged { get; set; }
        public Func<int>? OnlineCount { get; set; }
        public bool IsLoginBlocked { get; private set; }
        public IReadOnlyList<ulong> ParkedAccounts => Array.Empty<ulong>();

        public async Task<int> RegisterAccountAsync(AccountRow account, CancellationToken cancellationToken = default)
        {
            if (!RegisterHangs)
            {
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public Task<AuthenticateAndLoadResult?> AuthenticateAndLoadAsync(string username, CancellationToken cancellationToken = default)
            => Task.FromResult<AuthenticateAndLoadResult?>(null);

        public Task<CharacterRow> CreateDefaultCharacterAsync(ulong accountId, CancellationToken cancellationToken = default)
            => Task.FromResult(new CharacterRow { account_id = accountId });

        public Task InsertPlayerSessionAsync(PlayerRow player, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteExpiredPasswordResetTokensAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<AccountRow?> FindAccountByUsernameAsync(string username, CancellationToken cancellationToken = default)
            => Task.FromResult<AccountRow?>(null);

        public Task InsertPasswordResetTokenAsync(PasswordResetTokenRow row, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<PasswordResetTokenRow?> FindPasswordResetTokenAsync(string token, CancellationToken cancellationToken = default)
            => Task.FromResult<PasswordResetTokenRow?>(null);

        public Task<int> ConsumePasswordResetTokenAsync(ulong tokenId, CancellationToken cancellationToken = default)
            => Task.FromResult(0);

        public Task UpdatePasswordHashAsync(ulong accountId, string passwordHash, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ChatLogRow>> QueryChatLogsAsync(ulong? accountId, ulong? roomId, DateTime? startTime, DateTime? endTime, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ChatLogRow>>(Array.Empty<ChatLogRow>());

        public Task<IReadOnlyList<LoginLogRow>> QueryLoginLogsAsync(ulong? accountId, DateTime? startTime, DateTime? endTime, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<LoginLogRow>>(Array.Empty<LoginLogRow>());

        public Task<IReadOnlyList<RoomLogRow>> QueryRoomLogsAsync(ulong? accountId, ulong? roomId, string? action, DateTime? startTime, DateTime? endTime, int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<RoomLogRow>>(Array.Empty<RoomLogRow>());

        public Task<IReadOnlyList<StatLogRow>> QueryStatHistoryAsync(int limit, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<StatLogRow>>(Array.Empty<StatLogRow>());

        public void UpsertCharacter(CharacterRow row)
        {
            lock (Golds)
            {
                Golds.Add(row.gold);
            }
        }

        public void UpdateLogout(ulong accountId, DateTime logoutAt) { }
        public void WriteLoginLog(LoginLogRow row) { }
        public void WriteRoomLog(RoomLogRow row) { }
        public void WriteStatLog(StatLogRow row) { }

        public async Task FlushAccountAsync(ulong accountId)
        {
            FlushEntered.TrySetResult();
            await _flushRelease.Task;
        }
    }
}
