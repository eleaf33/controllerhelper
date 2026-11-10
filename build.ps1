param(
    [switch]$NoRestore,
    [switch]$Offline,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'publish-controllerhelper')
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Build\Commands.ps1')
. (Join-Path $PSScriptRoot 'Build\Publication.ps1')
Push-Location $PSScriptRoot
try {
    $publish = Assert-PublicationPath -Root $PSScriptRoot -Path $OutputDirectory
    $sdk = Assert-PinnedSdk -Root $PSScriptRoot
    # Stop on a failed test before preparing or replacing any publication.
    & (Join-Path $PSScriptRoot 'verify.ps1') -Offline:$Offline -NoRestore:$NoRestore
    $stage = Join-Path $PSScriptRoot ('.work\publish-stage-' + [Guid]::NewGuid().ToString('N'))
    $payload = Join-Path $stage 'lib'
    Invoke-CheckedDotNet -Arguments @('publish', 'ControllerHelper.csproj', '-c', 'Release', '-r', 'win-x64',
        '--self-contained', 'true', '-p:Platform=x64', '-p:PublishSingleFile=false',
        '--no-restore', '-o', $payload, '-v:minimal')
    $languages = Join-Path $stage 'lang'
    New-Item -ItemType Directory -Path $languages | Out-Null
    Get-ChildItem -LiteralPath $payload -Directory | Where-Object { $_.Name -match '^[a-z]{2,3}(-[a-z0-9]+)+$' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $languages $_.Name) -Recurse }

    # Use the pinned SDK compiler, rather than whichever legacy compiler Windows ships.
    $sdkDirectory = (& dotnet msbuild ControllerHelper.csproj -nologo -getProperty:MSBuildBinPath).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Не удалось определить компилятор SDK.' }
    $compiler = Join-Path $sdkDirectory 'Roslyn\bincore\csc.dll'
    $framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    $references = @('mscorlib.dll', 'System.dll', 'System.Core.dll', 'System.Runtime.Serialization.dll', 'System.Xml.dll')
    $compileArguments = @($compiler, '/nologo', '/noconfig', '/nostdlib+', '/deterministic+', '/optimize+', '/target:winexe',
        "/out:$(Join-Path $stage 'ControllerHelper.exe')", "/win32icon:$(Join-Path $PSScriptRoot 'controller.ico')")
    foreach ($reference in $references) { $compileArguments += "/reference:$(Join-Path $framework $reference)" }
    $compileArguments += (Join-Path $PSScriptRoot 'Launcher\Program.cs')
    Invoke-CheckedDotNet -Arguments $compileArguments

    $files = @(Get-ChildItem -LiteralPath $stage -File -Recurse | Sort-Object FullName | ForEach-Object {
        [ordered]@{ path = $_.FullName.Substring($stage.TrimEnd('\').Length + 1).Replace('\', '/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
    })
    $frameworkHashes = @($references | ForEach-Object {
        [ordered]@{ name = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $framework $_) -Algorithm SHA256).Hash }
    })
    $manifest = [ordered]@{
        sdk = $sdk; runtimeIdentifier = 'win-x64'; configuration = 'Release'
        dependencyLockSha256 = (Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'packages.lock.json') -Algorithm SHA256).Hash
        packageAudit = $(if ($Offline -or $NoRestore) { 'not-checked' } else { 'passed-during-restore' })
        launcherFrameworkReferences = $frameworkHashes; files = $files
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'build-manifest.json') -Encoding utf8
    $backup = Install-StagedPublication -Root $PSScriptRoot -Stage $stage -Destination $publish
    Write-Host "Приложение: $(Join-Path $publish 'ControllerHelper.exe')"
    if ($backup) { Write-Host "Предыдущая публикация сохранена: $backup" }
}
finally { Pop-Location }
