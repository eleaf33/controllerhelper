$ErrorActionPreference = 'Stop'
$publish = Join-Path $PSScriptRoot 'publish-controllerhelper'
$application = Join-Path $publish 'ControllerHelper.exe'
$shortcuts = Join-Path $publish 'shortcuts'
$shortcutPath = Join-Path $shortcuts 'ControllerHelper - возврат к ПК.lnk'

if (-not (Test-Path -LiteralPath $application)) {
    throw 'Сначала запустите build.ps1.'
}

$shell = New-Object -ComObject WScript.Shell
New-Item -ItemType Directory -Path $shortcuts -Force | Out-Null
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $application
$shortcut.Arguments = '--to-pc'
$shortcut.WorkingDirectory = $publish
$shortcut.IconLocation = $application
$shortcut.Description = 'Вернуть изображение и звук на ПК'
$shortcut.Save()
Write-Host "Ярлык: $shortcutPath"
