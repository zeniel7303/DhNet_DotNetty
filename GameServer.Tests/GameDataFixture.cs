using GameServer.Resources;

namespace GameServer.Tests;

/// <summary>
/// GameDataTable.Load()를 테스트 실행 전 1회만 수행하는 xUnit 픽스처.
/// GarlicWeaponTests, WeaponComponentTests 등 GameDataTable이 필요한 테스트에 사용.
/// </summary>
public class GameDataFixture
{
    public GameDataFixture()
    {
        // 테스트 출력 디렉토리(bin/Debug/net9.0/)에서 프로젝트 루트의 Bin/resources로 올라간다.
        var resourceDir = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "../../../../Bin/resources"));

        GameDataTable.Load(resourceDir);
    }
}
