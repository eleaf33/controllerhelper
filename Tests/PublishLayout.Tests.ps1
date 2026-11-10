$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\Build\PublishLayout.ps1')
$fixture = Join-Path $PSScriptRoot ('..\.work\shortcut-test-' + [Guid]::NewGuid().ToString('N'))
$old = Join-Path $fixture 'old'
$first = Join-Path $fixture 'first'
$second = Join-Path $fixture 'second'
$oldShortcuts = Join-Path $old 'shortcuts'
New-Item -ItemType Directory -Path $oldShortcuts,$first,$second | Out-Null
$shortcutNames = @('ControllerHelper - возврат к ПК.lnk', 'custom [1].LNK')
foreach ($shortcutName in $shortcutNames) {
    [IO.File]::WriteAllBytes((Join-Path $oldShortcuts $shortcutName), [byte[]](1,2,3,4,5))
}
[IO.File]::WriteAllText((Join-Path $oldShortcuts 'unrelated.txt'), 'Do not copy this file')
Copy-PublishShortcuts -PreviousPublication $old -StagedPublication $first
Copy-PublishShortcuts -PreviousPublication $first -StagedPublication $second
foreach ($shortcutName in $shortcutNames) {
    $originalHash = (Get-FileHash -LiteralPath (Join-Path $oldShortcuts $shortcutName)).Hash
    foreach ($publication in @($first,$second)) {
        $copiedHash = (Get-FileHash -LiteralPath (Join-Path (Join-Path $publication 'shortcuts') $shortcutName)).Hash
        if ($originalHash -ne $copiedHash) { throw "Ярлык повреждён: $shortcutName" }
    }
}
if (Test-Path -LiteralPath (Join-Path $first 'shortcuts\unrelated.txt')) { throw 'Перенесён посторонний файл.' }
Copy-PublishShortcuts -PreviousPublication (Join-Path $fixture 'missing') -StagedPublication $second
Write-Output 'PASS: штатный и пользовательский ярлыки сохранены после двух переносов; прочие файлы исключены; отсутствие старой публикации допустимо.'
Write-Output "Проверочные файлы: $fixture"
