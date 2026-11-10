param(
    [string]$ExecutablePath = (Join-Path $PSScriptRoot 'bin\x64\Release\net10.0-windows10.0.26100.0\win-x64\ControllerHelper.exe')
)

$ErrorActionPreference = 'Stop'

# A process started by the sandboxed Codex terminal inherits its restricted token.
# Detect this before launch; neither a working directory nor UseShellExecute fixes it.
if (-not ('ControllerHelper.LaunchContext' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
namespace ControllerHelper {
    public static class LaunchContext {
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool IsTokenRestricted(IntPtr token);
        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
        public static bool IsRestricted() {
            using (Process process = Process.GetCurrentProcess()) {
                IntPtr token;
                if (!OpenProcessToken(process.Handle, 8, out token))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try { return IsTokenRestricted(token); }
                finally { CloseHandle(token); }
            }
        }
    }
}
'@
}

if ([ControllerHelper.LaunchContext]::IsRestricted()) {
    throw 'Запуск отменён: терминал имеет ограниченный токен. Запустите скрипт из обычного PowerShell пользователя. В Codex используйте exec_command с sandbox_permissions=require_escalated (вне песочницы, без RunAs/повышения до администратора).'
}

if (-not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
    throw "Сборка не найдена: $ExecutablePath"
}
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
if (@(Get-Process ControllerHelper -ErrorAction SilentlyContinue).Count -gt 0) {
    throw 'ControllerHelper уже запущен. Завершите прежний экземпляр через трей → Выход, иначе новая сборка передаст ему команду вместо запуска.'
}

$launchedProcess = Start-Process -FilePath $resolvedExecutable -WorkingDirectory (Split-Path -Parent $resolvedExecutable) -PassThru
Write-Host "Запущен процесс $($launchedProcess.Id): $resolvedExecutable"
Write-Host 'Проверьте окно настроек, обнаружение выбранного геймпада и открытие оверлея хоткеем.'
