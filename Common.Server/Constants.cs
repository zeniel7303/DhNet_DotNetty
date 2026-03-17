namespace Common.Server;

public static class ServerConstants
{
    /// <summary>서버 최대 동시 접속자 수 기본값 — appsettings.json의 GameServer:MaxPlayers로 오버라이드 가능</summary>
    public const int MaxPlayers = 1000;
}
