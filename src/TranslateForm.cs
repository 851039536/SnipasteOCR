using SnipasteOcr.Annotations;

namespace SnipasteOcr;

/// <summary>
/// 独立译文窗口: 原文与译文上下对照, 只读 + 复制/关闭。
///
/// 为什么不把译文塞进 <see cref="OcrResultForm"/>: 结果窗口是整屏自绘的图片视口,
/// 缩放/命中/DPI 全部围绕图片坐标写死; 加一列译文要重排整套布局。
/// 独立窗口只需标准控件, 不动现有坐标逻辑, 风险最小。
/// </summary>
internal sealed class TranslateForm : Form
{
    private const float UiFontSize = 10f;
    private static readonly Color TextColor = Color.FromArgb(226, 230, 236);

    private readonly TextBox _source;
    private readonly TextBox _translation;
    private readonly ToolStrip _toolStrip = new();
    private readonly ToolStripButton _copy = new();
    private readonly ToolStripButton _close = new();
    private readonly ToolStripLabel _status = new();
    private CancellationTokenSource? _cts;
    private readonly Func<CancellationToken, Task<TranslateResult>> _translate;
    private string _translated = "";

    /// <param name="sourceText">已按上限截断的原文</param>
    /// <param name="direction">翻译方向展示文案 (如「English」或「中文 → English」)</param>
    /// <param name="truncated">原文是否被截断 (界面要如实标注)</param>
    /// <param name="translate">实际执行翻译的回调; 测试可注入假实现, 不联网</param>
    public TranslateForm(
        string sourceText,
        string direction,
        bool truncated,
        Func<CancellationToken, Task<TranslateResult>> translate)
    {
        _translate = translate;

        Text = $"SnipasteOCR - 翻译（{direction}）";
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        ShowInTaskbar = false;
        // 结果窗口常贴着截图尺寸收缩, 译文窗口过大会盖住它甚至越出屏幕; 默认给一个紧凑尺寸
        ClientSize = new Size(560, 400);
        MinimumSize = new Size(380, 260);
        BackColor = UiTheme.Canvas;
        ForeColor = TextColor;
        Font = UiFont.Create(UiFontSize);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);
        KeyPreview = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(8, 6, 8, 6),
            BackColor = UiTheme.Canvas,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 42f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20f));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 58f));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30f));

        var sourceLabel = MakeLabel(truncated
            ? $"原文（已截断，仅翻译前 {TranslateClient.MaxInputChars} 字）"
            : "原文");
        _source = MakeTextBox(sourceText);
        var translationLabel = MakeLabel($"译文（{direction}）");
        _translation = MakeTextBox("正在翻译\u2026");

        BuildToolStrip();

        layout.Controls.Add(sourceLabel, 0, 0);
        layout.Controls.Add(_source, 0, 1);
        layout.Controls.Add(translationLabel, 0, 2);
        layout.Controls.Add(_translation, 0, 3);
        layout.Controls.Add(_toolStrip, 0, 4);
        Controls.Add(layout);

        Shown += (_, _) => StartTranslation();
        // 只取消不释放: 释放由 StartTranslation 的 finally 负责 (请求结束/取消后才安全)。
        // 关闭时若提前 Dispose, FormClosed 的 Cancel() 会抛 ObjectDisposedException, 窗口就关不掉。
        FormClosed += (_, _) => _cts?.Cancel();
    }

    // ===== 测试钩子 (仅 InteractiveTests 使用, 生产路径不得调用) =====
    internal string SourceTextForTest => _source.Text;
    internal string TranslationTextForTest => _translation.Text;
    internal bool CopyEnabledForTest => _copy.Enabled;
    internal string StatusTextForTest => _status.Text ?? "";

    private static Label MakeLabel(string text) => new()
    {
        Text = text,
        Dock = DockStyle.Fill,
        TextAlign = ContentAlignment.MiddleLeft,
        ForeColor = UiTheme.MutedText,
        AutoSize = false,
    };

    private static TextBox MakeTextBox(string text) => new()
    {
        Multiline = true,
        ReadOnly = true,
        WordWrap = true,
        ScrollBars = ScrollBars.Vertical,
        Dock = DockStyle.Fill,
        BackColor = UiTheme.Surface,
        ForeColor = TextColor,
        BorderStyle = BorderStyle.FixedSingle,
        Font = UiFont.Create(11f),
        Text = text,
    };

    private void BuildToolStrip()
    {
        _toolStrip.Dock = DockStyle.Fill;
        _toolStrip.GripStyle = ToolStripGripStyle.Hidden;
        _toolStrip.RenderMode = ToolStripRenderMode.Professional;
        _toolStrip.Renderer = new DarkToolStripRenderer();
        _toolStrip.BackColor = UiTheme.Surface;
        _toolStrip.ForeColor = TextColor;
        _toolStrip.Font = UiFont.Create(UiFontSize);
        _toolStrip.Padding = new Padding(4, 1, 4, 1);
        _toolStrip.CanOverflow = false;

        _copy.Text = "复制译文";
        _copy.ToolTipText = "复制译文到剪贴板";
        _copy.Enabled = false;
        _copy.Click += (_, _) => CopyTranslation();

        _status.Text = "正在翻译\u2026";
        _status.ForeColor = UiTheme.MutedText;

        _close.Text = "关闭";
        _close.ToolTipText = "关闭翻译窗口（Esc）";
        _close.Alignment = ToolStripItemAlignment.Right;
        _close.Click += (_, _) => Close();

        _toolStrip.Items.Add(_copy);
        _toolStrip.Items.Add(_status);
        _toolStrip.Items.Add(_close);
    }

    /// <summary>
    /// 后台发起翻译; 结果回到 UI 线程。窗口在等待期间仍可拖动/关闭,
    /// 关闭时 <see cref="_cts"/> 取消请求, 不会在已销毁的控件上更新文本。
    /// </summary>
    private async void StartTranslation()
    {
        // 每次翻译窗口只发起一次请求; CTS 由本方法独占, 结束后在 finally 释放
        var cts = new CancellationTokenSource();
        _cts = cts;
        try
        {
            var result = await _translate(cts.Token);
            if (cts.IsCancellationRequested || IsDisposed || Disposing)
                return;

            if (result.Ok)
            {
                _translated = result.Text;
                _translation.Text = result.Text;
                _copy.Enabled = true;
                int tokens = result.PromptTokens + result.CompletionTokens;
                string tokenText = tokens > 0 ? $" · {tokens} tokens" : "";
                _status.Text = "完成" + tokenText + (result.Truncated ? " · 译文可能被截断" : "");
            }
            else
            {
                _translation.Text = result.Error ?? "翻译失败。";
                _status.Text = "翻译失败";
            }
        }
        catch (OperationCanceledException)
        {
            if (!IsDisposed && !Disposing)
            {
                _translation.Text = "已取消。";
                _status.Text = "已取消";
            }
        }
        catch (Exception ex)
        {
            // 这里是 UI 兜底: 网络层已把可预期错误转成中文提示, 走到这说明是未预期异常
            if (!IsDisposed && !Disposing)
            {
                _translation.Text = "翻译失败：" + ex.Message;
                _status.Text = "翻译失败";
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
                _cts = null;
            cts.Dispose();
        }
    }

    private void CopyTranslation()
    {
        if (string.IsNullOrWhiteSpace(_translated))
            return;

        _status.Text = ClipboardText.TrySet(_translated)
            ? $"已复制 {_translated.Length} 字符"
            : "复制失败（剪贴板被占用？）";
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape)
        {
            Close();
            return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
