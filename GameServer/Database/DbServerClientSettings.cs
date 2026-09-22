namespace GameServer.Database;

/// <summary>
/// GameServer가 붙는 DBServer 주소.
/// 같은 장비는 http://127.0.0.1:50051, 원격은 호스트만 바꾸거나 https 주소를 넣는다.
/// </summary>
public sealed class DbServerClientSettings
{
    public string Address { get; set; } = "http://127.0.0.1:50051";

    /// <summary>true면 기동 시 DBServer에 붙지 못하면 프로세스를 끝낸다.</summary>
    public bool RequireConnection { get; set; }
}
