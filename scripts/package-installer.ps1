param(
    [string]$Compiler,
    [string]$OutputDirectory,
    [string]$PayloadDirectory,
    [string]$TestVersion,
    [string]$Notes = '精简选片工作区，增加历史记录管理、桌面快捷方式和软件内卸载。'
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$work = Join-Path $projectRoot '.work'
$tool = Join-Path $work 'inno-6.7.4'
if (-not $Compiler) { $Compiler = Join-Path $tool 'compiler/ISCC.exe' }
$Compiler = (Resolve-Path -LiteralPath $Compiler).Path
$messages = (Resolve-Path -LiteralPath (Join-Path $tool 'ChineseSimplified.isl')).Path
$innoLicense = (Resolve-Path -LiteralPath (Join-Path $tool 'license.txt')).Path
$version = & (Join-Path $PSScriptRoot 'project-version.ps1')
$appProject = Join-Path $projectRoot 'src/ShuShuShai.App/ShuShuShai.App.csproj'
if ($TestVersion) {
    if ($PayloadDirectory) { throw '回环验收包使用独立构建载荷。' }
    if ($TestVersion -notmatch '^\d+\.\d+\.\d+$' -or ([version]$TestVersion).ToString(3) -ne $TestVersion) {
        throw '测试版本必须是三个数字段。'
    }
    $version = $TestVersion
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $work "updater-qa/$version" }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    if (-not $OutputDirectory.StartsWith($work + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw '回环验收包只能输出到项目的 .work 目录。'
    }
    $payload = Join-Path $OutputDirectory 'payload'
} else {
    if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'releases' }
    $OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
    $payload = if ($PayloadDirectory) { (Resolve-Path -LiteralPath $PayloadDirectory).Path } else { Join-Path $projectRoot "releases/舒舒筛-v$version-preview-win-x64" }
}

$fileName = "ShuShuShai-Setup-$version-win-x64.exe"
$manifestName = "update-v$version.json"
if ($TestVersion) { $manifestName = 'update.json' }
$installer = Join-Path $OutputDirectory $fileName
$manifestPath = Join-Path $OutputDirectory $manifestName
foreach ($path in $installer, $manifestPath) {
    if (Test-Path -LiteralPath $path) { throw "目标已存在，请使用新的输出目录。$path" }
}

if ($TestVersion) {
    if (Test-Path -LiteralPath $payload) { throw "测试载荷目录已存在。$payload" }
    $sdk = Join-Path $env:LOCALAPPDATA 'Programs/dotnet10/dotnet.exe'
    $env:DOTNET_ROOT = Split-Path $sdk -Parent
    & $sdk publish $appProject -c Release -r win-x64 --self-contained true --artifacts-path (Join-Path $OutputDirectory 'build') -o $payload "-p:Version=$version" -p:UpdateTestBuild=true -p:RestoreLockedMode=true -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw "测试版发布失败，退出码 $LASTEXITCODE" }
}

$exe = Join-Path $payload '舒舒筛.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw '请先运行 scripts/dev.ps1 publish，生成自包含便携目录。' }
if ([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion -ne "$version.0") { throw '载荷与安装器的版本不一致。' }
foreach ($module in 'hostfxr.dll', 'coreclr.dll', 'wpfgfx_cor3.dll', 'e_sqlite3.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $payload $module))) { throw "载荷缺少自包含模块 $module" }
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$arguments = @("/DAppVersion=$version", "/DPayload=$payload", "/DDestination=$OutputDirectory", "/DChineseMessages=$messages", "/DInnoLicense=$innoLicense")
if ($TestVersion) { $arguments += '/DUpdateTest=1' }
$compilerOutput = & $Compiler @arguments (Join-Path $projectRoot 'installer/ShuShuShai.iss')
$compilerOutput | Write-Output
if ($LASTEXITCODE -ne 0) { throw "安装器编译失败，退出码 $LASTEXITCODE" }
if (-not ($compilerOutput -match 'Compiler engine version: Inno Setup 6\.7\.4\b')) { throw '此发布脚本使用 Inno Setup 6.7.4。' }
& (Join-Path $PSScriptRoot 'write-update-manifest.ps1') -Installer $installer -Manifest $manifestPath -Notes $Notes
Write-Output "INSTALLER $installer"
Write-Output "MANIFEST $manifestPath"
