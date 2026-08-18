$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRawUrl = 'https://raw.githubusercontent.com/int04/citus-manager/master'
$installDir = if ($env:CITUS_MANAGER_INSTALL_DIR) {
    $env:CITUS_MANAGER_INSTALL_DIR
} else {
    Join-Path $HOME 'citus-manager'
}
$composeFile = Join-Path $installDir 'compose.yaml'
$envFile = Join-Path $installDir '.env'
$temporaryCompose = "$composeFile.tmp"

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw 'Docker is required: https://docs.docker.com/get-docker/'
}

& docker compose version *> $null
if ($LASTEXITCODE -ne 0) {
    throw 'Docker Compose v2 is required.'
}

New-Item -ItemType Directory -Path $installDir -Force | Out-Null

try {
    Invoke-WebRequest -UseBasicParsing -Uri "$repositoryRawUrl/compose.yaml" -OutFile $temporaryCompose
    Move-Item -LiteralPath $temporaryCompose -Destination $composeFile -Force
} finally {
    Remove-Item -LiteralPath $temporaryCompose -Force -ErrorAction SilentlyContinue
}

$environmentLines = if (Test-Path -LiteralPath $envFile) {
    @(Get-Content -LiteralPath $envFile)
} else {
    @()
}

if ($environmentLines -match '^CITUS_MANAGER_DB_PASSWORD=$') {
    throw "CITUS_MANAGER_DB_PASSWORD is empty in $envFile. Set it, then run this installer again."
}

if (-not ($environmentLines -match '^CITUS_MANAGER_DB_PASSWORD=.')) {
    $bytes = [byte[]]::new(32)
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    try {
        $random.GetBytes($bytes)
    } finally {
        $random.Dispose()
    }
    $password = ($bytes | ForEach-Object { $_.ToString('x2') }) -join ''
    Add-Content -LiteralPath $envFile -Value "CITUS_MANAGER_DB_PASSWORD=$password" -Encoding ascii
}

& docker compose --project-directory $installDir --file $composeFile up -d
if ($LASTEXITCODE -ne 0) {
    throw "Docker Compose failed with exit code $LASTEXITCODE."
}

& docker compose --project-directory $installDir --file $composeFile ps

Write-Host "`nCitus Manager installed in $installDir"
Write-Host 'Open http://localhost:2706/Account/Setup'
Write-Host "Manage it later with: Set-Location '$installDir'; docker compose <command>"
