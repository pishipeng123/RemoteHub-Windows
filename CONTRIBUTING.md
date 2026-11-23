# 参与贡献

## 环境要求

编译**不需要** Visual Studio、.NET SDK 或 NuGet——Windows 自带的 .NET Framework 编译器就够：

```bat
build.bat anycpu
```

如果想用 IDE 调试，用 Visual Studio 2019/2022 打开 `RemoteHub.csproj`（目标框架 .NET Framework 4.8）。
装了 Visual Studio 2022 的话，记得在安装器里勾上「.NET 桌面开发」工作负载。

## 提交前请确认

```bat
build.bat anycpu          :: 必须零错误、零警告地通过
RemoteHub.exe --selftest  :: 生成 selftest.log，确认各项功能自检通过
RemoteHub.exe --smoke     :: 生成 smoke.log，确认连接链路自检通过
```

## 代码约定

- **语言**：C# 5（.NET Framework 4.x 编译器只支持到 C# 5，不要引入 C# 6+ 语法，否则 `build.bat` 会失败）。
- **编码**：所有源文件 UTF-8。`build.bat` 已带 `/codepage:65001`。
- **换行符**：`.cs` / `.bat` / `.ps1` / `.iss` 用 CRLF（仓库已配置 `.gitattributes`，不要手动改动）。
- **缩进**：4 空格，不用 Tab。
- **注释**：写「为什么这么做」，不要复述代码在做什么。中文注释请与周围代码保持一致。
- **提交粒度**：一个提交只做一件事，提交信息用中文祈使句（`修复全屏时的分辨率同步`）。

## 不要提交的东西

- 编译产物（`RemoteHub.exe`）、打包产物（`dist/`）、本地截图（`preview/`）——都已在 `.gitignore` 里。
- **任何真实的台账数据**：`connections.xml`、`history.xml`、`vault.ini`、`Data/` 目录。这些包含主机地址与 DPAPI 密文。
- README 用图请使用**示例数据**生成。可行的做法：建一个临时目录，放一份 `portable.flag` 和一份脱敏的 `Data\connections.xml`，再运行 `RemoteHub.exe --shot=...`（便携模式下配置写在程序旁边，不会碰到 `%APPDATA%` 里的真实台账）。

## 提交 Pull Request

1. 从 `main` 切出分支：`git checkout -b feature/xxx`。
2. 提交改动并推送到你自己的 fork：`git push -u origin feature/xxx`。
3. 在 GitHub 上开 PR，描述清楚改了什么、为什么。
4. 等 CI 通过（Windows 环境跑编译 + 自检）。

## 报告 Bug

开 Issue 时请附上：

- RemoteHub 版本（帮助 → 关于，或 `RemoteHub.exe --selftest` 输出）；
- Windows 版本与位数；
- `selftest.log` 的内容（**注意先检查里面有没有你的主机名和 IP，有的话请先删掉**）。
