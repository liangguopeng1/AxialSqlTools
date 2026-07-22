$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$patterns = @(
  @{ Old = 'github.com/Axial-SQL/AxialSqlTools'; New = 'github.com/liangguopeng1/AxialSqlTools' },
  @{ Old = 'repos/Axial-SQL/AxialSqlTools'; New = 'repos/liangguopeng1/AxialSqlTools' }
)
$exts = @('.cs', '.xaml', '.md', '.vsixmanifest')
$utf8 = New-Object System.Text.UTF8Encoding $false
$changed = 0
Get-ChildItem -Path $root -Recurse -File | Where-Object {
  $exts -contains $_.Extension -and $_.FullName -notmatch '\\\.git\\|\\bin\\|\\obj\\'
} | ForEach-Object {
  $text = [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
  $updated = $text
  foreach ($p in $patterns) { $updated = $updated.Replace($p.Old, $p.New) }
  if ($updated -ne $text) {
    [System.IO.File]::WriteAllText($_.FullName, $updated, $utf8)
    $changed++
    Write-Output $_.FullName.Substring($root.Length + 1)
  }
}
Write-Output "filesChanged=$changed"
