param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'diagnostics'))
$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'logs'
$destination = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('report-' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $destination | Out-Null
# Copy only diagnostic journals. Settings contain pairing credentials and stay local.
foreach ($name in @('errors.jsonl', 'errors.jsonl.1', 'launcher.log', 'launcher.log.1')) {
    $file = Join-Path $source $name
    if (Test-Path -LiteralPath $file -PathType Leaf) {
        Copy-Item -LiteralPath $file -Destination (Join-Path $destination $name)
    }
}
$processes = @(Get-Process ControllerHelper -ErrorAction SilentlyContinue | ForEach-Object {
    [ordered]@{ pid = $_.Id; path = $_.Path; session = $_.SessionId }
})
[ordered]@{
    capturedUtc = [DateTime]::UtcNow.ToString('o')
    windows = [Environment]::OSVersion.VersionString
    is64BitOS = [Environment]::Is64BitOperatingSystem
    processes = $processes
    note = 'Process paths can contain the Windows account name. Settings and pairing keys are not included. Logs copied during an operation may be incomplete; export again after it finishes.'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $destination 'environment.json') -Encoding utf8
Write-Output "Диагностика сохранена: $destination"
