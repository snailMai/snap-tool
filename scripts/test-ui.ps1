param([switch]$Secondary)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $projectRoot 'tests\SnapTool.Tests\bin\Release\net10.0-windows\SnapTool.Tests.exe'
if (-not (Test-Path -LiteralPath $fixture)) { throw 'Run scripts/build.ps1 first.' }
$localRuntime = Join-Path $projectRoot '.tools\dotnet'
if (Test-Path -LiteralPath (Join-Path $localRuntime 'dotnet.exe')) { $env:DOTNET_ROOT = $localRuntime }
$fixtureArgs = @('--fixture')
if ($Secondary) { $fixtureArgs += '--secondary' }
& $fixture @fixtureArgs
