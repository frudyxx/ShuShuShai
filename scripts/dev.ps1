param(
    [ValidateSet('restore','build','run','test','publish')][string]$Action = 'run',
    [string]$ReleaseRepository,
    [string]$SigningCertificateSha256
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Set-Location -LiteralPath $projectRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$sdkExe = Join-Path $env:LOCALAPPDATA 'Programs\dotnet10\dotnet.exe'
if (-not (Test-Path -LiteralPath $sdkExe)) { & (Join-Path $PSScriptRoot 'setup-sdk.ps1') }
$env:DOTNET_ROOT = Split-Path $sdkExe -Parent
function Invoke-Dotnet([string[]]$Arguments) {
    & $sdkExe @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet 执行失败，退出码 $LASTEXITCODE" }
}
switch ($Action) {
    'restore' { Invoke-Dotnet -Arguments @('restore','--locked-mode') }
    'build' { Invoke-Dotnet -Arguments @('build','-c','Release') }
    'run' { Invoke-Dotnet -Arguments @('run','--project','src/ShuShuShai.App/ShuShuShai.App.csproj','-c','Release') }
    'test' { Invoke-Dotnet -Arguments @('run','--project','tests/ShuShuShai.Specs','-c','Release') }
    'publish' {
        $releaseArgs = @()
        if ($ReleaseRepository) {
            if ($ReleaseRepository -notmatch '\A[A-Za-z0-9-]+/(?!\.{1,2}\z)[A-Za-z0-9_.-]+\z') { throw '请填写 GitHub 的账号名/仓库名。' }
            $releaseArgs += "-p:UpdateReleaseRepository=$ReleaseRepository"
        }
        if ($SigningCertificateSha256) {
            if (-not $ReleaseRepository -or $SigningCertificateSha256 -notmatch '\A[0-9A-Fa-f]{64}\z') {
                throw '启用软件内下载安装时须提供发布仓库和发布者证书的 SHA-256 指纹。'
            }
            $releaseArgs += "-p:UpdateSigningCertificateSha256=$SigningCertificateSha256"
        }
        Invoke-Dotnet -Arguments @('restore','--locked-mode')
        $version = & (Join-Path $PSScriptRoot 'project-version.ps1')
        $packageName = "舒舒筛-v$version-preview-win-x64"
        $releaseRoot = Join-Path $projectRoot 'releases'
        $destination = Join-Path $releaseRoot $packageName
        if (Test-Path -LiteralPath $destination) { $packageName += '_' + (Get-Date -Format 'yyyyMMdd_HHmmss'); $destination = Join-Path $releaseRoot $packageName }
        Invoke-Dotnet -Arguments (@('publish','src/ShuShuShai.App/ShuShuShai.App.csproj','-c','Release','-r','win-x64','--self-contained','true','-p:DebugType=None','-p:DebugSymbols=false','-o',$destination) + $releaseArgs)
        Copy-Item -LiteralPath (Join-Path $projectRoot 'licenses') -Destination $destination -Recurse
        Copy-Item -LiteralPath (Join-Path $projectRoot '使用说明.md'),(Join-Path $projectRoot '开发验证记录.md'),(Join-Path $projectRoot '版本发布说明.md'),(Join-Path $projectRoot 'THIRD-PARTY-NOTICES.md'),(Join-Path $projectRoot 'LICENSE') -Destination $destination
        $dotnetLicense = Join-Path $env:DOTNET_ROOT 'LICENSE.txt'
        $dotnetNotices = Join-Path $env:DOTNET_ROOT 'ThirdPartyNotices.txt'
        Copy-Item -LiteralPath $dotnetLicense -Destination (Join-Path $destination 'licenses\dotnet-LICENSE.txt')
        Copy-Item -LiteralPath $dotnetNotices -Destination (Join-Path $destination 'licenses\dotnet-ThirdPartyNotices.txt')
        $zipPath = Join-Path $releaseRoot ($packageName + '.zip')
        Compress-Archive -LiteralPath $destination -DestinationPath $zipPath
        $checksum = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
        $checksum | Format-List
        [IO.File]::WriteAllText($zipPath + '.sha256.txt', ($checksum.Hash + '  ' + [IO.Path]::GetFileName($zipPath) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
        Write-Host "便携目录：$destination"
    }
}
