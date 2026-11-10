. (Join-Path $PSScriptRoot 'PublishLayout.ps1')

function Assert-PublicationPath {
    param([string]$Root, [string]$Path)
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\')
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $workPath = Join-Path $rootPath '.work'
    $isDefault = $fullPath -eq (Join-Path $rootPath 'publish-controllerhelper')
    $isWork = [IO.Path]::GetDirectoryName($fullPath) -eq $workPath -and
        [IO.Path]::GetFileName($fullPath).StartsWith('publish-', [StringComparison]::OrdinalIgnoreCase)
    if (-not $isDefault -and -not $isWork) { throw "Недопустимый путь публикации: $fullPath" }
    if ((Test-Path -LiteralPath $fullPath) -and -not (Test-Path -LiteralPath $fullPath -PathType Container)) {
        throw "Публикация должна быть каталогом: $fullPath"
    }
    foreach ($candidate in @($rootPath, $workPath, $fullPath)) {
        if ((Test-Path -LiteralPath $candidate) -and
            ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Путь публикации не должен быть ссылкой: $candidate"
        }
    }
    return $fullPath
}

function Move-PublicationDirectory {
    param([string]$Source, [string]$Destination)
    Move-Item -LiteralPath $Source -Destination $Destination -ErrorAction Stop
}

function Install-StagedPublication {
    param([string]$Root, [string]$Stage, [string]$Destination)
    $stagePath = Assert-PublicationPath -Root $Root -Path $Stage
    $destinationPath = Assert-PublicationPath -Root $Root -Path $Destination
    if ($stagePath -eq $destinationPath) { throw 'Промежуточная и рабочая папки совпадают.' }
    foreach ($relative in @('ControllerHelper.exe', 'lib\ControllerHelper.exe', 'build-manifest.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stagePath $relative) -PathType Leaf)) { throw "Публикация не готова: $relative" }
    }
    $active = Get-Process ControllerHelper -ErrorAction SilentlyContinue | Where-Object {
        -not $_.Path -or $_.Path.StartsWith($destinationPath + '\', [StringComparison]::OrdinalIgnoreCase)
    }
    if ($active) { throw 'Закройте ControllerHelper из папки заменяемой публикации (или проверьте доступ к пути процесса).' }
    Copy-PublishShortcuts -PreviousPublication $destinationPath -StagedPublication $stagePath
    $backup = $null
    if (Test-Path -LiteralPath $destinationPath) {
        $backup = Assert-PublicationPath -Root $Root -Path (Join-Path $Root ('.work\publish-backup-' + [Guid]::NewGuid().ToString('N')))
        Move-PublicationDirectory -Source $destinationPath -Destination $backup
    }
    try { Move-PublicationDirectory -Source $stagePath -Destination $destinationPath }
    catch {
        if ($backup -and -not (Test-Path -LiteralPath $destinationPath)) {
            Move-PublicationDirectory -Source $backup -Destination $destinationPath
        }
        throw
    }
    # Keep the prior publication for recovery instead of deleting it on success.
    return $backup
}
