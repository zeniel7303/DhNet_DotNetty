namespace GameServer.Auth;

internal static class AuthConstants
{
    /// <summary>
    /// BCrypt work factor. RegisterProcessor(해시 생성)와 LoginProcessor(_dummyHash)가
    /// 동일한 값을 공유해야 Timing Attack 방어의 연산 비용이 균일하게 유지된다.
    /// </summary>
    public const int BcryptWorkFactor = 11;
}
