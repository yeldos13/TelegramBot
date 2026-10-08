param(
    [string]$InstallDir = "C:\Bots\AnikiChatBot",
    [string]$DataFrom = ""
)

$ErrorActionPreference = "Stop"
$ServiceName = "AnikiChatBot"
$RepoRoot = Split-Path $PSScriptRoot -Parent
$ProjectDir = Join-Path $RepoRoot "AnikiChatBot"
$Project = Join-Path $ProjectDir "AnikiChatBot.csproj"
if (-not $DataFrom) { $DataFrom = Join-Path $ProjectDir "bin\Debug\net10.0" }

function Step($text) { Write-Host "`n==> $text" -ForegroundColor Cyan }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Запустите PowerShell от имени администратора." }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "Не найден .NET SDK. Установите .NET 10 SDK: https://dotnet.microsoft.com/download"
}
if (-not (Test-Path (Join-Path $ProjectDir "appsettings.json"))) {
    throw "Нет $ProjectDir\appsettings.json (он не хранится в git). Создайте его с BotToken, AllowedChatIds и ExchangeApiKey."
}

Step "Публикация бота в $InstallDir"
$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -ne "Stopped") { Stop-Service $ServiceName; Start-Sleep 2 }
dotnet publish $Project -c Release -r win-x64 --self-contained false -o $InstallDir
if ($LASTEXITCODE -ne 0) { throw "Ошибка публикации." }

Step "Защита appsettings.json и папки private"
icacls (Join-Path $InstallDir "appsettings.json") /inheritance:r /grant:r "*S-1-5-18:F" "*S-1-5-32-544:F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Не удалось ограничить доступ к appsettings.json." }

$private = Join-Path $InstallDir "private"
New-Item -ItemType Directory -Force $private | Out-Null
icacls $private /inheritance:r /grant:r "*S-1-5-18:(OI)(CI)F" "*S-1-5-32-544:(OI)(CI)F" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Не удалось ограничить доступ к папке private." }
if (Get-ChildItem $private -Force) { icacls "$private\*" /reset /T | Out-Null }

Step "Перенос данных из $DataFrom"
foreach ($file in "replies.txt", "rates_cache.json", "owner_id.txt", "stats.json", "pig.json", "morning_rates.json", "muted.json", "newcomers.json") {
    $source = Join-Path $DataFrom $file
    $target = Join-Path $InstallDir $file
    if (Test-Path $target) {
        Write-Host "$file уже есть в $InstallDir — не трогаю."
    } elseif (Test-Path $source) {
        Copy-Item $source $target
        Write-Host "$file скопирован."
    } else {
        Write-Host "$file не найден — бот создаст его сам."
    }
}

Step "Служба Windows '$ServiceName'"
$exe = Join-Path $InstallDir "AnikiChatBot.exe"
$binPath = "`"$exe`""
if (-not $svc) {
    New-Service -Name $ServiceName -BinaryPathName $binPath -DisplayName "AnikiChatBot" `
        -Description "Telegram-бот: курсы валют, скачивание медиа, автоответы" -StartupType Automatic | Out-Null
} else {
    Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name ImagePath -Value $binPath
    Set-Service $ServiceName -StartupType Automatic
}
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
sc.exe failureflag $ServiceName 1 | Out-Null
Start-Service $ServiceName

Step "Проверка"
Start-Sleep 5
$log = Get-ChildItem (Join-Path $InstallDir "logs") -Filter "bot-*.log" -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime | Select-Object -Last 1
if ($log) { Get-Content $log.FullName -Tail 5 -Encoding UTF8 }

if ((Get-Service $ServiceName).Status -eq "Running") {
    Write-Host "`nГотово. Бот работает как служба и сам запустится после перезагрузки." -ForegroundColor Green
} else {
    Write-Warning "Служба не запущена — смотрите логи в $InstallDir\logs"
}
Write-Host "Не запускайте бота одновременно из Visual Studio: два экземпляра с одним токеном мешают друг другу."
