using SnipasteOcr.Annotations;

namespace SnipasteOcr;

/// <summary>
/// 翻译设置对话框: API Key / 模型 / 目标语言 / 联网同意状态。
///
/// 为什么要单独一个 Form: 托盘菜单是纯 Win32 (TrackPopupMenu), 无法输入文本;
/// <see cref="HotKeyForm"/> 只有按键录制框, 也不能复用。Key 输入必须用真实文本框。
/// </summary>
public sealed class TranslateSettingsForm : Form
{
    private readonly TextBox _keyBox = new();
    private readonly Label _keyStatus = new();
    private readonly Button _clear = new();
    private readonly TextBox _modelBox = new();
    private readonly ComboBox _targetBox = new();
    private readonly CheckBox _consent = new();
    private readonly CheckBox _clickToTranslate = new();

    private bool _clearPending;

    // ===== 测试钩子 (仅 InteractiveTests 使用, 生产路径不得调用) =====
    internal string KeyStatusForTest => _keyStatus.Text;
    internal string KeyBoxTextForTest => _keyBox.Text;
    internal void SetKeyTextForTest(string text) => _keyBox.Text = text;

    public TranslateSettingsForm()
    {
        var settings = SettingsStore.Current;

        Text = "SnipasteOCR - 翻译设置";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(520, 340);
        Font = UiFont.Create(12f);
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96f, 96f);

        var tip = new Label
        {
            Text = "翻译需要联网: 只把识别出的文字发送到 DeepSeek（api.deepseek.com），" +
                   "不会上传截图；识别本身仍全程离线。",
            Location = new Point(18, 14),
            Size = new Size(484, 48),
            ForeColor = Color.FromArgb(60, 60, 60),
        };

        var keyLabel = new Label
        {
            Text = "API Key：",
            Location = new Point(18, 78),
            Size = new Size(90, 24),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _keyBox.Location = new Point(112, 76);
        _keyBox.Size = new Size(280, 28);
        _keyBox.UseSystemPasswordChar = true;
        _keyBox.PlaceholderText = "留空表示不修改";
        _keyBox.TextChanged += (_, _) =>
        {
            if (_keyBox.TextLength > 0)
            {
                _clearPending = false;
                _clear.Text = "清除已保存的 Key";
                _keyStatus.Text = $"将保存：{ProtectedKey.MaskTail(_keyBox.Text.Trim())}";
            }
            else
            {
                RefreshKeyStatus();
            }
        };

        _clear.Location = new Point(400, 76);
        _clear.Size = new Size(102, 28);
        _clear.Text = "清除已保存的 Key";
        _clear.Enabled = settings.TranslateApiKeyProtected.Length > 0;
        _clear.Click += (_, _) =>
        {
            _clearPending = !_clearPending;
            if (_clearPending)
            {
                _keyBox.Clear();
                _clear.Text = "撤销清除";
            }
            else
            {
                _clear.Text = "清除已保存的 Key";
            }
            RefreshKeyStatus();
        };

        _keyStatus.Location = new Point(112, 108);
        _keyStatus.Size = new Size(390, 22);
        _keyStatus.ForeColor = Color.FromArgb(100, 100, 100);

        var modelLabel = new Label
        {
            Text = "模型：",
            Location = new Point(18, 142),
            Size = new Size(90, 24),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _modelBox.Location = new Point(112, 140);
        _modelBox.Size = new Size(390, 28);
        _modelBox.Text = settings.TranslateModel;
        _modelBox.PlaceholderText = TranslateClient.DefaultModel;

        var targetLabel = new Label
        {
            Text = "目标语言：",
            Location = new Point(18, 182),
            Size = new Size(90, 24),
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _targetBox.Location = new Point(112, 180);
        _targetBox.Size = new Size(390, 28);
        _targetBox.DropDownStyle = ComboBoxStyle.DropDown;
        _targetBox.Items.AddRange([
            AppSettings.AutoTranslateTarget, "English", "简体中文", "日本語", "한국어",
        ]);
        _targetBox.Text = string.IsNullOrWhiteSpace(settings.TranslateTargetLanguage)
            ? AppSettings.AutoTranslateTarget
            : settings.TranslateTargetLanguage;

        _consent.Location = new Point(18, 220);
        _consent.Size = new Size(484, 26);
        _consent.Text = "允许把识别出的文字发送到 DeepSeek 进行翻译";
        _consent.Checked = settings.TranslateConsentGiven;

        // 可选交互: 默认关闭, 不改变原来的"单击复制"习惯
        _clickToTranslate.Location = new Point(18, 250);
        _clickToTranslate.Size = new Size(484, 26);
        _clickToTranslate.Text = "单击文字块翻译，结果显示在窗口底部（不再自动复制）";
        _clickToTranslate.Checked = settings.ClickToTranslate;

        var keyHint = new Label
        {
            Text = "Key 只保存在本机（DPAPI 加密），换机器/账户后需重新输入。",
            Location = new Point(18, 292),
            Size = new Size(290, 24),
            ForeColor = Color.FromArgb(120, 120, 120),
        };

        var ok = new Button
        {
            Text = "保存",
            DialogResult = DialogResult.OK,
            Location = new Point(320, 290),
            Size = new Size(80, 30),
        };
        var cancel = new Button
        {
            Text = "取消",
            DialogResult = DialogResult.Cancel,
            Location = new Point(410, 290),
            Size = new Size(80, 30),
        };
        ok.Click += (_, _) => SaveSettings();

        Controls.AddRange([
            tip, keyLabel, _keyBox, _clear, _keyStatus,
            modelLabel, _modelBox, targetLabel, _targetBox,
            _consent, _clickToTranslate, keyHint, ok, cancel,
        ]);
        AcceptButton = ok;
        CancelButton = cancel;

        RefreshKeyStatus();
    }

    /// <summary>保存设置; 校验失败时保持对话框打开, 不让本次输入丢失</summary>
    private void SaveSettings()
    {
        string model = _modelBox.Text.Trim();
        if (model.Length == 0)
            model = TranslateClient.DefaultModel;

        string target = _targetBox.Text.Trim();
        if (target.Length == 0)
            target = AppSettings.AutoTranslateTarget;

        string protectedKey = _clearPending ? "" : SettingsStore.Current.TranslateApiKeyProtected;

        string typed = _keyBox.Text.Trim();
        if (typed.Length > 0)
        {
            protectedKey = ProtectedKey.Protect(typed);
            if (protectedKey.Length == 0)
            {
                MessageBox.Show(this,
                    "无法安全保存 API Key（系统加密接口不可用），请稍后重试。",
                    "SnipasteOCR", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                DialogResult = DialogResult.None;
                return;
            }
        }

        SettingsStore.Update(s =>
        {
            s.TranslateApiKeyProtected = protectedKey;
            s.TranslateModel = model;
            s.TranslateTargetLanguage = target;
            s.TranslateConsentGiven = _consent.Checked;
            s.ClickToTranslate = _clickToTranslate.Checked;
        });

        DialogResult = DialogResult.OK;
    }

    /// <summary>显示 Key 的真实保存状态: 已保存/未配置/换账户后无法解密</summary>
    private void RefreshKeyStatus()
    {
        if (_clearPending)
        {
            _keyStatus.Text = "保存后将清除已保存的 Key";
            return;
        }

        string stored = SettingsStore.Current.TranslateApiKeyProtected;
        if (stored.Length == 0)
        {
            _keyStatus.Text = "未配置";
            return;
        }

        _keyStatus.Text = ProtectedKey.TryUnprotect(stored, out string plain)
            ? $"已保存：{ProtectedKey.MaskTail(plain)}"
            : "已保存但无法解密（换机器/账户），请重新输入";
    }
}
