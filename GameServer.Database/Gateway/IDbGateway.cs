using GameServer.Database.Rows;

namespace GameServer.Database.Gateway;

/// <summary>
/// GameServer가 DB에 접근하는 유일한 계약.
/// 요청-응답(계정·인증·관리 API)은 호출자가 완료를 기다리고,
/// 캐릭터 저장·로그아웃·로그는 큐에 넣은 뒤 즉시 반환한다.
/// </summary>
public interface IDbGateway
{
    // 요청-응답. 틱 루프에서 호출하지 않는다.
    Task<int> RegisterAccountAsync(AccountRow account);

    /// <summary>
    /// username으로 계정과 캐릭터 스냅샷(account_id, gold)을 읽는다.
    /// 계정이 없으면 null. 캐릭터 행이 없으면 Character는 null이며 생성하지 않는다.
    /// </summary>
    Task<AuthenticateAndLoadResult?> AuthenticateAndLoadAsync(string username);

    /// <summary>최초 로그인 시 기본 캐릭터(gold 0)를 동기 저장한다.</summary>
    Task<CharacterRow> CreateDefaultCharacterAsync(ulong accountId);

    Task InsertPlayerSessionAsync(PlayerRow player);

    Task DeleteExpiredPasswordResetTokensAsync();
    Task<AccountRow?> FindAccountByUsernameAsync(string username);
    Task InsertPasswordResetTokenAsync(PasswordResetTokenRow row);
    Task<PasswordResetTokenRow?> FindPasswordResetTokenAsync(string token);
    Task<int> ConsumePasswordResetTokenAsync(ulong tokenId);
    Task UpdatePasswordHashAsync(ulong accountId, string passwordHash);

    Task<IReadOnlyList<ChatLogRow>> QueryChatLogsAsync(
        ulong? accountId, ulong? roomId, DateTime? startTime, DateTime? endTime, int limit);
    Task<IReadOnlyList<LoginLogRow>> QueryLoginLogsAsync(
        ulong? accountId, DateTime? startTime, DateTime? endTime, int limit);
    Task<IReadOnlyList<RoomLogRow>> QueryRoomLogsAsync(
        ulong? accountId, ulong? roomId, string? action, DateTime? startTime, DateTime? endTime, int limit);
    Task<IReadOnlyList<StatLogRow>> QueryStatHistoryAsync(int limit);

    // 비동기 기록. 호출 스레드에서 DB를 기다리지 않는다.
    // 영속 스냅샷은 account_id·gold·로그아웃 시각뿐이다.
    void UpsertCharacter(CharacterRow row);
    void UpdateLogout(ulong accountId, DateTime logoutAt);
    void WriteLoginLog(LoginLogRow row);
    void WriteRoomLog(RoomLogRow row);
    void WriteStatLog(StatLogRow row);

    /// <summary>
    /// 해당 계정의 선행 저장·로그아웃 시도를 끝낸다.
    /// DB가 죽어도 WAL에 남긴 뒤 반환하므로 호출자는 이어서 세션을 제거할 수 있다.
    /// </summary>
    Task FlushAccountAsync(ulong accountId);

    bool IsLoginBlocked { get; }

    /// <summary>지속 장애로 로그인을 막을 때 한 번 호출된다. 인자는 버퍼에 남은 계정이다.</summary>
    Action<IReadOnlyList<ulong>>? OnSustainedOutage { get; set; }

    /// <summary>dirty 비율 계산에 쓰는 현재 온라인 세션 수.</summary>
    Func<int>? OnlineCount { get; set; }
}

/// <summary>인증 조회 결과. 비밀번호 검증은 호출자(LoginProcessor)가 한다.</summary>
public readonly record struct AuthenticateAndLoadResult(AccountRow Account, CharacterRow? Character);
