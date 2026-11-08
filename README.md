# 舒舒筛（ShuShuShai）

中文离线 Windows PC 选片工具。C# / WPF / .NET 10，领域逻辑与 UI 分离；所有照片内容保持原样。实际验证边界见 [开发验证记录.md](开发验证记录.md)。使用方式见 [使用说明.md](使用说明.md)。

当前交付为 **v0.3.5-preview**，提供精简选片界面、历史管理、当前用户安装和软件内卸载。更新链路使用隔离回环版验收。本地预览包用新版安装程序升级。配置公开发布仓库和代码签名的方法见 [版本发布说明.md](版本发布说明.md)。

## 最快运行

用户可运行 `releases/ShuShuShai-Setup-0.3.5-win-x64.exe` 安装，或解压便携 ZIP 后双击 `舒舒筛.exe`。源码目录可双击 `启动开发版.cmd`，首次会在当前用户的 `%LOCALAPPDATA%\Programs\dotnet10` 准备微软官方 SDK。

VS Code 打开本项目，菜单“终端 → 运行任务”可运行、测试、构建或发布。也可在项目终端执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 restore
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 build
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 run
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 test
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\dev.ps1 publish
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-inno.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-inno.ps1 -InstallVerified
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\package-installer.ps1
```

安装器编译使用 Inno Setup 6.7.4。准备脚本下载固定版本并检查 SHA-256，安装编译器前还检查发布者签名。安装器复用便携发布目录，输出更新清单和校验值。通过 `-TestVersion` 另行构建验收包，产物仅写入 `.work`，使用独立的安装标识。正式联网发布需要另行配置 HTTPS 与受信发布者签名。

`test` 是能以非零退出码报告失败的集成规格程序，不是空的测试占位。全部扫描、移动与故障注入只针对自动生成的临时文件。UI 测试需要可用的 Windows 桌面。为了排障测试程序暂时保留自己的临时目录，路径会在结束时打印；其中都是生成的素材，可按该明确目录清理。

SDK 准备脚本读取 global.json 固定版本，从微软发布元数据取下载地址，核对 SHA-512 后解压。直接使用已配置的 .NET 10 SDK 时，对应命令为 `dotnet restore --locked-mode`、`dotnet build -c Release`、`dotnet run --project src/ShuShuShai.App/ShuShuShai.App.csproj -c Release`、`dotnet run --project tests/ShuShuShai.Specs -c Release`。发布为 `dotnet publish src/ShuShuShai.App/ShuShuShai.App.csproj -c Release -r win-x64 --self-contained true`；推荐使用脚本，它会补齐使用说明、验证记录和许可并打 ZIP。v0.3.5 另存为 `releases/舒舒筛-v0.3.5-preview-win-x64` 及同名 ZIP，保留旧包。

## 工程结构

- `src/ShuShuShai.Core`：文件白名单、扫描/配对、筛选与评分历史、元数据、SQLite、命名计划、安全导出与恢复。
- `src/ShuShuShai.App`：MVVM 状态、WPF 深色资源、按行虚拟化总览、横向虚拟化胶片条、三级 JPG 预览与直方图、导入/配对/导出窗口。
- `tests/ShuShuShai.Specs`：临时目录集成检查、故障注入、真实 WPF 虚拟化检查、可生成已知像素测试 JPG。
- `scripts`：还原、构建、运行、测试、便携发布。
- `licenses` / `THIRD-PARTY-NOTICES.md`：第三方许可。

文件导出先生成不变的计划，再将文件状态写入 SQLite；每个临时文件路径、长度、源身份、SHA-256、提交和源文件移除状态均记录。Windows 移除源文件通过同一个已重新核验的文件句柄操作，避免核验后按路径删除到另一个文件。组内任何复制失败都会保留该组源文件。普通评分撤销与文件导出记录分开。

发布产物、数据库、缓存、原片均由 .gitignore 排除；源码以 MIT 许可发布，第三方组件保留各自许可。

便携包复核可运行 `powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-portable.ps1 -Zip releases/舒舒筛-v0.3.5-preview-win-x64.zip`。脚本独立解压、用临时数据启动并核验包内运行时加载，之后结束它自己启动的测试进程。检查旧包时可指定对应的 `-ExpectedVersion`。生成素材的性能模式为 `--performance`、`--zoom-performance`；`--real-jpg <目录>` 仅在明确指定时只读核验本机样本，输出不随发布分发。
