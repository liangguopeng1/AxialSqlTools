$ErrorActionPreference = 'Stop'
$path = Join-Path (Split-Path -Parent $PSScriptRoot) 'AxialSqlTools\Properties\Strings.resx'
$utf8 = New-Object System.Text.UTF8Encoding $false
$lines = [System.IO.File]::ReadAllLines($path, [System.Text.Encoding]::UTF8)
$zh = ([char]0x4E2D).ToString() + ([char]0x6587).ToString()
$fixed = 0
for ($i = 0; $i -lt $lines.Length; $i++) {
  if ($lines[$i] -match 'name="Settings_Language_Chinese"') {
    $lines[$i] = '  <data name="Settings_Language_Chinese" xml:space="preserve"><value>' + $zh + '</value></data>'
    $fixed++
  }
}
if ($fixed -eq 0) { throw 'Settings_Language_Chinese line not found' }
[System.IO.File]::WriteAllLines($path, $lines, $utf8)
$check = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
$m = [regex]::Match($check, 'name="Settings_Language_Chinese"[^>]*>\s*<value>(.*?)</value>')
$codes = ([int[]][char[]]$m.Groups[1].Value) -join ','
Write-Output ("fixed=$fixed valueCodes=$codes expected=20013,25991")
if ($codes -ne '20013,25991') { throw 'Value not 中文' }
Write-Output 'OK'
