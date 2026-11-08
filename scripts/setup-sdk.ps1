$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$sdkVersion = (Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$sdkDirectory = Join-Path $env:LOCALAPPDATA 'Programs\dotnet10'
$sdkExe = Join-Path $sdkDirectory 'dotnet.exe'
if ((Test-Path -LiteralPath $sdkExe) -and (& $sdkExe --list-sdks | Select-String -SimpleMatch $sdkVersion)) { Write-Host "SDK $sdkVersion 已准备好。"; return }
$metadata = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
$entry = $metadata.releases | ForEach-Object { $_.sdks } | Where-Object { $_.version -eq $sdkVersion } | Select-Object -First 1
if (-not $entry) { throw "微软官方元数据中未找到 SDK $sdkVersion" }
$download = $entry.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq 'dotnet-sdk-win-x64.zip' } | Select-Object -First 1
if (-not $download.url.StartsWith('https://builds.dotnet.microsoft.com/')) { throw '非预期的 SDK 下载地址' }
$sdkTemp = Join-Path ([System.IO.Path]::GetTempPath()) ('ShuShuShai-SDK-' + [guid]::NewGuid().ToString('N') + '.zip')
Write-Host "正在准备微软 .NET SDK $sdkVersion（当前用户，无需管理员）…"
Invoke-WebRequest -Uri $download.url -OutFile $sdkTemp
if ((Get-FileHash -LiteralPath $sdkTemp -Algorithm SHA512).Hash -ne $download.hash) { throw 'SDK SHA512 校验失败，未解压。' }
New-Item -ItemType Directory -Force -Path $sdkDirectory | Out-Null
Expand-Archive -LiteralPath $sdkTemp -DestinationPath $sdkDirectory -Force
Remove-Item -LiteralPath $sdkTemp
& $sdkExe --version
if ($LASTEXITCODE -ne 0) { throw 'SDK 验证失败' }
