# 第三方组件说明

应用离线运行。下列组件仅用于开发与本地运行，不会把照片上传到这些项目。

| 组件 | 固定版本 | 用途 | 许可及官方来源 |
|---|---|---|---|
| .NET / WPF | Runtime 10.0.14；SDK 10.0.403 | Windows 桌面运行时 | MIT 及其第三方声明，发布包 licenses/dotnet-*；https://github.com/dotnet/runtime |
| Microsoft.Data.Sqlite / Core | 10.0.14 | 本机 SQLite 任务记录 | MIT，licenses/Microsoft.Data.Sqlite.txt；https://github.com/dotnet/efcore |
| SQLitePCLRaw（core、bundle、provider、lib.e_sqlite3） | 2.1.13 | SQLite 原生运行库 | Apache-2.0，licenses/SQLitePCLRaw.txt；https://github.com/ericsink/SQLitePCL.raw |
| SQLite | 随 SQLitePCLRaw.lib.e_sqlite3 2.1.13 固定分发 | 数据库引擎 | Public domain；https://sqlite.org/copyright.html |
| MetadataExtractor | 2.9.3 | JPG 只读元数据 | Apache-2.0，licenses/MetadataExtractor.txt；https://github.com/drewnoakes/metadata-extractor-dotnet |
| XmpCore | 6.1.10.1（MetadataExtractor 间接依赖） | 读取已有 XMP 字段 | Adobe BSD，licenses/XmpCore-*；https://github.com/drewnoakes/xmp-core-dotnet |

版本与内容哈希在各项目 packages.lock.json 中锁定。2026-11-05 核对了微软官方支持策略、NuGet 官方包信息及上述上游许可证。XmpCore 上游 README 明确采用其来源 Adobe XMP SDK 的 BSD 许可；随包保留上游说明、版权头与 Adobe BSD 文本。应用不写 XMP、不改写照片文件。
# 安装程序

舒舒筛 Windows 安装程序由 [Inno Setup 6.7.4](https://jrsoftware.org/isdl.php) 生成。Inno Setup 版权归 Jordan Russell 和 Martijn Laan，许可随安装版放入 `licenses/Inno-Setup.txt`。简体中文消息采用 [Inno Setup 6.7.4 源码仓库中的社区译本](https://github.com/jrsoftware/issrc/blob/is-6_7_4/Files/Languages/Unofficial/ChineseSimplified.isl)，维护者为 Zhenghan Yang，向导内保留译者署名。
