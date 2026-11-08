param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][string]$Manifest,
    [Parameter(Mandatory)][string]$Notes,
    [string]$SigningCertificateSha256
)
$ErrorActionPreference = 'Stop'
$package = Get-Item -LiteralPath $Installer
if ($package.Name -cnotmatch '\AShuShuShai-Setup-(\d+\.\d+\.\d+)-win-x64\.exe\z') { throw '安装包文件名不符合发布格式。' }
$version = $Matches[1]
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($package.FullName)
$numericVersion = [version]::new($info.FileMajorPart, $info.FileMinorPart, $info.FileBuildPart, $info.FilePrivatePart)
if ($numericVersion -ne [version]"$version.0" -or $info.ProductVersion.Trim() -ne $version) { throw '安装器版本与文件名不一致。' }
if ($Notes.Trim().Length -eq 0 -or $Notes.Length -gt 8192) { throw '请填写不超过 8192 个字符的更新说明。' }
if ($SigningCertificateSha256) {
    if ($SigningCertificateSha256 -notmatch '\A[0-9A-Fa-f]{64}\z') { throw '发布者证书指纹无效。' }
    $signature = Get-AuthenticodeSignature -LiteralPath $package.FullName
    if ($signature.Status -ne 'Valid') { throw '安装包签名无效。' }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $publisher = [BitConverter]::ToString($sha.ComputeHash($signature.SignerCertificate.RawData)).Replace('-', '') }
    finally { $sha.Dispose() }
    if ($publisher -ne $SigningCertificateSha256) {
        throw '安装包签名无效或与指定发布者不符。'
    }
}
$hash = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
$data = [ordered]@{
    version = $version
    notes = $Notes
    fileName = $package.Name
    size = $package.Length
    sha256 = $hash
}
$utf8 = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText([IO.Path]::GetFullPath($Manifest), ($data | ConvertTo-Json), $utf8)
[IO.File]::WriteAllText($package.FullName + '.sha256.txt', "$hash  $($package.Name)" + [Environment]::NewLine, $utf8)
