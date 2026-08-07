<#
.SYNOPSIS
  100 common SQL IntelliSense keyword/context scenarios against Release AxialSqlTools.dll.
.EXAMPLE
  .\tools\intellisense-scenario-tests.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$builtDir = Join-Path $repoRoot "AxialSqlTools\bin\$Configuration"
$built = Join-Path $builtDir 'AxialSqlTools.dll'
if (-not (Test-Path $built)) { throw "DLL not found: $built" }

Get-ChildItem $builtDir -Filter '*.dll' | ForEach-Object { try { [void][Reflection.Assembly]::LoadFrom($_.FullName) } catch {} }
$asm = [Reflection.Assembly]::LoadFrom($built)
$engineType = $asm.GetType('AxialSqlTools.IntelliSense.CompletionEngine')
$settingsType = $asm.GetType('AxialSqlTools.IntelliSense.IntelliSenseSettings')
$get = $engineType.GetMethod('GetCompletion')

$cases = New-Object System.Collections.Generic.List[object]
function Add-Case([string]$name, [string]$sql, [string[]]$expect, [string]$ctx = '', [string[]]$mustNot = @()) {
    [void]$script:cases.Add([pscustomobject]@{ Name = $name; Sql = $sql; Expect = $expect; Ctx = $ctx; MustNot = $mustNot })
}

Add-Case '01' 's' @('SELECT') 'BatchStart'; Add-Case '02' 'se' @('SELECT') 'BatchStart'; Add-Case '03' 'in' @('INSERT') 'BatchStart'
Add-Case '04' 'up' @('UPDATE') 'BatchStart'; Add-Case '05' 'de' @('DELETE') 'BatchStart'; Add-Case '06' 'cr' @('CREATE') 'BatchStart'
Add-Case '07' 'al' @('ALTER') 'BatchStart'; Add-Case '08' 'ex' @('EXEC') 'BatchStart'; Add-Case '09' 'wi' @('WITH') 'BatchStart'
Add-Case '10' 'me' @('MERGE') 'BatchStart'
Add-Case '11' 'SELECT dis' @('DISTINCT') 'SelectElements'; Add-Case '12' 'SELECT DISTINCT' @('DISTINCT') 'SelectElements'
Add-Case '13' 'SELECT to' @('TOP') 'SelectElements'; Add-Case '14' 'SELECT TOP' @('TOP') 'SelectElements'
Add-Case '15' 'SELECT ca' @('CASE') 'SelectElements'; Add-Case '16' 'SELECT CASE w' @('WHEN') 'SelectElements'
Add-Case '17' 'SELECT CASE WHEN 1=1 t' @('THEN') 'SelectElements'; Add-Case '18' 'SELECT CASE WHEN 1=1 THEN 1 e' @('ELSE') 'SelectElements'
Add-Case '19' 'SELECT CASE WHEN 1=1 THEN 1 ELSE 0 e' @('END') 'SelectElements'
Add-Case '20' 'SELECT * FR' @('FROM') 'SelectElements'; Add-Case '21' 'SELECT a,b FR' @('FROM') 'SelectElements'
Add-Case '22' 'SELECT * IN' @('INTO') 'SelectElements'; Add-Case '23' 'SELECT k_id a' @('AS') 'SelectElements'
Add-Case '24' 'SELECT al' @('ALL') 'SelectElements'; Add-Case '25' 'SELECT TOP 10 pe' @('PERCENT') 'SelectElements'
Add-Case '26' 'SELECT * FROM t wh' @('WHERE'); Add-Case '27' 'SELECT * FROM t gr' @('GROUP BY')
Add-Case '28' 'SELECT * FROM t or' @('ORDER BY'); Add-Case '29' 'SELECT * FROM t ha' @('HAVING')
Add-Case '30' 'SELECT * FROM t le' @('LEFT JOIN'); Add-Case '31' 'SELECT * FROM t ri' @('RIGHT JOIN')
Add-Case '32' 'SELECT * FROM t in' @('INNER JOIN'); Add-Case '33' 'SELECT * FROM t fu' @('FULL JOIN')
Add-Case '34' 'SELECT * FROM t cr' @('CROSS JOIN'); Add-Case '35' 'SELECT * FROM t jo' @('JOIN')
Add-Case '36' 'SELECT * FROM t un' @('UNION'); Add-Case '37' 'SELECT * FROM t uni' @('UNION ALL')
Add-Case '38' 'SELECT * FROM t ex' @('EXCEPT'); Add-Case '39' 'SELECT * FROM dbo.T1 gr' @('GROUP BY')
Add-Case '40' 'SELECT * FROM RtBase.[dbo].[RT_YeWuKaoHe_KuCun] gr' @('GROUP BY')
Add-Case '41' 'SELECT * FROM dbo.T1 aa wh' @('WHERE')
Add-Case '42' 'SELECT * FROM dbo.gr' @() '' @('GROUP BY')
Add-Case '43' 'SELECT * FROM t lef' @('LEFT OUTER JOIN'); Add-Case '44' 'SELECT * FROM t cro' @('CROSS APPLY')
Add-Case '45' 'SELECT * FROM t ou' @('OUTER APPLY')
Add-Case '46' 'SELECT * FROM t WHERE x=1 gr' @('GROUP BY'); Add-Case '47' 'SELECT * FROM t WHERE x=1 or' @('ORDER BY')
Add-Case '48' 'SELECT * FROM t WHERE x=1 ha' @('HAVING'); Add-Case '49' 'SELECT * FROM t WHERE x=1 an' @('AND')
Add-Case '50' 'SELECT * FROM t WHERE x=1 AND y=2 or' @('ORDER BY'); Add-Case '51' 'SELECT * FROM t WHERE x IN (1,2) gr' @('GROUP BY')
Add-Case '52' 'SELECT * FROM t WHERE x BETWEEN 1 AND 2 gr' @('GROUP BY')
Add-Case '53' "SELECT * FROM t WHERE x LIKE 'a%' gr" @('GROUP BY'); Add-Case '54' 'SELECT * FROM t WHERE x IS NULL gr' @('GROUP BY')
Add-Case '55' 'SELECT * FROM t WHERE x IS NOT NULL gr' @('GROUP BY'); Add-Case '56' 'SELECT * FROM t WHERE (x=1 OR y=2) gr' @('GROUP BY')
Add-Case '57' 'SELECT * FROM t WHERE x>=10 gr' @('GROUP BY'); Add-Case '58' 'SELECT * FROM t WHERE x<>0 gr' @('GROUP BY')
Add-Case '59' 'SELECT * FROM t WHERE EXISTS(SELECT 1) an' @('AND')
Add-Case '60' 'SELECT k_id FROM RtBase.[dbo].[RT_YeWuKaoHe_KuCun] WHERE  CreateDate = 10  gr' @('GROUP BY')
Add-Case '61' 'SELECT * FROM t WHERE x=1 un' @('UNION'); Add-Case '62' 'SELECT * FROM t WHERE x=1 ex' @('EXCEPT')
Add-Case '63' 'SELECT * FROM t WHERE x i' @('IN'); Add-Case '64' 'SELECT * FROM t WHERE x l' @('LIKE')
Add-Case '65' 'SELECT * FROM t WHERE x b' @('BETWEEN')
Add-Case '66' 'SELECT a FROM t GROUP b' @('BY') 'OrderByGroupBy'; Add-Case '67' 'SELECT a FROM t ORDER b' @('BY') 'OrderByGroupBy'
Add-Case '68' 'SELECT a FROM t GROUP BY a ha' @('HAVING'); Add-Case '69' 'SELECT a FROM t GROUP BY a or' @('ORDER BY')
Add-Case '70' 'SELECT a FROM t HAVING COUNT(*)>1 or' @('ORDER BY'); Add-Case '71' 'SELECT a FROM t HAVING COUNT(*)>1 an' @('AND')
Add-Case '72' 'SELECT a FROM t ORDER BY a un' @('UNION'); Add-Case '73' 'SELECT a FROM t GROUP BY a un' @('UNION')
Add-Case '74' 'SELECT * FROM t JOIN x ON a=b an' @('AND'); Add-Case '75' 'SELECT * FROM t JOIN x ON a=b gr' @('GROUP BY')
Add-Case '76' 'INSERT in' @('INTO') 'InsertTarget'; Add-Case '77' 'INSERT INTO' @('INTO') 'InsertTarget'
Add-Case '78' 'INSERT INTO t v' @('VALUES'); Add-Case '79' 'INSERT INTO t s' @('SELECT')
Add-Case '80' 'CREATE t' @('TABLE') 'AfterCreate'; Add-Case '81' 'CREATE p' @('PROCEDURE') 'AfterCreate'
Add-Case '82' 'CREATE v' @('VIEW') 'AfterCreate'; Add-Case '83' 'ALTER t' @('TABLE') 'AfterAlter'
Add-Case '84' 'ALTER p' @('PROCEDURE') 'AfterAlter'; Add-Case '85' 'DELETE' @('DELETE') 'BatchStart'
Add-Case '86' 'WITH c' @('CREATE') 'BatchStart'; Add-Case '87' 'MERGE m' @('MERGE') 'BatchStart'
Add-Case '88' 'TRUNCATE t' @('TRUNCATE') 'BatchStart'; Add-Case '89' 'SELECT * FROM t cc where cc.x=1 a' @('AND')
Add-Case '90' 'SELECT * FROM t WHERE x=1 an' @('AND'); Add-Case '91' 'SELECT * FROM t WHERE n' @('NOT')
Add-Case '92' 'SELECT * FROM t WHERE x i' @('IS'); Add-Case '93' 'SELECT * FROM t WHERE x IS n' @('NULL')
Add-Case '94' 'SELECT * FROM t WHERE CASE WHEN 1=1 THEN 1 e' @('ELSE')
Add-Case '95' 'SELECT * FROM a JOIN b ON a.id=b.id JOIN c ON b.id=c.id wh' @('WHERE')
Add-Case '96' 'SELECT * FROM t WHERE x=(SELECT MAX(y) FROM u) gr' @('GROUP BY')
Add-Case '97' "SELECT * FROM t WHERE name='hi' gr" @('GROUP BY'); Add-Case '98' 'SELECT * FROM t WHERE price=1.5 gr' @('GROUP BY')
Add-Case '99' 'SELECT * FROM t WHERE x=1 /*c*/ gr' @('GROUP BY')
Add-Case '100' ("SELECT * FROM t WHERE x=1" + [char]10 + "gr") @('GROUP BY')
# 内建函数：SELECT / WHERE / HAVING / SET
Add-Case '101' 'SELECT isn' @('ISNULL') 'SelectElements'
Add-Case '102' 'SELECT nulli' @('NULLIF') 'SelectElements'
Add-Case '103' 'SELECT try_c' @('TRY_CAST') 'SelectElements'
Add-Case '104' 'SELECT iif' @('IIF') 'SelectElements'
Add-Case '105' 'SELECT trim' @('TRIM') 'SelectElements'
Add-Case '106' 'SELECT eom' @('EOMONTH') 'SelectElements'
Add-Case '107' 'SELECT * FROM t WHERE isn' @('ISNULL') 'WhereClause'
Add-Case '108' 'SELECT * FROM t HAVING isn' @('ISNULL') 'WhereClause'
Add-Case '109' 'UPDATE t SET x=coa' @('COALESCE') 'UpdateSet'
Add-Case '110' 'SELECT a FROM t GROUP BY isn' @('ISNULL') 'OrderByGroupBy'
Add-Case '111' 'SELECT getd' @('GETDATE') 'SelectElements'
Add-Case '112' 'SELECT json_v' @('JSON_VALUE') 'SelectElements'
Add-Case '113' 'SELECT abs' @('ABS') 'SelectElements'
Add-Case '114' 'SELECT datedi' @('DATEDIFF') 'SelectElements'
Add-Case '115' 'SELECT * FROM t WHERE try_con' @('TRY_CONVERT') 'WhereClause'

Write-Host "Cases=$($cases.Count)"
$fail = New-Object System.Collections.Generic.List[string]
$pass = 0
foreach ($c in $cases) {
    $e = [Activator]::CreateInstance($engineType)
    $s = [Activator]::CreateInstance($settingsType)
    $s.includeKeywords = $true
    try {
        $r = $get.Invoke($e, @($c.Sql, $c.Sql.Length, $null, $s, $null))
    } catch {
        [void]$fail.Add("$($c.Name) EX=$($_.Exception.Message)")
        continue
    }
    $n = New-Object System.Collections.Generic.List[string]
    if ($r.Items) { foreach ($it in $r.Items) { [void]$n.Add([string]$it.DisplayText) } }
    $ok = $true
    $why = ''
    if ($c.Ctx -and $r.Context.ToString() -ne $c.Ctx) { $ok = $false; $why = "ctx=$($r.Context)" }
    foreach ($exp in $c.Expect) {
        if ($exp -and -not $n.Contains($exp)) {
            $ok = $false
            $top = ($n | Select-Object -First 8) -join ','
            $why = "miss $exp top=[$top]"
        }
    }
    foreach ($mn in $c.MustNot) {
        if ($mn -and $n.Contains($mn)) { $ok = $false; $why = "has $mn" }
    }
    if ($ok) { $pass++ } else { [void]$fail.Add("$($c.Name) $why prefix=[$($r.Prefix)]") }
}
Write-Host "PASS=$pass FAIL=$($fail.Count)"
$fail | ForEach-Object { Write-Host "  $_" }
if ($fail.Count -gt 0) { exit 1 }
Write-Host 'ALL 100 PASS'
exit 0
