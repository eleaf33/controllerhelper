param([switch]$Offline, [switch]$NoRestore)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build\Commands.ps1')
Push-Location $PSScriptRoot
try {
    $sdk = Assert-PinnedSdk -Root $PSScriptRoot
    Write-Host "SDK: $sdk"
    if ($Offline -or $NoRestore) { Write-Warning 'Проверка актуальных уязвимостей пакетов пропущена (Offline/NoRestore).' }
    if (-not $NoRestore) {
        Restore-LockedProject -Project 'ControllerHelper.csproj' -Root $PSScriptRoot -App -Offline:$Offline
        Restore-LockedProject -Project 'Tests\ControllerHelper.Tests.csproj' -Root $PSScriptRoot -Offline:$Offline
    }
    Invoke-CheckedDotNet -Arguments @('build', 'ControllerHelper.csproj', '-c', 'Release',
        '-p:Platform=x64', '-p:SelfContained=true', "-p:OutDir=$PSScriptRoot/.work/verified-build/", '--no-restore', '-v:minimal')
    Invoke-CheckedDotNet -Arguments @('build', 'Tests\ControllerHelper.Tests.csproj', '-c', 'Release', '--no-restore', '-v:minimal')
    Invoke-CheckedDotNet -Arguments @('Tests\bin\Release\net10.0\ControllerHelper.Tests.dll')
    & (Join-Path $PSScriptRoot 'Tests\PublishLayout.Tests.ps1')
    & (Join-Path $PSScriptRoot 'Tests\Publication.Tests.ps1')
    & (Join-Path $PSScriptRoot 'Tests\LockedRestore.Tests.ps1')
    Write-Host 'PASS: сборка, C#-тесты и проверки публикации.'
}
finally { Pop-Location }
