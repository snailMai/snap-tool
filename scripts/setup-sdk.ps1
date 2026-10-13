$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$projectRoot = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $projectRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$destination = Join-Path $projectRoot '.tools\dotnet'
if (Test-Path -LiteralPath (Join-Path $destination 'dotnet.exe')) {
    & (Join-Path $destination 'dotnet.exe') --version
    if ($LASTEXITCODE -ne 0) { throw 'Existing local SDK is not usable.' }
    return
}
$temp = Join-Path $projectRoot '.tmp\sdk-setup'
New-Item -ItemType Directory -Path $temp -Force | Out-Null
$metadata = Invoke-RestMethod 'https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/10.0/releases.json'
$sdk = $metadata.releases | ForEach-Object { $_.sdks } | Where-Object { $_.version -eq $version } | Select-Object -First 1
if ($null -eq $sdk) { throw "SDK $version is not in Microsoft's release metadata." }
$file = $sdk.files | Where-Object { $_.rid -eq 'win-x64' -and $_.name -eq 'dotnet-sdk-win-x64.zip' }
$zip = Join-Path $temp 'sdk.zip'
Invoke-WebRequest -Uri $file.url -OutFile $zip
if ((Get-FileHash -LiteralPath $zip -Algorithm SHA512).Hash -ne $file.hash) { throw 'SDK SHA512 mismatch.' }
Expand-Archive -LiteralPath $zip -DestinationPath $destination
& (Join-Path $destination 'dotnet.exe') --version
if ($LASTEXITCODE -ne 0) { throw 'Local SDK installation failed.' }
