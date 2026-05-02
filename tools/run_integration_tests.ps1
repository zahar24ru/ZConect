# Integration test runner: локально поднимает Go signaling server,
# прогоняет C# client LiveServerTests против него, останавливает сервер.
#
# Usage:
#   powershell -ExecutionPolicy Bypass -File tools/run_integration_tests.ps1
#   powershell -ExecutionPolicy Bypass -File tools/run_integration_tests.ps1 -Port 8099 -FilterClass LiveServerTests
#   powershell -ExecutionPolicy Bypass -File tools/run_integration_tests.ps1 -TestCategory Live -Verbose
#
# Prerequisites:
#   - Go 1.x установлен (проверяется в C:\Program Files\Go\bin\go.exe или PATH)
#   - .NET 8 SDK для dotnet test
#   - Порт 8099 свободен (или передайте -Port)
#
# Что делает:
#   1. Build'ит Go server из worktree'а
#   2. Генерирует bcrypt hash для test admin password
#   3. Стартует сервер в background на localhost:$Port с изолированными temp файлами
#   4. Ждёт healthz OK (до 15 сек)
#   5. Запускает `dotnet test` с переменными среды ZCONECT_TEST_SERVER/ZCONECT_TEST_WS
#   6. По завершении (успех или failure) — обязательно останавливает сервер
#   7. Чистит temp файлы

[CmdletBinding()]
param(
    [int]$Port = 8099,
    [string]$FilterClass = "LiveServerTests",
    [string]$TestCategory = "Live",
    [int]$HealthWaitSec = 15,
    [switch]$KeepServerRunning,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'

# Paths
$repoRoot = Split-Path -Parent $PSScriptRoot
$serverDir = Join-Path $repoRoot 'server'
$testsProj = Join-Path $repoRoot 'client\ZConect.Tests\ZConect.Tests.csproj'
$serverExe = Join-Path $serverDir 'zconect-signaling-test.exe'
$hashpassExe = Join-Path $serverDir 'admin-hashpass-test.exe'

# Locate Go
$goExe = 'go'
if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
    $candidates = @(
        'C:\Program Files\Go\bin\go.exe',
        "$env:USERPROFILE\go\bin\go.exe",
        'C:\Go\bin\go.exe'
    )
    foreach ($c in $candidates) {
        if (Test-Path $c) { $goExe = $c; break }
    }
    if ($goExe -eq 'go') {
        throw "Go не найден. Установи через 'choco install golang' или добавь в PATH."
    }
}

Write-Host "[1/7] Build Go server from $serverDir..."
# -NoBuild только пропускает rebuild если оба exe УЖЕ есть. Иначе ALWAYS build.
$needsBuild = -not $NoBuild -or -not (Test-Path $serverExe) -or -not (Test-Path $hashpassExe)
if ($needsBuild) {
    Push-Location $serverDir
    try {
        & $goExe build -o $serverExe ./cmd/signaling
        if ($LASTEXITCODE -ne 0) { throw "go build signaling failed" }
        & $goExe build -o $hashpassExe ./cmd/admin-hashpass
        if ($LASTEXITCODE -ne 0) { throw "go build admin-hashpass failed" }
    } finally {
        Pop-Location
    }
} else {
    Write-Host "    -NoBuild: skipped (both exe exist)"
}
Write-Host "    -> $serverExe"

# Generate temporary bcrypt hash через temp file (robust к stderr/stdout merge issues).
Write-Host "[2/7] Generate admin bcrypt hash..."
$testPass = 'integration-test-pass-12345'
$hashStdoutFile = Join-Path $env:TEMP "zconect-hash-$(Get-Random).txt"
# Start-Process с -RedirectStandardOutput пишет в файл; stderr игнорим.
$hashProc = Start-Process -FilePath $hashpassExe -PassThru -NoNewWindow -Wait `
    -RedirectStandardInput (New-Object System.IO.StringReader($testPass)) `
    -RedirectStandardOutput $hashStdoutFile -RedirectStandardError "NUL" `
    -ErrorAction SilentlyContinue 2>$null
# Fallback — PowerShell 5.1 не всегда поддерживает -RedirectStandardInput со StringReader.
# Используем cmd.exe echo pipe через файл.
if (-not (Test-Path $hashStdoutFile) -or (Get-Item $hashStdoutFile).Length -eq 0) {
    $passInFile = Join-Path $env:TEMP "zconect-passin-$(Get-Random).txt"
    Set-Content -Path $passInFile -Value $testPass -NoNewline -Encoding ASCII
    # cmd /c type pipe'ит файл в exe и redirect'ит stdout в файл.
    & cmd /c "type `"$passInFile`" | `"$hashpassExe`" > `"$hashStdoutFile`" 2>NUL"
    Remove-Item $passInFile -Force -ErrorAction SilentlyContinue
}
$adminHash = (Get-Content $hashStdoutFile -Raw -ErrorAction SilentlyContinue).Trim()
Remove-Item $hashStdoutFile -Force -ErrorAction SilentlyContinue
if ([string]::IsNullOrWhiteSpace($adminHash) -or -not $adminHash.StartsWith('$2')) {
    throw "Не удалось получить bcrypt hash (got: '$adminHash')"
}
Write-Host "    -> hash ok (len=$($adminHash.Length))"

# Temp dir для изоляции server state
$tmpBase = Join-Path $env:TEMP "zconect-integration-$(Get-Random)"
New-Item -ItemType Directory -Path $tmpBase | Out-Null
$dbPath = Join-Path $tmpBase 'telemetry.db'
$versionPath = Join-Path $tmpBase 'client-version.json'
$hashFile = Join-Path $tmpBase 'admin-hash.txt'
$serverLog = Join-Path $tmpBase 'server.log'

Write-Host "[3/7] Start server on localhost:$Port (logs: $serverLog)"
$envVars = @{
    'SIGNALING_PORT' = "$Port"
    'ADMIN_PASSWORD_HASH' = $adminHash
    'ADMIN_PASSWORD_HASH_FILE' = $hashFile
    'TELEMETRY_DB_PATH' = $dbPath
    'CLIENT_VERSION_FILE' = $versionPath
    'APP_ENV' = 'dev'
    'ALLOWED_ORIGINS' = "http://127.0.0.1:$Port,http://localhost:$Port"
    'SESSION_TTL_SEC' = '300'
    'MAX_JOIN_ATTEMPTS' = '5'
}
foreach ($k in $envVars.Keys) { [Environment]::SetEnvironmentVariable($k, $envVars[$k], 'Process') }

$serverProcess = Start-Process -FilePath $serverExe -PassThru -NoNewWindow `
    -RedirectStandardOutput $serverLog -RedirectStandardError "$serverLog.err"
Write-Host "    → PID=$($serverProcess.Id)"

# Ждём healthz
Write-Host "[4/7] Wait for healthz..."
$healthOk = $false
$started = Get-Date
while (((Get-Date) - $started).TotalSeconds -lt $HealthWaitSec) {
    try {
        $r = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/healthz" -TimeoutSec 2 -ErrorAction Stop
        if ($r.status -eq 'ok') { $healthOk = $true; break }
    } catch {
        Start-Sleep -Milliseconds 400
    }
}
if (-not $healthOk) {
    Write-Host "Server log tail:"
    Get-Content $serverLog -Tail 20 -ErrorAction SilentlyContinue
    Write-Host "Server stderr tail:"
    Get-Content "$serverLog.err" -Tail 20 -ErrorAction SilentlyContinue
    Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
    Remove-Item -Recurse -Force $tmpBase -ErrorAction SilentlyContinue
    throw "Сервер не ответил healthz за $HealthWaitSec сек."
}
Write-Host "    → healthz OK"

# Run .NET tests
Write-Host "[5/7] Run dotnet test (filter: $FilterClass / Category=$TestCategory)..."
$env:ZCONECT_TEST_SERVER = "http://127.0.0.1:$Port"
$env:ZCONECT_TEST_WS = "ws://127.0.0.1:$Port/ws"

$filterExpr = "FullyQualifiedName~$FilterClass"
if ($TestCategory) { $filterExpr += "&Category=$TestCategory" }

$testArgs = @('test', $testsProj, '--filter', $filterExpr, '-c', 'Debug', '--logger', 'console;verbosity=minimal')
& dotnet @testArgs
$testExitCode = $LASTEXITCODE
Write-Host "    → dotnet test exit code: $testExitCode"

# Stop server
if (-not $KeepServerRunning) {
    Write-Host "[6/7] Stop server (PID=$($serverProcess.Id))..."
    Stop-Process -Id $serverProcess.Id -Force -ErrorAction SilentlyContinue
    Write-Host "    → stopped"
} else {
    Write-Host "[6/7] -KeepServerRunning: сервер остаётся работать на :$Port (PID=$($serverProcess.Id))"
    Write-Host "    Логи: $serverLog"
    Write-Host "    Temp dir: $tmpBase"
    Write-Host "    Остановить: Stop-Process -Id $($serverProcess.Id) -Force"
}

# Cleanup
if (-not $KeepServerRunning) {
    Write-Host "[7/7] Cleanup temp files..."
    Start-Sleep -Milliseconds 500 # give server time to flush
    Remove-Item -Recurse -Force $tmpBase -ErrorAction SilentlyContinue
    Remove-Item $serverExe -Force -ErrorAction SilentlyContinue
    Remove-Item $hashpassExe -Force -ErrorAction SilentlyContinue
    Write-Host "    → clean"
}

# Exit с test exit code для CI integration
if ($testExitCode -ne 0) {
    Write-Host "`n[FAIL] Integration tests FAILED (exit=$testExitCode)" -ForegroundColor Red
    exit $testExitCode
} else {
    Write-Host "`n[OK] Integration tests PASSED" -ForegroundColor Green
}
