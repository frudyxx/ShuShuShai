param([switch]$InstallVerified)
$ErrorActionPreference = 'Stop'
$work = Join-Path (Split-Path $PSScriptRoot -Parent) '.work/inno-6.7.4'
$binary = Join-Path $work 'innosetup-6.7.4.exe'
$messages = Join-Path $work 'ChineseSimplified.isl'
$license = Join-Path $work 'license.txt'
$binaryHash = 'A549929319F9120716677C685535CA4671E4B937B5FDC87E2585F32D8EB30E8C'
$messagesHash = '7D544B9BB1D142CFA11F2E5D3CC8ABE2E55F8E066C5124E3772675AA236E1278'
$repo = 'https://raw.githubusercontent.com/jrsoftware/issrc/is-6_7_4'
function Get-PinnedFile([string]$Url,[string]$Path,[string]$Hash) {
    if ((Test-Path -LiteralPath $Path) -and (Get-FileHash -LiteralPath $Path).Hash -eq $Hash) { return }
    $partial = $Path + '.partial'
    & curl.exe --fail --location --silent --show-error --retry 2 --connect-timeout 25 --max-time 240 --output $partial $Url
    if ($LASTEXITCODE -ne 0 -or (Get-FileHash -LiteralPath $partial).Hash -ne $Hash) { throw "Official file hash mismatch: $Path" }
    Move-Item -LiteralPath $partial -Destination $Path -Force
}

if (-not $InstallVerified) {
    New-Item -ItemType Directory -Path $work -Force | Out-Null
    Get-PinnedFile 'https://github.com/jrsoftware/issrc/releases/download/is-6_7_4/innosetup-6.7.4.exe' $binary $binaryHash
    Get-PinnedFile "$repo/Files/Languages/Unofficial/ChineseSimplified.isl" $messages $messagesHash
    Write-Output 'Inno Setup and Chinese messages downloaded and checked. Run again with -InstallVerified.'
    return
}

if ((Get-FileHash -LiteralPath $binary).Hash -ne $binaryHash -or (Get-FileHash -LiteralPath $messages).Hash -ne $messagesHash) {
    throw 'Re-download the official packages and check their hashes.'
}
$signature = Get-AuthenticodeSignature -LiteralPath $binary
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'CN=Pyrsys B\.V\.') {
    throw "Inno Setup publisher verification failed: $($signature.Status)"
}
$target = Join-Path $work 'compiler'
$process = Start-Process -FilePath $binary -ArgumentList @(
    '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/NOICONS', ('/DIR="' + $target + '"')
) -WindowStyle Hidden -PassThru -Wait
if ($process.ExitCode -ne 0) { throw "Inno Setup exited with code $($process.ExitCode)" }
Copy-Item -LiteralPath (Join-Path $target 'license.txt') -Destination $license -Force
Write-Output (Join-Path $target 'ISCC.exe')
