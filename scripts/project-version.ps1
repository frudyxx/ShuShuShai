$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'src/ShuShuShai.App/ShuShuShai.App.csproj'
$version = ([xml](Get-Content -LiteralPath $project -Raw -Encoding UTF8)).Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid application version in project file' }
[string]$version
