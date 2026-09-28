param(
    [string]$InstallDir = "C:\Bots\AnikiChatBot",
    [switch]$NoPull
)

$ErrorActionPreference = "Stop"
$ServiceName = "AnikiChatBot"
$RepoRoot = Split-Path $PSScriptRoot -Parent
$Project = Join-Path $RepoRoot "AnikiChatBot\AnikiChatBot.csproj"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Запустите PowerShell от имени администратора." }

if (-not $NoPull) {
    Write-Host "==> git pull" -ForegroundColor Cyan
    git -C $RepoRoot pull --ff-only
    if ($LASTEXITCODE -ne 0) { throw "git pull не удался." }
}

Write-Host "==> Остановка службы" -ForegroundColor Cyan
Stop-Service $ServiceName
Start-Sleep 2

Write-Host "==> Публикация" -ForegroundColor Cyan
dotnet publish $Project -c Release -r win-x64 --self-contained false -o $InstallDir
$ok = $LASTEXITCODE -eq 0

foreach ($secret in "appsettings.json", "cookies.txt") {
    $path = Join-Path $InstallDir $secret
    if (Test-Path $path) { icacls $path /inheritance:r /grant:r "*S-1-5-18:F" "*S-1-5-32-544:F" | Out-Null }
}

Write-Host "==> Запуск службы" -ForegroundColor Cyan
Start-Service $ServiceName
if (-not $ok) { throw "Ошибка публикации — запущена предыдущая версия бота." }

Write-Host "Бот обновлён." -ForegroundColor Green
