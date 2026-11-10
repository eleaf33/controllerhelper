$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $root ('.work\locked-restore-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
try {
    $project = Join-Path $fixture 'ControllerHelper.csproj'
    $lock = Join-Path $fixture 'packages.lock.json'
    $source = [IO.File]::ReadAllText((Join-Path $root 'ControllerHelper.csproj'))
    # Deliberately disagree with the lock without downloading a different version.
    if (-not $source.Contains('Version="2.5.1"')) { throw 'Обновите проверку изменённой версии WindowsAppSDK.' }
    [IO.File]::WriteAllText($project, $source.Replace('Version="2.5.1"', 'Version="2.5.0"'))
    Copy-Item -LiteralPath (Join-Path $root 'packages.lock.json') -Destination $lock
    $before = (Get-FileHash -LiteralPath $lock).Hash
    # Empty source ensures this negative test cannot use the network.
    $emptySource = Join-Path $fixture 'empty-feed'
    New-Item -ItemType Directory -Path $emptySource | Out-Null
    $output = & dotnet restore $project --locked-mode --source $emptySource -p:Platform=x64 -p:SelfContained=true -p:NuGetAudit=false 2>&1
    if ($LASTEXITCODE -eq 0 -or ($output -join "`n") -notmatch 'NU1004') { throw "Несогласованные зависимости не отклонены locked restore: $output" }
    if ((Get-FileHash -LiteralPath $lock).Hash -ne $before) { throw 'Locked restore изменил lock-файл.' }
    Write-Output 'PASS: изменение зависимости отклонено NU1004; lock-файл не изменён.'
}
finally {
    $fullPath = [IO.Path]::GetFullPath($fixture)
    if ([IO.Path]::GetDirectoryName($fullPath) -ne (Join-Path $root '.work') -or
        -not [IO.Path]::GetFileName($fullPath).StartsWith('locked-restore-test-')) { throw "Неверный путь очистки: $fullPath" }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}
