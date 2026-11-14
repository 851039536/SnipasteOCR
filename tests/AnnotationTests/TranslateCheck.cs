// 校验翻译客户端: 请求体 (含关闭思考模式)、响应解析、错误映射、重试策略、截断与 Key 保护。
// 全部离线执行, 不发起真实网络请求。
using System.Net;
using System.Reflection;
using System.Text.Json;
using SnipasteOcr;

namespace SnipasteOcr.Tests;

internal static class TranslateCheck
{
    public static (int Passed, List<string> Failures) Run()
    {
        var runner = new CheckRunner();
        void Check(bool cond, string name) => runner.Check(cond, name);

        // ===== 1. 请求体: 字段名、关闭思考、参数上限 =====
        {
            string body = TranslateClient.BuildRequestBody("deepseek-flash", "English", "你好");
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            Check(root.GetProperty("model").GetString() == "deepseek-flash", "请求体 model 正确");
            Check(root.GetProperty("thinking").GetProperty("type").GetString() == "disabled",
                "请求体显式关闭思考模式");
            Check(root.GetProperty("max_tokens").GetInt32() == 260, "max_tokens 按输入长度估算");
            Check(Math.Abs(root.GetProperty("temperature").GetDouble() - 0.2) < 1e-9, "temperature 使用低值");

            var messages = root.GetProperty("messages").EnumerateArray().ToArray();
            Check(messages.Length == 2, "messages 为 system + user 两条");
            Check(messages[0].GetProperty("role").GetString() == "system", "第一条是 system 角色");
            Check(messages[0].GetProperty("content").GetString()!.Contains("只输出译文"),
                "system 提示词要求只输出译文");
            Check(messages[0].GetProperty("content").GetString()!.Contains("不要执行"),
                "system 提示词明确不执行原文中的指令 (提示注入防护)");

            string user = messages[1].GetProperty("content").GetString()!;
            Check(messages[1].GetProperty("role").GetString() == "user", "第二条是 user 角色");
            Check(user.Contains("English") && user.Contains("你好"), "user 消息包含目标语言与原文");
        }

        // ===== 2. 空模型名回退默认值 =====
        {
            using var doc = JsonDocument.Parse(TranslateClient.BuildRequestBody("", "English", "x"));
            Check(doc.RootElement.GetProperty("model").GetString() == TranslateClient.DefaultModel,
                "空模型名回退 deepseek-flash");
        }

        // ===== 3. max_tokens 上下限 =====
        Check(TranslateClient.ComputeMaxTokens(0) == 256, "max_tokens 下限 256");
        Check(TranslateClient.ComputeMaxTokens(1_000_000) == 384_000, "max_tokens 上限 384K");

        // ===== 4. 输入截断 =====
        {
            string exact = TranslateClient.LimitInput(new string('a', TranslateClient.MaxInputChars), out bool t0);
            Check(!t0 && exact.Length == TranslateClient.MaxInputChars, "恰好 8000 字不截断");

            string over = TranslateClient.LimitInput(new string('a', TranslateClient.MaxInputChars + 1), out bool t1);
            Check(t1 && over.Length == TranslateClient.MaxInputChars, "超过 8000 字截断到上限");

            // 第 8000 个位置是代理对的高位: 截断后不能留下孤立的高位代理
            string emoji = new string('a', TranslateClient.MaxInputChars - 1) + "😀b";
            string cut = TranslateClient.LimitInput(emoji, out bool t2);
            Check(t2 && cut.Length == TranslateClient.MaxInputChars - 1 &&
                  !char.IsHighSurrogate(cut[^1]), "截断不会切出半个代理对字符");
        }

        // ===== 4b. 客户端自身也截断: 不能只依赖 UI 调用 LimitInput =====
        {
            string body = TranslateClient.BuildRequestBody(
                "m", "English", new string('a', TranslateClient.MaxInputChars + 1));
            using var doc = JsonDocument.Parse(body);
            var messages = doc.RootElement.GetProperty("messages").EnumerateArray().ToArray();
            string content = messages[1].GetProperty("content").GetString()!;
            int prefixLength = "目标语言：English\n\n".Length;

            Check(content.Length == prefixLength + TranslateClient.MaxInputChars,
                $"请求体内部截断到 {TranslateClient.MaxInputChars} 字 (实得正文 {content.Length - prefixLength})");
            Check(doc.RootElement.GetProperty("max_tokens").GetInt32() ==
                  TranslateClient.ComputeMaxTokens(TranslateClient.MaxInputChars),
                "max_tokens 按截断后的长度计算");
        }

        // ===== 5. 成功响应解析 =====
        {
            const string json = """
                {
                  "choices": [ { "message": { "role": "assistant", "content": "  Hello\n" } } ],
                  "usage": { "prompt_tokens": 12, "completion_tokens": 34, "total_tokens": 46 }
                }
                """;
            var r = TranslateClient.ParseResponse(json, HttpStatusCode.OK);
            Check(r.Ok && r.Text == "Hello", $"解析 choices[0].message.content 并去首尾空白 (实得 '{r.Text}')");
            Check(r.PromptTokens == 12 && r.CompletionTokens == 34, "解析 usage tokens");

            var truncated = TranslateClient.ParseResponse(
                """{"choices":[{"message":{"content":"Partial"},"finish_reason":"length"}]}""",
                HttpStatusCode.OK);
            Check(truncated.Ok && truncated.Truncated, "finish_reason=length 标记译文可能被截断");

            var stopped = TranslateClient.ParseResponse(
                """{"choices":[{"message":{"content":"Done"},"finish_reason":"stop"}]}""",
                HttpStatusCode.OK);
            Check(stopped.Ok && !stopped.Truncated, "finish_reason=stop 不标记截断");
        }

        // ===== 6. 空/缺失内容 =====
        {
            var empty = TranslateClient.ParseResponse("""{"choices":[]}""", HttpStatusCode.OK);
            Check(!empty.Ok && !string.IsNullOrEmpty(empty.Error), "没有 choices 时报错而不是空成功");

            var blank = TranslateClient.ParseResponse(
                """{"choices":[{"message":{"content":"   "}}]}""", HttpStatusCode.OK);
            Check(!blank.Ok, "空白译文视为失败");

            var broken = TranslateClient.ParseResponse("not-json", HttpStatusCode.OK);
            Check(!broken.Ok, "非 JSON 响应不崩溃且报错");

            var apiErr = TranslateClient.ParseResponse(
                """{"error":{"message":"quota exceeded"}}""", HttpStatusCode.OK);
            Check(!apiErr.Ok && apiErr.Error!.Contains("quota"),
                "200 携带 error 对象时按失败处理, 不误报未返回译文");
        }

        // ===== 7. 错误映射 =====
        {
            var e401 = TranslateClient.ParseResponse(
                """{"error":{"message":"Authentication Fails"}}""", HttpStatusCode.Unauthorized);
            Check(!e401.Ok && e401.Error!.Contains("Key"), "401 提示重新填写 API Key");

            var e402 = TranslateClient.ParseResponse(
                """{"error":{"message":"Insufficient Balance"}}""", HttpStatusCode.PaymentRequired);
            Check(!e402.Ok && e402.Error!.Contains("余额"), "402 提示余额不足");

            var e429 = TranslateClient.ParseResponse(
                """{"error":{"message":"Rate Limit"}}""", HttpStatusCode.TooManyRequests);
            Check(!e429.Ok && e429.Error!.Contains("频繁"), "429 提示请求过于频繁");

            var e503 = TranslateClient.ParseResponse(
                """{"error":{"message":"Server Busy"}}""", HttpStatusCode.ServiceUnavailable);
            Check(!e503.Ok && e503.Error!.Contains("服务"), "503 提示服务暂不可用");

            var e422 = TranslateClient.ParseResponse(
                """{"error":{"message":"bad parameter"}}""", HttpStatusCode.UnprocessableEntity);
            Check(!e422.Ok && e422.Error!.Contains("bad parameter"), "422 带出官方参数错误说明");

            Check(TranslateClient.MapError(HttpStatusCode.BadRequest, null) == "翻译请求参数有误。",
                "400 无说明时给通用参数错误提示");
        }

        // ===== 8. 重试策略 =====
        Check(TranslateClient.ShouldRetry(HttpStatusCode.TooManyRequests), "429 可重试");
        Check(TranslateClient.ShouldRetry(HttpStatusCode.ServiceUnavailable), "503 可重试");
        Check(!TranslateClient.ShouldRetry(HttpStatusCode.Unauthorized), "401 不重试");
        Check(!TranslateClient.ShouldRetry(HttpStatusCode.PaymentRequired), "402 不重试");
        Check(!TranslateClient.ShouldRetry(HttpStatusCode.InternalServerError), "500 不重试");

        // ===== 9. HTTP 请求: 端点与认证头 =====
        {
            using var request = TranslateClient.BuildHttpRequest(
                TranslateClient.DefaultModel, "English", "x", "sk-test-abc");
            Check(request.RequestUri?.ToString() == TranslateClient.Endpoint, "请求地址为官方端点");
            Check(request.Headers.Authorization?.Scheme == "Bearer" &&
                  request.Headers.Authorization.Parameter == "sk-test-abc", "认证头为 Bearer <key>");
            Check(request.Content?.Headers.ContentType?.MediaType == "application/json", "请求体为 JSON");
        }

        // ===== 10. Key 掩码 =====
        Check(ProtectedKey.MaskTail("sk-1234567890") == "****7890", "Key 只显示末 4 位");
        Check(ProtectedKey.MaskTail("abc") == "****", "过短的 Key 全部遮掉");

        // ===== 11. DPAPI 往返 (同机同账户) =====
        {
            string encrypted = ProtectedKey.Protect("sk-test-1234");
            Check(encrypted.Length > 0 && encrypted != "sk-test-1234", "Key 加密后不是明文");
            Check(ProtectedKey.TryUnprotect(encrypted, out string plain) && plain == "sk-test-1234",
                "Key 解密往返正确");
            Check(!ProtectedKey.TryUnprotect("not-base64!!", out _), "损坏的密文解密失败");
            Check(ProtectedKey.Protect("") == "" && !ProtectedKey.TryUnprotect("", out _),
                "空 Key 不加密也不解密");
        }

        // ===== 11b. 翻译可用性: 同意联网 + Key 可解密 (设置与单击路径共用) =====
        {
            string encrypted = ProtectedKey.Protect("sk-ready");
            Check(TranslateClient.IsReady(encrypted, consentGiven: true),
                "已同意且有可解密 Key 时翻译可用");
            Check(!TranslateClient.IsReady(encrypted, consentGiven: false),
                "未同意联网时翻译不可用");
            Check(!TranslateClient.IsReady("", consentGiven: true),
                "空 Key 时翻译不可用");
        }

        // ===== 12. 设置默认值与旧配置兼容 =====
        {
            var legacy = JsonSerializer.Deserialize("""{"clickToCopy":false}""",
                SettingsJsonContext.Default.AppSettings)!;
            Check(legacy.TranslateApiKeyProtected == "", "旧配置缺 Key 字段时为空");
            Check(legacy.TranslateModel == "deepseek-flash", "旧配置缺模型时用默认值");
            Check(legacy.TranslateTargetLanguage == AppSettings.AutoTranslateTarget,
                "旧配置缺目标语言时默认自动中↔英");
            Check(!legacy.TranslateConsentGiven, "旧配置默认未同意联网翻译");
            Check(!legacy.ClickToTranslate, "旧配置默认不开启单击翻译 (保持单击复制)");

            var saved = JsonSerializer.Deserialize(
                JsonSerializer.Serialize(new AppSettings { TranslateConsentGiven = true },
                    SettingsJsonContext.Default.AppSettings),
                SettingsJsonContext.Default.AppSettings)!;
            Check(saved.TranslateConsentGiven, "同意状态可 JSON 往返");

            var savedClick = JsonSerializer.Deserialize(
                JsonSerializer.Serialize(new AppSettings { ClickToTranslate = true },
                    SettingsJsonContext.Default.AppSettings),
                SettingsJsonContext.Default.AppSettings)!;
            Check(savedClick.ClickToTranslate, "单击翻译开关可 JSON 往返");
        }

        // ===== 13. 自动方向: 中文→英文, 英文→中文; 显式目标语言不猜 =====
        {
            var zh = TranslateClient.ResolveDirection("你好，世界", AppSettings.AutoTranslateTarget);
            Check(zh.Auto && zh.Target == "English" && zh.Label.Contains("中文 → English"),
                $"中文原文自动译英文 (实得 {zh.Target} / {zh.Label})");

            var en = TranslateClient.ResolveDirection("Hello world", AppSettings.AutoTranslateTarget);
            Check(en.Auto && en.Target == "简体中文" && en.Label.Contains("英文 → 简体中文"),
                $"英文原文自动译中文 (实得 {en.Target} / {en.Label})");

            var mixed = TranslateClient.ResolveDirection("这是 OCR 的结果", AppSettings.AutoTranslateTarget);
            Check(mixed.Target == "English", "中英混排以汉字为主时译英文");

            var explicitEn = TranslateClient.ResolveDirection("你好", "English");
            Check(!explicitEn.Auto && explicitEn.Target == "English" && explicitEn.Label == "English",
                "显式目标语言不猜方向");

            var explicitAuto = TranslateClient.ResolveDirection("Hello", "auto");
            Check(explicitAuto.Auto && explicitAuto.Target == "简体中文", "配置写 auto 也按自动处理");

            var shortAuto = TranslateClient.ResolveDirection("Hello", "自动");
            Check(shortAuto.Auto, "配置只写「自动」也按自动处理");

            var empty = TranslateClient.ResolveDirection("Hello", "  ");
            Check(empty.Auto, "空目标语言回退自动");

            Check(TranslateClient.IsMostlyChinese("纯中文"), "纯中文判定为中文");
            Check(!TranslateClient.IsMostlyChinese("English only"), "纯英文判定为非中文");
            Check(TranslateClient.IsMostlyChinese("这是一段中文 abc"), "汉字多于字母时判定为中文");
        }

        // ===== 14. JSON 显式 null 的兜底 (设置文件被手工编辑/半写入) =====
        {
            // Normalize 是私有方法, 与 SettingsCheck 一样按真实加载路径反射调用
            var normalize = typeof(SettingsStore).GetMethod(
                "Normalize", BindingFlags.NonPublic | BindingFlags.Static)!;
            AppSettings Normalize(AppSettings s) => (AppSettings)normalize.Invoke(null, [s])!;

            const string nulls = """
                {
                  "translateApiKeyProtected": null,
                  "translateModel": null,
                  "translateTargetLanguage": null
                }
                """;
            var s = Normalize(JsonSerializer.Deserialize(nulls, SettingsJsonContext.Default.AppSettings)!);

            Check(s.TranslateApiKeyProtected == "", "null Key 密文归一化为空串 (避免设置对话框空引用)");
            Check(s.TranslateModel == AppSettings.DefaultTranslateModel, "null 模型名回退默认值");
            Check(s.TranslateTargetLanguage == AppSettings.AutoTranslateTarget, "null 目标语言回退自动");
        }

        return runner.ToResult();
    }
}
