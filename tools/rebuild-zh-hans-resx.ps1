$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$enPath = Join-Path $root 'AxialSqlTools\Properties\Strings.resx'
$zhPath = Join-Path $root 'AxialSqlTools\Properties\Strings.zh-Hans.resx'
$fragPath = Join-Path $PSScriptRoot 'i18n-keys-zh.fragment.xml'
$knownPath = Join-Path $PSScriptRoot 'i18n-known-zh.json'
function Get-ResxMap([string]$path) {
  $text = [System.IO.File]::ReadAllText($path, [System.Text.Encoding]::UTF8)
  $map = [ordered]@{}
  $rx = [regex]'<data name="([^"]+)"[^>]*>\s*<value>(.*?)</value>\s*</data>'
  foreach ($m in $rx.Matches($text)) {
    $name = $m.Groups[1].Value
    $val = [System.Net.WebUtility]::HtmlDecode($m.Groups[2].Value)
    $map[$name] = $val
  }
  return $map
}
function Esc([string]$s) {
  if ($null -eq $s) { return '' }
  return ($s -replace '&','&amp;' -replace '<','&lt;' -replace '>','&gt;')
}
$en = Get-ResxMap $enPath
$zhFrag = Get-ResxMap $fragPath
$knownObj = Get-Content -LiteralPath $knownPath -Encoding UTF8 -Raw | ConvertFrom-Json
$known = @{}
$knownObj.PSObject.Properties | ForEach-Object { $known[$_.Name] = [string]$_.Value }
$zhBroken = [ordered]@{}
if (Test-Path $zhPath) {
  $brokenText = [System.IO.File]::ReadAllText($zhPath, [System.Text.Encoding]::UTF8)
  $rx2 = [regex]'<data name="([^"]+)"[^>]*>\s*<value>(.*?)</value>\s*</data>'
  foreach ($m in $rx2.Matches($brokenText)) {
    $name = $m.Groups[1].Value
    $val = [System.Net.WebUtility]::HtmlDecode($m.Groups[2].Value)
    if ($val -match '[\u4e00-\u9fff]' -and $val -notmatch '\?/value>' -and $val -notmatch ([char]0xFFFD)) {
      $zhBroken[$name] = $val
    }
  }
}
$header = @'
<?xml version="1.0" encoding="utf-8"?>
<root>
  <xsd:schema id="root" xmlns="" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:msdata="urn:schemas-microsoft-com:xml-msdata">
    <xsd:element name="root" msdata:IsDataSet="true">
      <xsd:complexType>
        <xsd:choice maxOccurs="unbounded">
          <xsd:element name="data">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
                <xsd:element name="comment" type="xsd:string" minOccurs="0" msdata:Ordinal="2" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
          <xsd:element name="resheader">
            <xsd:complexType>
              <xsd:sequence>
                <xsd:element name="value" type="xsd:string" minOccurs="0" msdata:Ordinal="1" />
              </xsd:sequence>
              <xsd:attribute name="name" type="xsd:string" use="required" />
            </xsd:complexType>
          </xsd:element>
        </xsd:choice>
      </xsd:complexType>
    </xsd:element>
  </xsd:schema>
  <resheader name="resmimetype"><value>text/microsoft-resx</value></resheader>
  <resheader name="version"><value>2.0</value></resheader>
  <resheader name="reader"><value>System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
  <resheader name="writer"><value>System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089</value></resheader>
'@
$sb = New-Object System.Text.StringBuilder
[void]$sb.AppendLine($header.TrimEnd())
$fromKnown=0; $fromFrag=0; $fromBroken=0; $fromEn=0
$missing = New-Object System.Collections.Generic.List[string]
foreach ($name in $en.Keys) {
  $val = $null
  if ($known.ContainsKey($name)) { $val = $known[$name]; $fromKnown++ }
  elseif ($zhFrag.Contains($name)) { $val = $zhFrag[$name]; $fromFrag++ }
  elseif ($zhBroken.Contains($name)) { $val = $zhBroken[$name]; $fromBroken++ }
  else { $val = $en[$name]; $fromEn++; $missing.Add($name) }
  [void]$sb.AppendLine(('  <data name="{0}" xml:space="preserve"><value>{1}</value></data>' -f $name, (Esc $val)))
}
[void]$sb.AppendLine('</root>')
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[System.IO.File]::WriteAllText($zhPath, $sb.ToString(), $utf8NoBom)
Write-Output "EN keys=$($en.Count) FRAG=$($zhFrag.Count)"
Write-Output "sources: known=$fromKnown frag=$fromFrag broken=$fromBroken enFallback=$fromEn"
Write-Output "Wrote: $zhPath"
$check = [System.IO.File]::ReadAllText($zhPath, [System.Text.Encoding]::UTF8)
if ($check -match '\?/value>') { throw 'Still corrupt markers' }
[xml]$nullXml = $check
Write-Output 'XML OK'
if ($missing.Count -gt 0) {
  Write-Output "English fallback keys ($($missing.Count)):"
  $missing | ForEach-Object { Write-Output "  $_" }
}
Select-String -Path $zhPath -Pattern 'Settings_LanguageHint|Menu_Tools|Settings_Tab_General' | ForEach-Object { $_.Line }
