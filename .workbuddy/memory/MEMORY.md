# SnipasteOCR 项目长期备忘

## 构建环境 (2026-11-27 实测)

- 本机 dotnet SDK: 8.0.101 / 9.0.310 / 10.0.100-rc.1 / 11.0.100-rc.3 并存;
  仓库根 `global.json` 钉 **11.0.100-rc.3** (rollForward latestFeature)。
- 10.0.100-rc.1 安装不完整 (缺 `Microsoft.Extensions.Logging.*` 系列 dll),
  钉到它时任何 dotnet 命令崩 TypeInitializationException —— **不可用**。
- SDK 11 RC 下 AOT publish 必须配 csproj 的 `PublishDocumentationFile=false`,
  否则必现 MSB3030「无法复制 SnipasteOcr.xml」: AOT 构建不在 bin 产出文档 xml,
  而发布清单 (`@(FinalDocFile)`, Microsoft.NET.Publish.targets L656) 仍引用它。
  文档 xml 仅用于构建期 CS1574/CS1587 检查, 无需进发布产物。
- 待办: 安装 .NET 10 GA SDK 后, 把 global.json 版本改回 10.0.1xx 并复跑发布验证。
