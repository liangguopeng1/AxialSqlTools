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
    $caret = $sql.Length
    $marker = $sql.IndexOf('|')
    if ($marker -ge 0) {
        $caret = $marker
        $sql = $sql.Remove($marker, 1)
    }
    [void]$script:cases.Add([pscustomobject]@{ Name = $name; Sql = $sql; Expect = $expect; Ctx = $ctx; MustNot = $mustNot; Caret = $caret })
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
# 行首 in/ord：跨行表别名后仍提示 JOIN / ORDER BY
Add-Case '116' ("SELECT aa.* FROM huizong.dbo.tab_dingdan aa" + [char]10 + "in") @('INNER JOIN') 'FromClause'
Add-Case '117' ("SELECT aa.* FROM huizong.dbo.tab_dingdan aa" + [char]10 + "where aa.x = 1" + [char]10 + "ord") @('ORDER BY') 'WhereClause'
Add-Case '118' ("SELECT * FROM t aa" + [char]10 + "le") @('LEFT JOIN') 'FromClause'
Add-Case '119' ("SELECT * FROM t" + [char]10 + "where x=1 and y=2" + [char]10 + "ord") @('ORDER BY') 'WhereClause'
# 多表 JOIN ON 后再写 in → INNER JOIN（勿落成 WhereClause 的 IN）
Add-Case '120' ("SELECT bb.* FROM a aa" + [char]10 + "inner join b bb on aa.id = bb.id" + [char]10 + "in") @('INNER JOIN') 'FromClause'
Add-Case '121' 'SELECT * FROM a aa INNER JOIN b bb ON aa.id=bb.id in' @('INNER JOIN') 'FromClause'
Add-Case '122' ("SELECT * FROM a aa INNER JOIN b bb ON aa.id=bb.id and aa.x=1" + [char]10 + "in") @('INNER JOIN') 'FromClause'
Add-Case '123' ("SELECT * FROM a aa INNER JOIN b bb ON aa.id=bb.id" + [char]10 + "wh") @('WHERE') 'FromClause'
# 无分号时行首新开 SELECT（勿被 ORDER BY 上下文 + SESSION_USER 抢走）
Add-Case '124' ("SELECT * FROM t" + [char]10 + "order by id" + [char]10 + [char]10 + "se") @('SELECT') 'BatchStart'
Add-Case '125' ("SELECT * FROM t where x=1" + [char]10 + [char]10 + "se") @('SELECT') 'BatchStart'
Add-Case '126' ("SELECT * FROM t" + [char]10 + "order by id" + [char]10 + "ord") @('ORDER BY') 'OrderByGroupBy'
# 无分号 ORDER BY 后行首 ss → 片段 ssf（勿掉进 OrderByGroupBy）
Add-Case '127' ("SELECT * FROM t" + [char]10 + "order by id" + [char]10 + [char]10 + "ss") @('ssf') 'BatchStart'
# JOIN ON 多条件 and alias. → 列（MemberAccess）
Add-Case '128' 'SELECT * FROM a aa INNER JOIN b bb ON aa.id = bb.id and aa.' @() 'MemberAccess'
Add-Case '129' ("SELECT * FROM a aa" + [char]10 + "INNER JOIN b bb ON aa.id = bb.id and bb.") @() 'MemberAccess'
Add-Case '130' 'SELECT * FROM a aa INNER JOIN b bb ON aa.id = bb.id and aa.x = 1 in' @('INNER JOIN') 'FromClause'
# 仅 FROM 后无 WHERE/ORDER：行首 ss/se 开新句；in/wh 仍续写
Add-Case '131' ("SELECT * FROM rt_fenjian..FJ_Daitui_Items" + [char]10 + [char]10 + "ss") @('ssf') 'BatchStart'
Add-Case '132' ("SELECT * FROM t" + [char]10 + [char]10 + "se") @('SELECT') 'BatchStart'
Add-Case '133' ("SELECT * FROM t" + [char]10 + "in") @('INNER JOIN') 'FromClause'
Add-Case '134' ("SELECT * FROM t" + [char]10 + "wh") @('WHERE') 'FromClause'
Add-Case '135' ("SELECT * FROM a" + [char]10 + "inner join b on a.id=b.id" + [char]10 + [char]10 + "ss") @('ssf') 'BatchStart'
Add-Case '136' ("SELECT * FROM t" + [char]10 + [char]10 + "up") @('UPDATE') 'BatchStart'
# 上一句无分号时，新 SELECT * fro → FROM，勿串上一句列
Add-Case '137' ("SELECT * FROM t where x=1 order by x" + [char]10 + [char]10 + "SELECT * fro") @('FROM') 'SelectElements'
Add-Case '138' ("SELECT a.F_PrimaryCode FROM dbo.CGD_NeiPei_Items a" + [char]10 + [char]10 + "SELECT * fro") @('FROM') 'SelectElements'
Add-Case '139' ("SELECT * FROM t where x=1;" + [char]10 + "SELECT * fro") @('FROM') 'SelectElements'
Add-Case '140' 'SELECT * fro' @('FROM') 'SelectElements'
# 全字段（AllColumns）：需注入元数据目录；| 表示光标
Add-Case '141' 'SELECT | FROM dbo.DemoT' @() 'SelectElements'
Add-Case '142' 'INSERT INTO dbo.DemoT (|' @() 'InsertColumnList'
Add-Case '143' 'INSERT INTO dbo.DemoT (id, |' @() 'InsertColumnList'
Add-Case '144' 'INSERT INTO dbo.DemoT VALUES (|' @() 'InsertTarget'
# UPDATE SET：目标表列（无 FROM 时也要提示；跨库三段名；同标签上一句 SELECT 不得抢走别名）
Add-Case '145' 'UPDATE dbo.kucun SET ope|' @('oper') 'UpdateSet'
Add-Case '146' 'UPDATE rt_kucun.dbo.kucun SET ope|' @('oper') 'UpdateSet'
Add-Case '147' 'UPDATE rt_kucun.dbo.kucun SET oper where operid = ''x''|' @('operid') 'WhereClause'
Add-Case '148' ("SELECT * FROM rt_fenjian.dbo.FJ_Items where DD_Item_ID = 1" + [char]10 + [char]10 + "UPDATE  rt_kucun.dbo.kucun set ope|") @('oper') 'UpdateSet'
Add-Case '149' ("247965692" + [char]10 + "SELECT * FROM rt_fenjian.dbo.FJ_Items_Status" + [char]10 + [char]10 + "UPDATE  rt_kucun.dbo.kucun set ope| where operid = '202410111844658620279545858'") @('oper') 'UpdateSet'
# UPDATE 目标：库/表名，勿把 kucun. 当成列访问
Add-Case '150' 'UPDATE kuc|' @('dbo.kucun') 'UpdateTarget' @('oper')
Add-Case '151' 'UPDATE |' @('dbo.kucun') 'UpdateTarget' @('oper')
Add-Case '152' 'UPDATE kucun.|' @() 'UpdateTarget' @('oper')
Add-Case '153' 'UPDATE dbo.kuc|' @('dbo.kucun') 'UpdateTarget' @('oper')

Write-Host "Cases=$($cases.Count)"

# 供全字段用例使用的假目录
$catalogType = $asm.GetType('AxialSqlTools.IntelliSense.MetadataCatalog')
$tableType = $asm.GetType('AxialSqlTools.IntelliSense.TableColumnInfo')
$colType = $asm.GetType('AxialSqlTools.IntelliSense.ColumnInfo')
$mockCatalog = [Activator]::CreateInstance($catalogType)
$mockCatalog.Database = 'RtBase'
$demo = [Activator]::CreateInstance($tableType)
$demo.Schema = 'dbo'
$demo.Name = 'DemoT'
foreach ($cn in @('id','k_id','h_id','CreateRen')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'int'
    [void]$demo.Columns.Add($col)
}
[void]$mockCatalog.Tables.Add($demo)
$kucun = [Activator]::CreateInstance($tableType)
$kucun.Schema = 'dbo'
$kucun.Name = 'kucun'
foreach ($cn in @('oper','operid','k_id')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'nvarchar'
    [void]$kucun.Columns.Add($col)
}
[void]$mockCatalog.Tables.Add($kucun)

$fail = New-Object System.Collections.Generic.List[string]
$pass = 0
foreach ($c in $cases) {
    $e = [Activator]::CreateInstance($engineType)
    $s = [Activator]::CreateInstance($settingsType)
    $s.includeKeywords = $true
    $useCatalog = $c.Name -in @('141','142','143','144','145','146','147','148','149','150','151','152','153')
    $cat = if ($useCatalog) { $mockCatalog } else { $null }
    try {
        $r = $get.Invoke($e, @($c.Sql, $c.Caret, $cat, $s, $null))
    } catch {
        [void]$fail.Add("$($c.Name) EX=$($_.Exception.Message)")
        continue
    }
    $n = New-Object System.Collections.Generic.List[string]
    $kinds = New-Object System.Collections.Generic.List[string]
    $allInsert = $null
    if ($r.Items) {
        foreach ($it in $r.Items) {
            [void]$n.Add([string]$it.DisplayText)
            [void]$kinds.Add([string]$it.Kind)
            if ([string]$it.Kind -eq 'AllColumns') { $allInsert = [string]$it.InsertText }
        }
    }
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
    if ($c.Name -in @('141','142','143')) {
        if (-not $kinds.Contains('AllColumns')) {
            $ok = $false
            $why = "miss AllColumns kinds=[$($kinds | Select-Object -First 10)]"
        } elseif ($allInsert -notmatch 'id' -or $allInsert -notmatch 'k_id' -or $allInsert -notmatch 'CreateRen') {
            $ok = $false
            $why = "AllColumns insert incomplete: $allInsert"
        }
    }
    if ($c.Name -eq '144' -and $kinds.Contains('AllColumns')) {
        $ok = $false
        $why = 'VALUES should not have AllColumns'
    }
    if ($ok) { $pass++ } else { [void]$fail.Add("$($c.Name) $why prefix=[$($r.Prefix)]") }
}
Write-Host "PASS=$pass FAIL=$($fail.Count)"
$fail | ForEach-Object { Write-Host "  $_" }
if ($fail.Count -gt 0) { exit 1 }
Write-Host "ALL $($cases.Count) PASS"
exit 0
