namespace GameServer.Database.Gateway;

/// <summary>프로세스 전역 게이트웨이. 서버 기동 시 <see cref="Use"/>로 한 번 지정한다.</summary>
public static class DbGateway
{
    private static IDbGateway? _current;

    public static IDbGateway Current =>
        _current ?? throw new InvalidOperationException("DbGateway가 초기화되지 않았습니다.");

    public static void Use(IDbGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        _current = gateway;
    }
}
