param([switch]$Download)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$modelRoot = Join-Path $projectRoot 'models'
$manifest = Get-Content -LiteralPath (Join-Path $modelRoot 'manifest.json') -Raw | ConvertFrom-Json
foreach ($entry in $manifest) {
    $path = Join-Path $modelRoot $entry.file
    if (-not (Test-Path -LiteralPath $path)) {
        if (-not $Download) { throw "Missing OCR model: $path. Run scripts/setup-ocr-models.ps1 -Download." }
        $tempRoot = Join-Path $projectRoot '.tmp/ocr-model-download'
        New-Item -ItemType Directory -Force $tempRoot | Out-Null
        $temp = Join-Path $tempRoot ([Guid]::NewGuid().ToString('N') + '.download')
        try {
            Invoke-WebRequest -Uri $entry.url -OutFile $temp -UseBasicParsing
            if ((Get-FileHash -LiteralPath $temp -Algorithm SHA256).Hash -ne $entry.sha256) { throw "Downloaded model hash mismatch: $($entry.file)" }
            New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
            Move-Item -LiteralPath $temp -Destination $path
        }
        finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp } }
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $entry.sha256) { throw "OCR model hash mismatch: $path" }
}
Write-Output 'OCR model hashes verified (4 files).'
