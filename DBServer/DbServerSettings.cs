namespace DBServer;

/// <summary>
/// DBServer 수신 설정. 기본은 같은 장비의 루프백이다.
/// 원격으로 바꿀 때는 BindAddress와 TLS(또는 사설망)를 함께 지정한다.
/// </summary>
public sealed class DbServerSettings
{
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 50051;

    /// <summary>
    /// TLS가 꺼져 있으면 gRPC(HTTP/2)와 /health(HTTP/1.1)를 나눈다.
    /// 0이면 gRPC 포트 다음 번호를 쓴다. TLS가 켜지면 같은 포트에서 둘 다 받는다.
    /// </summary>
    public int HealthPort { get; set; }

    public bool UseTls { get; set; }
    public string CertificatePath { get; set; } = "";
    public string CertificatePassword { get; set; } = "";

    /// <summary>비어 있으면 실행 파일 옆 db-wal 디렉터리를 쓴다.</summary>
    public string WalDirectory { get; set; } = "";
}
