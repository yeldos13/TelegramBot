$ErrorActionPreference = "Stop"
$ServiceName = "AnikiChatBot"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Запустите PowerShell от имени администратора." }

$svc = Get-Service $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne "Stopped") { Stop-Service $ServiceName }
    sc.exe delete $ServiceName | Out-Null
    Write-Host "Служба $ServiceName удалена."
} else {
    Write-Host "Служба $ServiceName не установлена."
}
Write-Host "Папку C:\Bots\AnikiChatBot (с replies.txt и логами) можно удалить вручную."
