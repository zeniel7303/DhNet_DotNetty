using Common.Logging;
using Common.Server.Component;
using GameServer.Database.Gateway;
using GameServer.Database.Rows;
using GameServer.World;

namespace GameServer.Component.Player;

/// <summary>
/// 플레이어 DB 저장 책임 전담 컴포넌트.
/// 접속 해제 시 최종 저장(SaveAsync)과 주기적 저장(Update dirty-flag 기반)을 처리한다.
/// </summary>
public class PlayerSaveComponent : BaseComponent
{
    private readonly PlayerComponent _player;

    // 0 = Players.InsertAsync 미완료, 1 = 완료 — SaveAsync 실행 허용 조건
    private int _dbInserted;

    // 0 = 저장 불필요, 1 = dirty (주기적 저장 대상)
    private int _isDirty;

    public bool IsDirty => Volatile.Read(ref _isDirty) == 1;

    // 0 = 정상, 1 = 접속 해제 진행 중 — 주기적 저장 중복 방지
    private int _disconnecting;

    // 주기적 저장 누산기 (단일 틱 스레드에서만 접근)
    private float _saveAcc;
    private const float SaveInterval = 60f;

    public PlayerSaveComponent(PlayerComponent player)
    {
        _player = player;
    }

    // 의도적 빈 구현 — BaseComponent 상속 컨벤션 준수
    public override void Initialize() { }
    protected override void OnDispose() { }

    // LoginProcessor: Players.InsertAsync 완료 후 호출 — 이후 SaveAsync 실행 허용
    public void MarkDbInserted() => Interlocked.Exchange(ref _dbInserted, 1);

    // 저장이 필요한 상태 변경 시 호출 (골드 변경 등)
    // 단일 틱 스레드 외부(ex. ThreadPool)에서도 호출될 수 있으므로 Volatile 사용
    public void MarkDirty() => Volatile.Write(ref _isDirty, 1);

    public void MarkDisconnecting() => Interlocked.Exchange(ref _disconnecting, 1);

    public CharacterRow? ReadSnapshot()
    {
        if (Volatile.Read(ref _dbInserted) == 0)
            return null;

        return _player.Character.ToRow();
    }

    public CharacterRow? TryConsumeDirtySnapshot()
    {
        if (Volatile.Read(ref _dbInserted) == 0 || Volatile.Read(ref _disconnecting) == 1)
            return null;

        if (Interlocked.Exchange(ref _isDirty, 0) == 0)
            return null;

        return _player.Character.ToRow();
    }

    // 주기적 저장 — PlayerComponent.Update(dt)에서 호출
    // 단일 틱 스레드에서만 실행되므로 _saveAcc는 volatile 불필요
    public override void Update(float dt)
    {
        base.Update(dt);
        if (IsDisposed || Volatile.Read(ref _dbInserted) == 0)
        {
            return;
        }

        // 접속 해제(SaveAsync) 진행 중이면 중복 upsert 방지를 위해 주기적 저장 스킵
        if (Volatile.Read(ref _disconnecting) == 1)
        {
            return;
        }

        // 존에 있는 동안 주기 적재는 ZoneLoop가 맡는다.
        if (ContentZone.Shared.IsSpawned(_player.AccountId))
        {
            return;
        }

        _saveAcc += dt;
        if (_saveAcc < SaveInterval)
        {
            return;
        }
        _saveAcc = 0f;

        if (Interlocked.Exchange(ref _isDirty, 0) == 0)
        {
            return;
        }

        // 틱에서는 큐에만 넣는다. DB 대기는 게이트웨이 워커가 맡는다.
        try
        {
            DbGateway.Current.UpsertCharacter(_player.Character.ToRow());
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _isDirty, 1);
            GameLogger.Error("PlayerSaveComponent", $"주기적 캐릭터 저장 큐 적재 실패 (AccountId={_player.AccountId})", ex);
        }
    }

    /// <summary>
    /// 접속 해제 시 최종 저장. Players.InsertAsync가 완료된 경우에만 실행.
    /// </summary>
    public async Task SaveAsync(PlayerCharacterComponent? character, DateTime logoutAt)
    {
        // 플래그 세트 — Update()의 주기적 저장이 이후 틱에서 중복 upsert하지 않도록 방지
        Interlocked.Exchange(ref _disconnecting, 1);

        if (Volatile.Read(ref _dbInserted) == 0)
        {
            return;
        }

        var gateway = DbGateway.Current;
        if (character != null)
        {
            gateway.UpsertCharacter(character.ToRow());
        }

        gateway.UpdateLogout(_player.AccountId, logoutAt);
        await gateway.FlushAccountAsync(_player.AccountId);
    }

    /// <summary>
    /// 존 끊김 경로. 스냅샷 적재는 World가 이미 끝냈으므로 Flush와 로그아웃만 수행한다.
    /// </summary>
    public async Task FlushSessionAsync(DateTime logoutAt)
    {
        Interlocked.Exchange(ref _disconnecting, 1);
        if (Volatile.Read(ref _dbInserted) == 0)
            return;

        var gateway = DbGateway.Current;
        gateway.UpdateLogout(_player.AccountId, logoutAt);
        await gateway.FlushAccountAsync(_player.AccountId);
    }
}
