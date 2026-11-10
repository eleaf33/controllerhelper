function Copy-PublishShortcuts {
    param(
        [Parameter(Mandatory)][string]$PreviousPublication,
        [Parameter(Mandatory)][string]$StagedPublication
    )

    $sourceDirectory = Join-Path $PreviousPublication 'shortcuts'
    if (-not (Test-Path -LiteralPath $sourceDirectory)) { return }
    $sourceInfo = Get-Item -LiteralPath $sourceDirectory -Force
    if (-not $sourceInfo.PSIsContainer -or ($sourceInfo.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Каталог ярлыков должен быть обычной папкой: $sourceDirectory"
    }
    $shortcutFiles = @(Get-ChildItem -LiteralPath $sourceDirectory -File -Force |
        Where-Object { $_.Extension -ieq '.lnk' })
    if ($shortcutFiles.Count -eq 0) { return }
    $destinationDirectory = Join-Path $StagedPublication 'shortcuts'
    New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    foreach ($shortcutFile in $shortcutFiles) {
        if ($shortcutFile.Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Нельзя переносить файловую ссылку вместо ярлыка: $($shortcutFile.FullName)"
        }
        Copy-Item -LiteralPath $shortcutFile.FullName -Destination (Join-Path $destinationDirectory $shortcutFile.Name) -Force
    }
}
