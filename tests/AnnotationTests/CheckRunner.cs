// 自检工程共用的断言收集器。
//
// 原先四个校验文件各自写一份 `int passed` + `List<string> failures` + 局部 `Check(...)`,
// 样板重复且容易漏掉累加 (漏了就会"假通过")。这里统一成一个可复用的收集器。
namespace SnipasteOcr.Tests;

/// <summary>
/// 断言收集器: 记录通过/失败条数, 并可按需即时回显 (顶层脚本用)。
/// </summary>
internal sealed class CheckRunner
{
    private readonly List<string> _failures = [];
    private readonly bool _echo;
    private int _passed;

    /// <param name="echo">是否每条都立即打印 (供顶层脚本实时观察)</param>
    public CheckRunner(bool echo = false) => _echo = echo;

    /// <summary>已通过条数</summary>
    public int Passed => _passed;

    /// <summary>失败说明列表</summary>
    public IReadOnlyList<string> Failures => _failures;

    /// <summary>断言: 条件成立记一条通过, 否则记一条失败 (附上名称便于定位)</summary>
    public void Check(bool condition, string name)
    {
        if (condition)
            _passed++;
        else
            _failures.Add(name);

        if (_echo)
            Console.WriteLine((condition ? "PASS  " : "FAIL  ") + name);
    }

    /// <summary>把失败项逐条打印出来 (供各分组输出)</summary>
    public void ReportFailures()
    {
        foreach (string f in _failures)
            Console.WriteLine("FAIL  " + f);
    }

    /// <summary>作为 (通过数, 失败列表) 返回, 便于调用方汇总</summary>
    public (int Passed, List<string> Failures) ToResult() => (_passed, [.. _failures]);
}
