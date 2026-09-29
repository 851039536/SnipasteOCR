// 交互式验证入口: 真实创建窗口, 驱动"选文字工具 -> 点击 -> 打字 -> 回车"全流程。
// 需要桌面会话; 无桌面时直接报告并退出。
namespace SnipasteOcr.InteractiveTests;

internal static class InteractiveMain
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var (passed, failures) = SnipasteOcr.Tests.InteractiveCheck.Run();

            foreach (var f in failures)
                Console.WriteLine("FAIL  " + f);

            Console.WriteLine();
            Console.WriteLine($"交互检查: {passed} 通过, {failures.Count} 失败");
            return failures.Count == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine("交互测试异常: " + ex);
            return 2;
        }
    }
}