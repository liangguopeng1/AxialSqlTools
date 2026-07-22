$p = Join-Path $env:APPDATA 'AxialSqlTools\settings.json'
$out = Join-Path $PSScriptRoot 'settings-dump.txt'
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine("path=$p")
[void]$sb.AppendLine("exists=$([bool](Test-Path -LiteralPath $p))")
if (Test-Path -LiteralPath $p) {
  [void]$sb.AppendLine([System.IO.File]::ReadAllText($p, [System.Text.Encoding]::UTF8))
}
[System.IO.File]::WriteAllText($out, $sb.ToString(), (New-Object System.Text.UTF8Encoding $false))
Write-Output "wrote $out"
