using System.Runtime.CompilerServices;

namespace NowBar.Tests;

internal static class TestEnvironment
{
    /// <summary>테스트가 실제 %AppData%\NowBar 에 로그/설정을 쓰지 않도록 임시 폴더로 돌린다.</summary>
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("NOWBAR_DATA_DIR", Path.Combine(Path.GetTempPath(), "nowbar-tests-data"));
}
