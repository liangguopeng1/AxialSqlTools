$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$keysPath = Join-Path $PSScriptRoot 'i18n-new-keys.json'
$enPath = Join-Path $root 'AxialSqlTools\Properties\Strings.resx'
$zhPath = Join-Path $root 'AxialSqlTools\Properties\Strings.zh-Hans.resx'
function Esc([string]$s) {
  if ($null -eq $s) { return '' }
  return ($s -replace '&','&amp;' -replace '<','&lt;' -replace '>','&gt;')
}
function Get-ExistingNames([string]$path) {
  $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
  $set = New-Object 'System.Collections.Generic.HashSet[string]'
  foreach ($m in [regex]::Matches($text, '<data name="([^"]+)"')) { [void]$set.Add($m.Groups[1].Value) }
  return @{ Text = $text; Names = $set }
}
function Append-Keys([string]$path, [hashtable]$values) {
  $info = Get-ExistingNames $path
  $text = $info.Text
  $added = 0
  $sb = New-Object System.Text.StringBuilder
  foreach ($name in ($values.Keys | Sort-Object)) {
    if ($info.Names.Contains($name)) { continue }
    [void]$sb.AppendLine(('  <data name="{0}" xml:space="preserve"><value>{1}</value></data>' -f $name, (Esc $values[$name])))
    $added++
  }
  if ($added -eq 0) { return 0 }
  if ($text -notmatch '</root>\s*$') { throw "No closing root in $path" }
  $text = [regex]::Replace($text, '</root>\s*$', ($sb.ToString() + "</root>`n"))
  $utf8 = New-Object System.Text.UTF8Encoding $false
  [System.IO.File]::WriteAllText($path, $text, $utf8)
  return $added
}
$obj = Get-Content -LiteralPath $keysPath -Encoding UTF8 -Raw | ConvertFrom-Json
$en = @{}; $zh = @{}
$obj.PSObject.Properties | ForEach-Object {
  $en[$_.Name] = [string]$_.Value.en
  $zh[$_.Name] = [string]$_.Value.zh
}
# Fix garbled Chinese language label in EN neutral resource (keep native name)
$enText = [System.IO.File]::ReadAllText($enPath, [System.Text.Encoding]::UTF8)
$enText2 = [regex]::Replace($enText, '(<data name="Settings_Language_Chinese"[^>]*>\s*<value>)(.*?)(</value>)', '${1}中文${3}')
if ($enText2 -ne $enText) {
  $utf8 = New-Object System.Text.UTF8Encoding $false
  [System.IO.File]::WriteAllText($enPath, $enText2, $utf8)
  Write-Output 'Fixed Settings_Language_Chinese in Strings.resx'
}
$a1 = Append-Keys $enPath $en
$a2 = Append-Keys $zhPath $zh
Write-Output "Appended EN=$a1 ZH=$a2"
[xml]$x1 = [System.IO.File]::ReadAllText($enPath, [System.Text.Encoding]::UTF8)
[xml]$x2 = [System.IO.File]::ReadAllText($zhPath, [System.Text.Encoding]::UTF8)
Write-Output 'XML OK'
