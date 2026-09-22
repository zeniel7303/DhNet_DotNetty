using Common.Logging;
using GameServer.Database;
using GameServer.Database.Gateway;
using GameServer.Database.Grpc;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace DBServer;

/// <summary>
/// gRPC 요청을 <see cref="IDbGateway"/>로 넘긴다.
/// 저장 계열은 큐에 넣은 뒤 바로 반환하고, flush만 큐가 끝날 때까지 기다린다.
/// </summary>
public sealed class DbGatewayGrpcService(
    IDbGateway gateway,
    IDbIdSeeds seeds,
    GatewaySignalHub hub,
    OnlineCountSource onlineCount) : DbGatewayApi.DbGatewayApiBase
{
    public override async Task<IdSeeds> GetIdSeeds(Empty request, ServerCallContext context)
    {
        var read = await Guard(ct => seeds.ReadIdSeedsAsync(ct), context.CancellationToken);
        return new IdSeeds
        {
            MaxAccountId = read.MaxAccountId,
            MaxRoomId = read.MaxRoomId
        };
    }

    public override async Task<RowCount> RegisterAccount(AccountMessage request, ServerCallContext context)
    {
        var count = await Guard(
            ct => gateway.RegisterAccountAsync(DbGatewayMapper.ToRow(request), ct),
            context.CancellationToken);
        return new RowCount { Value = count };
    }

    public override async Task<AuthLoadResponse> AuthenticateAndLoad(UsernameRequest request, ServerCallContext context)
    {
        var loaded = await Guard(
            ct => gateway.AuthenticateAndLoadAsync(request.Username, ct),
            context.CancellationToken);
        if (loaded == null)
        {
            return new AuthLoadResponse { Found = false };
        }

        var response = new AuthLoadResponse
        {
            Found = true,
            Account = DbGatewayMapper.ToMessage(loaded.Value.Account)
        };
        if (loaded.Value.Character is { } character)
        {
            response.HasCharacter = true;
            response.Character = DbGatewayMapper.ToMessage(character);
        }

        return response;
    }

    public override async Task<CharacterMessage> CreateDefaultCharacter(AccountIdRequest request, ServerCallContext context)
    {
        var row = await Guard(
            ct => gateway.CreateDefaultCharacterAsync(request.AccountId, ct),
            context.CancellationToken);
        return DbGatewayMapper.ToMessage(row);
    }

    public override async Task<Empty> InsertPlayerSession(PlayerMessage request, ServerCallContext context)
    {
        await Guard(
            ct => gateway.InsertPlayerSessionAsync(DbGatewayMapper.ToRow(request), ct),
            context.CancellationToken);
        return new Empty();
    }

    public override async Task<Empty> DeleteExpiredPasswordResetTokens(Empty request, ServerCallContext context)
    {
        await Guard(ct => gateway.DeleteExpiredPasswordResetTokensAsync(ct), context.CancellationToken);
        return new Empty();
    }

    public override async Task<AccountResponse> FindAccountByUsername(UsernameRequest request, ServerCallContext context)
    {
        var account = await Guard(
            ct => gateway.FindAccountByUsernameAsync(request.Username, ct),
            context.CancellationToken);
        return account == null
            ? new AccountResponse { Found = false }
            : new AccountResponse { Found = true, Account = DbGatewayMapper.ToMessage(account) };
    }

    public override async Task<Empty> InsertPasswordResetToken(PasswordResetTokenMessage request, ServerCallContext context)
    {
        await Guard(
            ct => gateway.InsertPasswordResetTokenAsync(DbGatewayMapper.ToRow(request), ct),
            context.CancellationToken);
        return new Empty();
    }

    public override async Task<PasswordResetTokenResponse> FindPasswordResetToken(TokenRequest request, ServerCallContext context)
    {
        var row = await Guard(
            ct => gateway.FindPasswordResetTokenAsync(request.Token, ct),
            context.CancellationToken);
        return row == null
            ? new PasswordResetTokenResponse { Found = false }
            : new PasswordResetTokenResponse { Found = true, Token = DbGatewayMapper.ToMessage(row) };
    }

    public override async Task<RowCount> ConsumePasswordResetToken(TokenIdRequest request, ServerCallContext context)
    {
        var count = await Guard(
            ct => gateway.ConsumePasswordResetTokenAsync(request.TokenId, ct),
            context.CancellationToken);
        return new RowCount { Value = count };
    }

    public override async Task<Empty> UpdatePasswordHash(UpdatePasswordRequest request, ServerCallContext context)
    {
        await Guard(
            ct => gateway.UpdatePasswordHashAsync(request.AccountId, request.PasswordHash, ct),
            context.CancellationToken);
        return new Empty();
    }

    public override async Task<ChatLogList> QueryChatLogs(ChatLogQuery request, ServerCallContext context)
    {
        var rows = await Guard(ct => gateway.QueryChatLogsAsync(
            request.HasAccountId ? request.AccountId : null,
            request.HasRoomId ? request.RoomId : null,
            DbGatewayMapper.OptionalTicks(request.HasStartTicks, request.StartTicks),
            DbGatewayMapper.OptionalTicks(request.HasEndTicks, request.EndTicks),
            request.Limit,
            ct), context.CancellationToken);
        var list = new ChatLogList();
        list.Rows.AddRange(rows.Select(DbGatewayMapper.ToMessage));
        return list;
    }

    public override async Task<LoginLogList> QueryLoginLogs(LoginLogQuery request, ServerCallContext context)
    {
        var rows = await Guard(ct => gateway.QueryLoginLogsAsync(
            request.HasAccountId ? request.AccountId : null,
            DbGatewayMapper.OptionalTicks(request.HasStartTicks, request.StartTicks),
            DbGatewayMapper.OptionalTicks(request.HasEndTicks, request.EndTicks),
            request.Limit,
            ct), context.CancellationToken);
        var list = new LoginLogList();
        list.Rows.AddRange(rows.Select(DbGatewayMapper.ToMessage));
        return list;
    }

    public override async Task<RoomLogList> QueryRoomLogs(RoomLogQuery request, ServerCallContext context)
    {
        var rows = await Guard(ct => gateway.QueryRoomLogsAsync(
            request.HasAccountId ? request.AccountId : null,
            request.HasRoomId ? request.RoomId : null,
            request.HasAction ? request.Action : null,
            DbGatewayMapper.OptionalTicks(request.HasStartTicks, request.StartTicks),
            DbGatewayMapper.OptionalTicks(request.HasEndTicks, request.EndTicks),
            request.Limit,
            ct), context.CancellationToken);
        var list = new RoomLogList();
        list.Rows.AddRange(rows.Select(DbGatewayMapper.ToMessage));
        return list;
    }

    public override async Task<StatLogList> QueryStatHistory(StatQuery request, ServerCallContext context)
    {
        var rows = await Guard(
            ct => gateway.QueryStatHistoryAsync(request.Limit, ct),
            context.CancellationToken);
        var list = new StatLogList();
        list.Rows.AddRange(rows.Select(DbGatewayMapper.ToMessage));
        return list;
    }

    public override Task<Empty> UpsertCharacter(CharacterMessage request, ServerCallContext context)
    {
        gateway.UpsertCharacter(DbGatewayMapper.ToRow(request));
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> UpdateLogout(LogoutRequest request, ServerCallContext context)
    {
        gateway.UpdateLogout(request.AccountId, DbGatewayMapper.FromTicks(request.LogoutAtTicks));
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> WriteLoginLog(LoginLogMessage request, ServerCallContext context)
    {
        gateway.WriteLoginLog(DbGatewayMapper.ToRow(request));
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> WriteRoomLog(RoomLogMessage request, ServerCallContext context)
    {
        gateway.WriteRoomLog(DbGatewayMapper.ToRow(request));
        return Task.FromResult(new Empty());
    }

    public override Task<Empty> WriteStatLog(StatLogMessage request, ServerCallContext context)
    {
        gateway.WriteStatLog(DbGatewayMapper.ToRow(request));
        return Task.FromResult(new Empty());
    }

    public override async Task<Empty> FlushAccount(AccountIdRequest request, ServerCallContext context)
    {
        // 클라이언트 데드라인과 분리한다. MySQL이 죽어도 WAL에 남긴 뒤 반환해야 한다.
        await gateway.FlushAccountAsync(request.AccountId);
        return new Empty();
    }

    public override Task<Empty> ReportOnlineCount(OnlineCountRequest request, ServerCallContext context)
    {
        onlineCount.Set(request.Count);
        return Task.FromResult(new Empty());
    }

    public override Task WatchServer(Empty request, IServerStreamWriter<ServerSignal> responseStream, ServerCallContext context)
    {
        var serve = hub.ServeAsync(responseStream, context.CancellationToken);
        hub.PublishLoginBlocked(gateway.IsLoginBlocked);
        if (gateway.IsLoginBlocked)
        {
            hub.PublishOutage(gateway.ParkedAccounts);
        }

        return serve;
    }

    private static async Task<T> Guard<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        try
        {
            return await call(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (TimeoutException ex)
        {
            throw new RpcException(new Status(StatusCode.DeadlineExceeded, ex.Message));
        }
        catch (RpcException)
        {
            throw;
        }
        catch (Exception ex)
        {
            GameLogger.Error("DbGateway", "DB 요청 처리 실패", ex);
            throw new RpcException(new Status(StatusCode.Internal, "DB 처리에 실패했습니다."));
        }
    }

    private static async Task Guard(Func<CancellationToken, Task> call, CancellationToken cancellationToken)
    {
        await Guard(async ct =>
        {
            await call(ct);
            return 0;
        }, cancellationToken);
    }
}
