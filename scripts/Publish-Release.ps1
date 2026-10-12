param([string]$Version)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\AccountManager.App\AccountManager.App.csproj'
if (!$Version) { $Version = ([xml](Get-Content $project -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1 }
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must be major.minor.patch.' }
$output = Join-Path $root ('release\v' + $Version)
if (Test-Path $output) { throw 'Release directory already exists. Use a new version or review it before removing it.' }
$build = Join-Path $root ('.release-staging\publish-' + [Guid]::NewGuid().ToString('N'))
& dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false "-p:Version=$Version" -o $build
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
$asset = Join-Path $output 'OpenCodeAccountManager-win-x64.exe'
Copy-Item (Join-Path $build 'OpenCodeAccountManager.exe') $asset
$hash = (Get-FileHash $asset -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($asset + '.sha256'), ($hash + '  OpenCodeAccountManager-win-x64.exe' + "`n"), [Text.Encoding]::ASCII)
Get-ChildItem $output | Select-Object Name, Length
Write-Output ('ReleaseDirectory=' + $output)
