function Invoke-CheckedDotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet завершился с кодом $LASTEXITCODE ($($Arguments[0]))." }
}

function Assert-PinnedSdk {
    param([Parameter(Mandatory)][string]$Root)
    $expected = (Get-Content -LiteralPath (Join-Path $Root 'global.json') -Raw | ConvertFrom-Json).sdk.version
    $actual = & dotnet --version
    if ($LASTEXITCODE -ne 0 -or $actual.Trim() -ne $expected) {
        throw "Для сборки нужен SDK $expected из global.json. Установите его и повторите команду."
    }
    return $expected
}

function Restore-LockedProject {
    param([string]$Project, [string]$Root, [switch]$App, [switch]$Offline)
    $arguments = @('restore', $Project, '--locked-mode', '-v:minimal')
    if ($App) { $arguments += @('-p:Platform=x64', '-p:SelfContained=true') }
    if ($Offline) {
        $cache = Join-Path $Root '.work\nuget'
        if (-not (Test-Path -LiteralPath $cache -PathType Container)) { throw "Нет локального кэша: $cache" }
        $arguments += @('--source', $cache, '-p:NuGetAudit=false')
    }
    else {
        # A network/audit failure must not be mistaken for a clean dependency report.
        $arguments += @('-p:NuGetAudit=true', '-p:WarningsAsErrors=NU1801%3BNU1900%3BNU1901%3BNU1902%3BNU1903%3BNU1904%3BNU1905')
    }
    Invoke-CheckedDotNet -Arguments $arguments
}
