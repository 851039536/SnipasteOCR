using System.Runtime.InteropServices;

// 自检必须显式构造 ExternalException/COMException 才能验证 HResult 分类,
// 分析器的"不要抛运行时保留异常"在这里不适用。
#pragma warning disable CA2201

namespace SnipasteOcr.Tests;

/// <summary>剪贴板异常分类: 只有 CLIPBRD_E_CANT_OPEN 才是"被占用"</summary>
internal static class ClipboardImageCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();

        runner.Check(
            ClipboardErrors.IsBusy(new ExternalException("busy", ClipboardErrors.ClipbrdCantOpen)),
            "ExternalException 0x800401D0 判为被占用");
        runner.Check(
            ClipboardErrors.IsBusy(new COMException("busy", ClipboardErrors.ClipbrdCantOpen)),
            "COMException 同码也判为被占用");
        runner.Check(
            !ClipboardErrors.IsBusy(new InvalidOperationException("x")),
            "普通异常不判为被占用");
        runner.Check(
            !ClipboardErrors.IsBusy(new ExternalException("other", unchecked((int)0x80004005))),
            "其它 HResult 不判为被占用");
        runner.Check(!ClipboardErrors.IsBusy(null), "null 不判为被占用");

        return runner.ToResult();
    }
}
