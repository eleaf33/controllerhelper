$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Build\Publication.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$work = Join-Path $root '.work'
$prefix = 'publish-test-' + [Guid]::NewGuid().ToString('N')
$destination = Join-Path $work ($prefix + '-current')
$first = Join-Path $work ($prefix + '-first')
$second = Join-Path $work ($prefix + '-second')
$failed = Join-Path $work ($prefix + '-failed')
$cleanup = [Collections.Generic.List[string]]::new()
foreach ($path in @($destination,$first,$second,$failed)) { $cleanup.Add($path) }

function New-TestPublication([string]$Path, [string]$Version) {
    New-Item -ItemType Directory -Path (Join-Path $Path 'lib') -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $Path 'ControllerHelper.exe'), $Version)
    [IO.File]::WriteAllText((Join-Path $Path 'lib\ControllerHelper.exe'), $Version)
    [IO.File]::WriteAllText((Join-Path $Path 'build-manifest.json'), '{}')
}

try {
    New-TestPublication $destination 'old'
    New-Item -ItemType Directory -Path (Join-Path $destination 'shortcuts') | Out-Null
    $shortcut = 'shortcuts\custom [1].LNK'
    [IO.File]::WriteAllText((Join-Path $destination $shortcut), 'keep these exact bytes')
    New-TestPublication $first 'first'
    $cleanup.Add((Install-StagedPublication -Root $root -Stage $first -Destination $destination))
    New-TestPublication $second 'second'
    $cleanup.Add((Install-StagedPublication -Root $root -Stage $second -Destination $destination))
    if ([IO.File]::ReadAllText((Join-Path $destination 'ControllerHelper.exe')) -ne 'second' -or
        [IO.File]::ReadAllText((Join-Path $destination $shortcut)) -ne 'keep these exact bytes') {
        throw 'Повторное обновление повредило публикацию или ярлык.'
    }
    Write-Output 'PASS: два обновления публикации сохранили ярлыки и резервные версии.'

    New-TestPublication $failed 'failed'
    $script:FailStage = $failed
    function Move-PublicationDirectory([string]$Source, [string]$Destination) {
        if ($Source -eq $script:FailStage) { throw 'Искусственный отказ установки промежуточной папки.' }
        Move-Item -LiteralPath $Source -Destination $Destination -ErrorAction Stop
    }
    $rejected = $false
    try { Install-StagedPublication -Root $root -Stage $failed -Destination $destination | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected -or [IO.File]::ReadAllText((Join-Path $destination 'ControllerHelper.exe')) -ne 'second' -or
        -not (Test-Path -LiteralPath (Join-Path $destination $shortcut))) { throw 'Отказ установки не вернул прежнюю публикацию.' }
    Write-Output 'PASS: отказ замены возвращает прежнюю публикацию вместе с ярлыком.'

    $rejected = $false
    try { Assert-PublicationPath -Root $root -Path (Join-Path $root 'Models') | Out-Null }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'Публикация разрешена поверх исходников.' }
    Write-Output 'PASS: путь вне каталога публикации отклонён.'

    foreach ($scriptFile in @('build.ps1','verify.ps1','Build\Commands.ps1','Build\Publication.ps1','Build\PublishLayout.ps1')) {
        $tokens = $null; $parseErrors = $null
        [Management.Automation.Language.Parser]::ParseFile((Join-Path $root $scriptFile), [ref]$tokens, [ref]$parseErrors) | Out-Null
        if ($parseErrors.Count) { throw "Ошибка синтаксиса в $scriptFile : $parseErrors" }
    }
    Write-Output 'PASS: сценарии сборки и проверки проходят синтаксический разбор.'
}
finally {
    foreach ($path in $cleanup) {
        $fullPath = [IO.Path]::GetFullPath($path)
        if ([IO.Path]::GetDirectoryName($fullPath) -ne $work -or
            -not ([IO.Path]::GetFileName($fullPath).StartsWith($prefix) -or
                  [IO.Path]::GetFileName($fullPath).StartsWith('publish-backup-'))) { throw "Неверный путь очистки: $fullPath" }
        if (Test-Path -LiteralPath $fullPath) { Remove-Item -LiteralPath $fullPath -Recurse -Force }
    }
}
