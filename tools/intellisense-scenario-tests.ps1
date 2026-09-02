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
# IP 链接服务器：替换整段 192. 而不是叠成 192.192.168...；dbo. 仍只替换点后
Add-Case '154' 'SELECT * FROM 192.' @() 'FromClause'
Add-Case '155' 'SELECT * FROM 192.168.' @() 'FromClause'
Add-Case '156' 'SELECT * FROM dbo.' @() ''
# db.. 只插入表名，勿变成 newdaku..dbo.table
Add-Case '157' 'SELECT * FROM RtBase..' @('kucun') 'FromClause' @('dbo.kucun')
Add-Case '158' 'SELECT * FROM RtBase...' @() 'FromClause' @('kucun','dbo.kucun')
Add-Case '159' 'SELECT * FROM RtBase....' @() 'FromClause' @('kucun','dbo.kucun')
# 跨服务器 server.db..table：列补全走链接服务器缓存，勿在当前库找表
Add-Case '160' 'SELECT * FROM [192.168.1.23].baoxiao..BaoXiao aa where aa.|' @('id') 'MemberAccess'
Add-Case '161' 'SELECT * FROM [192.168.1.23].baoxiao..BaoXiao  where |' @('id') 'WhereClause'
Add-Case '162' 'SELECT * FROM [192.168.1.23].baoxiao..BaoXiao  where id|' @('id') 'WhereClause'
# 上一句无分号时行首 ex → EXEC（勿被 EXCEPT 续写抢走）
Add-Case '164' ("SELECT * FROM t where x=1" + [char]10 + [char]10 + "ex") @('EXEC') 'BatchStart'
Add-Case '165' ("SELECT * FROM t" + [char]10 + "ex") @('EXEC') 'BatchStart'
# in 仍续写 INNER JOIN，勿因 EXEC 特例把 INSERT 抢过来
Add-Case '166' ("SELECT * FROM t" + [char]10 + "in") @('INNER JOIN') 'FromClause'
# 片段包含匹配：ss → ssf（前缀）+ sess（包含）
Add-Case '167' 'ss' @('ssf','sess') 'BatchStart'
# 字符串/注释内不出补全
Add-Case '168' "SELECT * FROM t WHERE x='b|" @() '' @('BETWEEN','BY','Beizhu')
Add-Case '169' "SELECT * FROM t WHERE x=N'b|" @() '' @('BETWEEN','BY')
Add-Case '170' "SELECT * FROM t -- b|" @() '' @('BETWEEN','BY')
Add-Case '171' "SELECT * FROM t /* b|" @() '' @('BETWEEN','BY')
# USE 未执行：按脚本当前库补全（连接仍是 master）
Add-Case '172' ("USE  rt_storage" + [char]10 + [char]10 + "SELECT * FROM DB_|") @('DB_DiaoBoItem') 'FromClause'
Add-Case '173' ("USE [rt_storage]" + [char]10 + [char]10 + "SELECT * FROM DB_|") @('DB_DiaoBoItem') 'FromClause'
# 前面还有跨库 SELECT 时，USE 仍作用于后面的裸表名
Add-Case '176' ("USE  rt_storage" + [char]10 + "SELECT * FROM jichushuju.dbo.t_products" + [char]10 + "SELECT * FROM DB_|") @('DB_DiaoBoItem') 'FromClause'
# 跨库三段名：前一个 JOIN 已有 ON 时仍要补全后续库.表
Add-Case '178' 'SELECT * FROM t a INNER JOIN u b ON a.id=b.id LEFT JOIN newdaku.dbo.db_book|' @('db_bookinfo_Base') 'FromClause'
# 多个 JOIN 后 WHERE 别名.列：k/c 不能因为前面的 ON 丢别名
Add-Case '181' ("USE rt_storage" + [char]10 + "SELECT a.x FROM DB_DiaoBoItem (nolock) a INNER JOIN DB_YeWuLiuZhuan (nolock) b ON a.x=b.x INNER JOIN BK_KuFang (nolock) k ON b.x=k.k_id where k.|") @('k_id') 'MemberAccess' @('PrimaryCode')
Add-Case '182' 'SELECT a.x FROM t a INNER JOIN u b ON a.x=b.x LEFT JOIN newdaku.dbo.db_bookinfo_Base (nolock) c ON a.x=c.H_ID where c.|' @('DingJia') 'MemberAccess'
# 超大脚本（无 GO）只解析当前语句：前面堆几千行后 where k. 仍要出列
$pad = New-Object System.Text.StringBuilder
for ($i = 0; $i -lt 2500; $i++) { [void]$pad.AppendLine('SELECT 1') }
Add-Case '183' ("USE rt_storage`n" + $pad.ToString() + "SELECT a.x FROM DB_DiaoBoItem (nolock) a INNER JOIN BK_KuFang (nolock) k ON a.x=k.k_id where k.|") @('k_id') 'MemberAccess' @('PrimaryCode')
# 大脚本切片后仍能从前文捞 #临时表 / 表变量 / 变量
Add-Case '185' ("CREATE TABLE #tmp (id int, name nvarchar(50))" + [char]10 + $pad.ToString() + "SELECT * FROM #tmp t WHERE t.|") @('id','name') 'MemberAccess'
Add-Case '186' ("DECLARE @tv TABLE (x int, y int)" + [char]10 + $pad.ToString() + "SELECT * FROM @tv t WHERE t.|") @('x','y') 'MemberAccess'
Add-Case '187' ("DECLARE @foo int" + [char]10 + $pad.ToString() + "SELECT @|") @('@foo') 'LocalVariable'
Add-Case '189' ("CREATE TABLE #tmp (id int)" + [char]10 + $pad.ToString() + "SELECT * FROM #|") @('#tmp') 'FromClause'
# 前面未闭合 /* 时，不能把注释里的 SQL 当代码补全
Add-Case '188' ("SELECT 1`n/*`n" + $pad.ToString() + "SELECT * FROM t WHERE an|") @() '' @('AND')

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

$linkedCatalog = [Activator]::CreateInstance($catalogType)
$linkedCatalog.Database = 'baoxiao'
$baoXiao = [Activator]::CreateInstance($tableType)
$baoXiao.Schema = 'dbo'
$baoXiao.Name = 'BaoXiao'
foreach ($cn in @('id','name')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'int'
    [void]$baoXiao.Columns.Add($col)
}
[void]$linkedCatalog.Tables.Add($baoXiao)
$svcType = $asm.GetType('AxialSqlTools.IntelliSense.MetadataCatalogService')
$svc = $svcType.GetProperty('Instance').GetValue($null)
$svc.PutCatalog('192.168.1.23', 'baoxiao', $linkedCatalog)
$connType = $asm.GetType('AxialSqlTools.ScriptFactoryAccess+ConnectionInfo')
$linkedConn = [Activator]::CreateInstance($connType)
[void]$connType.GetProperty('ServerName').SetValue($linkedConn, 'local')
[void]$connType.GetProperty('Database').SetValue($linkedConn, 'RtBase')

$storageCatalog = [Activator]::CreateInstance($catalogType)
$storageCatalog.Database = 'rt_storage'
$diao = [Activator]::CreateInstance($tableType)
$diao.Schema = 'dbo'
$diao.Name = 'DB_DiaoBoItem'
foreach ($cn in @('PrimaryCode','H_ID','ShuLiang')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'int'
    [void]$diao.Columns.Add($col)
}
[void]$storageCatalog.Tables.Add($diao)
$kufang = [Activator]::CreateInstance($tableType)
$kufang.Schema = 'dbo'
$kufang.Name = 'BK_KuFang'
foreach ($cn in @('k_id')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'int'
    [void]$kufang.Columns.Add($col)
}
[void]$storageCatalog.Tables.Add($kufang)
$svc.PutCatalog('local', 'rt_storage', $storageCatalog)

$masterCatalog = [Activator]::CreateInstance($catalogType)
$masterCatalog.Database = 'master'
$bak = [Activator]::CreateInstance($tableType)
$bak.Schema = 'dbo'
$bak.Name = 't_Product_ShuXingValues_bak_202607281153'
foreach ($cn in @('id')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'int'
    [void]$bak.Columns.Add($col)
}
[void]$masterCatalog.Tables.Add($bak)
$svc.PutCatalog('local', 'master', $masterCatalog)

$rtbaseCatalog = [Activator]::CreateInstance($catalogType)
$rtbaseCatalog.Database = 'rtbase'
$userInfo = [Activator]::CreateInstance($tableType)
$userInfo.Schema = 'dbo'
$userInfo.Name = 'UserInfo'
foreach ($cn in @('id','单位名称','单位代码')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'nvarchar'
    [void]$userInfo.Columns.Add($col)
}
[void]$rtbaseCatalog.Tables.Add($userInfo)
$svc.PutCatalog('local', 'rtbase', $rtbaseCatalog)

$newdakuCatalog = [Activator]::CreateInstance($catalogType)
$newdakuCatalog.Database = 'newdaku'
$book = [Activator]::CreateInstance($tableType)
$book.Schema = 'dbo'
$book.Name = 'db_bookinfo_Base'
foreach ($cn in @('H_ID','DingJia')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'int'
    [void]$book.Columns.Add($col)
}
[void]$newdakuCatalog.Tables.Add($book)
$svc.PutCatalog('local', 'newdaku', $newdakuCatalog)

$masterConn = [Activator]::CreateInstance($connType)
[void]$connType.GetProperty('ServerName').SetValue($masterConn, 'local')
[void]$connType.GetProperty('Database').SetValue($masterConn, 'master')

$fail = New-Object System.Collections.Generic.List[string]
$pass = 0
$linkedCases = @('160','161','162')
$useCases = @('172','173','176','178','181','182','183')
foreach ($c in $cases) {
    $e = [Activator]::CreateInstance($engineType)
    $s = [Activator]::CreateInstance($settingsType)
    $s.includeKeywords = $true
    $useCatalog = $c.Name -in @('141','142','143','144','145','146','147','148','149','150','151','152','153','157','158','159')
    $cat = if ($useCatalog) { $mockCatalog } else { $null }
    $conn = $null
    if ($c.Name -in $linkedCases) {
        $cat = $mockCatalog
        $conn = $linkedConn
    }
    if ($c.Name -in $useCases) {
        $cat = $masterCatalog
        $conn = $masterConn
    }
    try {
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $r = $get.Invoke($e, @($c.Sql, $c.Caret, $cat, $s, $conn))
        $sw.Stop()
        if ($c.Name -eq '183') { Write-Host ("183 GetCompletion large-script {0:F1}ms sqlLen=$($c.Sql.Length)" -f $sw.Elapsed.TotalMilliseconds) }
    } catch {
        [void]$fail.Add("$($c.Name) EX=$($_.Exception.Message)")
        continue
    }
    $n = New-Object System.Collections.Generic.List[string]
    $kinds = New-Object System.Collections.Generic.List[string]
    $allInsert = $null
    $kucunInsert = $null
    if ($r.Items) {
        foreach ($it in $r.Items) {
            [void]$n.Add([string]$it.DisplayText)
            [void]$kinds.Add([string]$it.Kind)
            if ([string]$it.Kind -eq 'AllColumns') { $allInsert = [string]$it.InsertText }
            if ([string]$it.DisplayText -eq 'kucun' -or [string]$it.DisplayText -eq 'dbo.kucun') {
                $kucunInsert = [string]$it.InsertText
            }
        }
    }
    $ok = $true
    $why = ''
    if ($c.Ctx -and $r.Context.ToString() -ne $c.Ctx) { $ok = $false; $why = "ctx=$($r.Context)" }
    foreach ($exp in $c.Expect) {
        $hit = $false
        if ($exp) {
            $hit = $n.Contains($exp)
            if (-not $hit) {
                foreach ($disp in $n) {
                    if ($disp -eq $exp -or $disp.EndsWith(".$exp")) { $hit = $true; break }
                }
            }
        }
        if ($exp -and -not $hit) {
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
    if ($c.Name -in @('154','155')) {
        $expectStart = $c.Sql.IndexOf('192')
        if ($r.ReplaceStartOffset -ne $expectStart) {
            $ok = $false
            $why = "replace=$($r.ReplaceStartOffset) want=$expectStart"
        }
    }
    if ($c.Name -eq '156') {
        $expectStart = $c.Sql.Length
        if ($r.ReplaceStartOffset -ne $expectStart) {
            $ok = $false
            $why = "replace=$($r.ReplaceStartOffset) want=$expectStart (dbo. should keep prefix)"
        }
    }
    if ($ok) { $pass++ } else { [void]$fail.Add("$($c.Name) $why prefix=[$($r.Prefix)]") }
}

$qiType = $asm.GetType('AxialSqlTools.IntelliSense.QuickInfoProvider')
$qiSql = 'SELECT * FROM [192.168.1.23].baoxiao..BaoXiao aa where aa.id = 1'
$qiHover = $qiSql.IndexOf('BaoXiao') + 2
$qi = [Activator]::CreateInstance($qiType)
$qiInfo = $qi.GetQuickInfo($qiSql, $qiHover, $mockCatalog, $linkedConn)
$qiOk = $false
$qiWhy = 'null'
if ($null -ne $qiInfo) {
    $headers = if ($qiInfo.HeaderLines) { ($qiInfo.HeaderLines -join ' | ') } else { '' }
    $ddl = [string]$qiInfo.DdlText
    if ($headers -match 'BaoXiao' -and $headers -match '192\.168\.1\.23' -and $ddl -match 'id') { $qiOk = $true }
    else { $qiWhy = "headers=[$headers] ddl=$ddl" }
}
if ($qiOk) { $pass++ } else { [void]$fail.Add("163 QuickInfo $qiWhy") }

$qiSql2 = "SELECT a.x FROM t a LEFT JOIN rtbase.dbo.UserInfo (nolock) d ON a.x = d.id"
$qiHover2 = $qiSql2.LastIndexOf('id')
$qi2 = [Activator]::CreateInstance($qiType)
$qiInfo2 = $qi2.GetQuickInfo($qiSql2, $qiHover2, $masterCatalog, $masterConn)
$qi2Ok = $false
$qi2Why = 'null'
if ($null -ne $qiInfo2) {
    $headers2 = if ($qiInfo2.HeaderLines) { ($qiInfo2.HeaderLines -join ' | ') } else { '' }
    if ($headers2 -match 'UserInfo' -and $headers2 -notmatch 't_Product_ShuXingValues') { $qi2Ok = $true }
    else { $qi2Why = "headers=[$headers2]" }
} else { $qi2Why = 'null (alias d.id should be UserInfo)' }
if ($qi2Ok) { $pass++ } else { [void]$fail.Add("174 QuickInfo alias $qi2Why") }

$qiSql3 = "USE rt_storage`nSELECT * FROM DB_DiaoBoItem"
$qiHover3 = $qiSql3.IndexOf('DB_DiaoBoItem') + 3
$qi3 = [Activator]::CreateInstance($qiType)
$qiInfo3 = $qi3.GetQuickInfo($qiSql3, $qiHover3, $masterCatalog, $masterConn)
$qi3Ok = $false
$qi3Why = 'null'
if ($null -ne $qiInfo3) {
    $headers3 = if ($qiInfo3.HeaderLines) { ($qiInfo3.HeaderLines -join ' | ') } else { '' }
    if ($headers3 -match 'DB_DiaoBoItem' -and $headers3 -match 'rt_storage') { $qi3Ok = $true }
    else { $qi3Why = "headers=[$headers3]" }
}
if ($qi3Ok) { $pass++ } else { [void]$fail.Add("175 QuickInfo USE $qi3Why") }

$qiSql4 = "USE rt_storage`nSELECT * FROM jichushuju.dbo.t_products`nSELECT a.x FROM DB_DiaoBoItem (nolock) a LEFT JOIN rtbase.dbo.UserInfo (nolock) d ON a.x = d.id"
$qiHover4 = $qiSql4.LastIndexOf('id')
$qi4 = [Activator]::CreateInstance($qiType)
$qiInfo4 = $qi4.GetQuickInfo($qiSql4, $qiHover4, $masterCatalog, $masterConn)
$qi4Ok = $false
$qi4Why = 'null'
if ($null -ne $qiInfo4) {
    $headers4 = if ($qiInfo4.HeaderLines) { ($qiInfo4.HeaderLines -join ' | ') } else { '' }
    if ($headers4 -match 'UserInfo' -and $headers4 -notmatch 't_Product_ShuXingValues' -and $headers4 -notmatch 't_products') { $qi4Ok = $true }
    else { $qi4Why = "headers=[$headers4]" }
} else { $qi4Why = 'null (two-stmt d.id should be UserInfo)' }
if ($qi4Ok) { $pass++ } else { [void]$fail.Add("177 QuickInfo two-stmt alias $qi4Why") }

$qiSql5 = "SELECT a.x FROM t a INNER JOIN u b ON a.x=b.x LEFT JOIN rtbase.dbo.UserInfo (nolock) d ON a.x = d.id"
$qiHover5 = $qiSql5.IndexOf('UserInfo') + 2
$qi5 = [Activator]::CreateInstance($qiType)
$qiInfo5 = $qi5.GetQuickInfo($qiSql5, $qiHover5, $masterCatalog, $masterConn)
$qi5Ok = $false
$qi5Why = 'null'
if ($null -ne $qiInfo5) {
    $headers5 = if ($qiInfo5.HeaderLines) { ($qiInfo5.HeaderLines -join ' | ') } else { '' }
    if ($headers5 -match 'UserInfo' -and $headers5 -match 'rtbase') { $qi5Ok = $true }
    else { $qi5Why = "headers=[$headers5]" }
} else { $qi5Why = 'null (JOIN after ON should still resolve UserInfo)' }
if ($qi5Ok) { $pass++ } else { [void]$fail.Add("179 QuickInfo later JOIN UserInfo $qi5Why") }

$qiSql6 = "SELECT a.x FROM t a INNER JOIN u b ON a.x=b.x LEFT JOIN newdaku.dbo.db_bookinfo_Base (nolock) c ON a.x = c.H_ID"
$qiHover6 = $qiSql6.IndexOf('db_bookinfo_Base') + 3
$qi6 = [Activator]::CreateInstance($qiType)
$qiInfo6 = $qi6.GetQuickInfo($qiSql6, $qiHover6, $masterCatalog, $masterConn)
$qi6Ok = $false
$qi6Why = 'null'
if ($null -ne $qiInfo6) {
    $headers6 = if ($qiInfo6.HeaderLines) { ($qiInfo6.HeaderLines -join ' | ') } else { '' }
    if ($headers6 -match 'db_bookinfo_Base' -and $headers6 -match 'newdaku') { $qi6Ok = $true }
    else { $qi6Why = "headers=[$headers6]" }
} else { $qi6Why = 'null (JOIN after ON should still resolve db_bookinfo_Base)' }
if ($qi6Ok) { $pass++ } else { [void]$fail.Add("180 QuickInfo later JOIN book $qi6Why") }

$qiSql7 = ("USE rt_storage`n" + $pad.ToString() + "SELECT a.x FROM DB_DiaoBoItem (nolock) a INNER JOIN BK_KuFang (nolock) k ON a.x=k.k_id")
$qiHover7 = $qiSql7.LastIndexOf('BK_KuFang') + 3
$qi7 = [Activator]::CreateInstance($qiType)
$swQi = [Diagnostics.Stopwatch]::StartNew()
$qiInfo7 = $qi7.GetQuickInfo($qiSql7, $qiHover7, $masterCatalog, $masterConn)
$swQi.Stop()
Write-Host ("183 QuickInfo large-script {0:F1}ms" -f $swQi.Elapsed.TotalMilliseconds)
$qi7Ok = $false
$qi7Why = 'null'
if ($null -ne $qiInfo7) {
    $headers7 = if ($qiInfo7.HeaderLines) { ($qiInfo7.HeaderLines -join ' | ') } else { '' }
    if ($headers7 -match 'BK_KuFang' -and $headers7 -match 'rt_storage') { $qi7Ok = $true }
    else { $qi7Why = "headers=[$headers7]" }
} else { $qi7Why = 'null (large script should still resolve last JOIN table)' }
if ($qi7Ok) { $pass++ } else { [void]$fail.Add("184 QuickInfo large-script $qi7Why") }

Write-Host "PASS=$pass FAIL=$($fail.Count)"
$fail | ForEach-Object { Write-Host "  $_" }
if ($fail.Count -gt 0) { exit 1 }
Write-Host "ALL $pass PASS"
exit 0
