param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$publish = Join-Path $root 'artifacts\publish\win-x64'
$portable = Join-Path $root 'artifacts\OpenVocare-portable-win-x64.zip'
$packageStaging = Join-Path $root 'artifacts\package-staging'
$publishedExecutable = Join-Path $publish 'OpenVocare.exe'

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
$bundledDotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
$dotnet = if (Test-Path -LiteralPath $bundledDotnet) { $bundledDotnet } elseif ($dotnetCommand) { $dotnetCommand.Source } else { $null }
if (-not $dotnet) { throw 'The .NET 10 SDK was not found globally or under .tools\dotnet.' }

& $dotnet restore "$root\OpenVocare.sln" --runtime win-x64 --configfile "$root\NuGet.Config"
if ($LASTEXITCODE -ne 0) { throw 'Package restore failed.' }
if (-not $SkipTests) {
    & $dotnet test "$root\OpenVocare.sln" -c $Configuration --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
if (Test-Path $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
& $dotnet publish "$root\src\OpenVocare\OpenVocare.csproj" -c $Configuration -r win-x64 --self-contained true --no-restore `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o $publish
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }

New-Item -ItemType Directory -Force (Split-Path -Parent $portable) | Out-Null
if (Test-Path $portable) { Remove-Item $portable }
if (Test-Path $packageStaging) {
    Remove-Item -LiteralPath $packageStaging -Recurse -Force
}
try {
    $packageAssetDirectory = Join-Path $packageStaging 'src\OpenVocare\Assets'
    New-Item -ItemType Directory -Force $packageAssetDirectory | Out-Null
    Copy-Item -LiteralPath $publishedExecutable -Destination $packageStaging
    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination $packageStaging
    Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $packageStaging
    $license = Join-Path $root 'LICENSE'
    if (Test-Path -LiteralPath $license) {
        Copy-Item -LiteralPath $license -Destination $packageStaging
    }
    Copy-Item -LiteralPath (Join-Path $root 'src\OpenVocare\Assets\OpenVocare.svg') `
        -Destination $packageAssetDirectory
    $packageBenchmarkDirectory = Join-Path $packageStaging 'docs\benchmarks'
    New-Item -ItemType Directory -Force $packageBenchmarkDirectory | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'docs\benchmarks\2026-07-27-paired-latency.md') `
        -Destination $packageBenchmarkDirectory
    Compress-Archive -Path (Join-Path $packageStaging '*') -DestinationPath $portable
}
finally {
    if (Test-Path $packageStaging) {
        Remove-Item -LiteralPath $packageStaging -Recurse -Force
    }
}
