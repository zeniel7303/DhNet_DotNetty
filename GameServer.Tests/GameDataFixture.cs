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
        GameDataTable.Load(FindResourceDir());
    }

    // 출력 경로가 bin/Debug/net9.0 이든 Bin/output/Debug/... 이든 Bin/resources 를 찾는다.
    private static string FindResourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Bin", "resources");
            if (File.Exists(Path.Combine(candidate, "player.json")))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Bin/resources 를 찾지 못했습니다.");
    }
}
