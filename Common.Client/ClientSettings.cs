namespace Common;

using System.Net;

public class ClientSettings
{
    public static IPAddress Host => IPAddress.Parse("127.0.0.1");

    public static readonly int Port = 8007;

    public static readonly int Size = 256;
}