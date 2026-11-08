param([Parameter(Mandatory=$true)][string]$Zip,[string]$ExpectedVersion)
$ErrorActionPreference='Stop'
if (-not $ExpectedVersion) { $ExpectedVersion = & (Join-Path $PSScriptRoot 'project-version.ps1') }
$archive=(Resolve-Path -LiteralPath $Zip).Path
$reviewRoot=Join-Path ([IO.Path]::GetTempPath()) ('ShuShuShai.Portable-'+[Guid]::NewGuid().ToString('N'))
Expand-Archive -LiteralPath $archive -DestinationPath $reviewRoot
$executables=@(Get-ChildItem -LiteralPath $reviewRoot -Filter '舒舒筛.exe' -File -Recurse)
if ($executables.Count -ne 1) { throw '便携包必须包含唯一应用入口' }
$exe=$executables[0].FullName
$packageRoot=Split-Path $exe -Parent
$unexpected=@(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object { $_.Extension -match '^\.(pdb|png|jpg|jpeg|arw|cr3|cr2|nef|raf|dng|db|sqlite)$' })
if ($unexpected.Count) { throw ('发布包含非预期文件：'+($unexpected.FullName -join ', ')) }
$start=New-Object Diagnostics.ProcessStartInfo
$start.FileName=$exe
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$start.EnvironmentVariables['PATH']=$env:SystemRoot+';'+$env:SystemRoot+'\System32'
$start.EnvironmentVariables['DOTNET_ROOT']=Join-Path $reviewRoot 'runtime-not-installed'
$start.EnvironmentVariables['DOTNET_ROOT_X64']=$start.EnvironmentVariables['DOTNET_ROOT']
$start.EnvironmentVariables['DOTNET_MULTILEVEL_LOOKUP']='0'
$start.EnvironmentVariables['SHUSHUSHAI_DATA_DIR']=Join-Path $reviewRoot 'test-data'
$qaProcess=[Diagnostics.Process]::Start($start)
try {
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    do {
        Start-Sleep -Milliseconds 150
        $qaProcess.Refresh()
        if ($qaProcess.HasExited) { throw '独立解压的便携程序提前退出' }
    } while (($qaProcess.MainWindowHandle -eq 0 -or $qaProcess.MainWindowTitle -notlike ('*v'+$ExpectedVersion+'*')) -and [DateTime]::UtcNow -lt $deadline)
    if ($qaProcess.MainWindowHandle -eq 0 -or $qaProcess.MainWindowTitle -notlike ('*v'+$ExpectedVersion+'*')) { throw ('未观察到 v'+$ExpectedVersion+' 主窗口') }
    Start-Sleep -Milliseconds 500
    $qaProcess.Refresh()
    $modules=@($qaProcess.Modules)
    foreach ($name in @('hostfxr.dll','coreclr.dll','wpfgfx_cor3.dll','PresentationNative_cor3.dll','e_sqlite3.dll')) {
        $module=@($modules | Where-Object ModuleName -eq $name)
        if ($module.Count -ne 1 -or -not $module[0].FileName.StartsWith($packageRoot+'\',[StringComparison]::OrdinalIgnoreCase)) { throw ('运行模块未从便携包加载：'+$name) }
        Write-Output ('PASS packaged module '+$name)
    }
    $dll=Join-Path $packageRoot '舒舒筛.dll'
    Write-Output ('PASS isolated startup '+$qaProcess.MainWindowTitle)
    Write-Output ('VERSION '+[Diagnostics.FileVersionInfo]::GetVersionInfo($exe).FileVersion)
    Write-Output ('DLL_SHA256 '+(Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash)
    Write-Output ('ZIP_SHA256 '+(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash)
    Write-Output ('REVIEW_ROOT '+$reviewRoot)
    Write-Output 'NOTE 此检查核验启动和包内模块加载；独立干净 Windows 与正常退出分别验收。'
} finally {
    # Only the process this check launched, using its own temporary database.
    if (-not $qaProcess.HasExited) { Stop-Process -Id $qaProcess.Id }
    $qaProcess.Dispose()
}
