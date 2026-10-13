param([switch]$Publish, [string]$PublishDirectory = 'dist/SnapTool')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $projectRoot '.tools\dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet)) { $dotnet = (Get-Command dotnet -ErrorAction Stop).Source }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_HOME = Join-Path $projectRoot '.tools\cli-home'
$env:NUGET_PACKAGES = Join-Path $projectRoot '.tools\nuget'
Push-Location $projectRoot
try {
    & (Join-Path $PSScriptRoot 'setup-ocr-models.ps1')
    & $dotnet restore tests/SnapTool.Tests/SnapTool.Tests.csproj --locked-mode --configfile NuGet.Config --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
    & $dotnet build tests/SnapTool.Tests/SnapTool.Tests.csproj -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    & $dotnet run --project tests/SnapTool.Tests/SnapTool.Tests.csproj -c Release --no-build
    if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed.' }
    if ($Publish) {
        & $dotnet restore src/SnapTool/SnapTool.csproj -r win-x64 -p:SelfContained=true --locked-mode --configfile NuGet.Config --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Runtime restore failed.' }
        & $dotnet publish src/SnapTool/SnapTool.csproj -c Release -r win-x64 --self-contained true --no-restore -o $PublishDirectory --nologo
        if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    }
}
finally { Pop-Location }
