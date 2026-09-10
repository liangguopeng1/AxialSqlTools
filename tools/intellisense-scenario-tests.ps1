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
function Add-Case([string]$name, [string]$sql, [string[]]$expect, [string]$ctx = '', [string[]]$mustNot = @(), [string]$Catalog = '') {
    $caret = $sql.Length
    $marker = $sql.IndexOf('|')
    if ($marker -ge 0) {
        $caret = $marker
        $sql = $sql.Remove($marker, 1)
    }
    # Catalog: ''=none; 'mock'=mockCatalog; 'linked'=mockCatalog+linked conn; 'use'=masterCatalog+master conn; 'emptysys'=已加载标志但无 sys 对象; 'sharesys'=库目录无 sys，走实例级共用目录.
    # Catalog requirement is inline with each case, avoiding drift from scattered lists.
    [void]$script:cases.Add([pscustomobject]@{ Name = $name; Sql = $sql; Expect = $expect; Ctx = $ctx; MustNot = $mustNot; Caret = $caret; Catalog = $Catalog })
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
Add-Case '22' 'SELECT * IN' @('INTO') 'SelectElements'; Add-Case '23' 'SELECT k_id a' @('AS') 'SelectAlias' @('FROM','INTO')
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
Add-Case '212' 'SELECT * FROM kucun_zong AS KC INNER JOIN BK_JiaWei AS JW ON JW.j_id = KC.j_id AND JW.Type = 1 AND KC|' @('KC') 'WhereClause' -Catalog 'use'
Add-Case '216' 'SELECT * FROM t AS KCZ INNER JOIN GongYingShang AS GHS ON GHS.Id = KCZ.ghs_id INNER JOIN db_bookInfo_Base AS BOOK ON BOOK.h_id = KCZ.h_id and g|' @('GHS') 'WhereClause'
Add-Case '213' 'SELECT * FROM (SELECT isnull((select 1 from rt_storage.dbo.TH_TuiGHS_ShenQing_Item where Y_GHSID = KC.|),0) as c FROM rt_storage.dbo.kucun_zong AS KC) AS KCZ' @('stock','h_id') 'MemberAccess' @('CeShu') -Catalog 'use'
Add-Case '76' 'INSERT in' @('INTO') 'InsertTarget'; Add-Case '77' 'INSERT INTO' @('INTO') 'InsertTarget'
Add-Case '78' 'INSERT INTO t v' @('VALUES'); Add-Case '79' 'INSERT INTO t s' @('SELECT')
Add-Case '80' 'CREATE t' @('TABLE') 'AfterCreate'; Add-Case '81' 'CREATE p' @('PROCEDURE') 'AfterCreate'
Add-Case '82' 'CREATE v' @('VIEW') 'AfterCreate'; Add-Case '83' 'ALTER t' @('TABLE') 'AfterAlter'
Add-Case '84' 'ALTER p' @('PROCEDURE') 'AfterAlter'; Add-Case '85' 'DELETE' @('DELETE') 'BatchStart'
Add-Case '86' 'WITH c' @('CREATE') 'BatchStart'; Add-Case '87' 'MERGE m' @() 'DdlObjectTarget'
Add-Case '88' 'TRUNCATE t' @('TABLE') 'AfterTruncate' @('TRUNCATE')
Add-Case '89' 'SELECT * FROM t cc where cc.x=1 a' @('AND')
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
Add-Case '108' 'SELECT * FROM t HAVING isn' @('ISNULL') 'HavingClause'
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
Add-Case '141' 'SELECT | FROM dbo.DemoT' @() 'SelectElements' -Catalog 'mock'
Add-Case '142' 'INSERT INTO dbo.DemoT (|' @() 'InsertColumnList' -Catalog 'mock'
Add-Case '143' 'INSERT INTO dbo.DemoT (id, |' @() 'InsertColumnList' -Catalog 'mock'
Add-Case '144' 'INSERT INTO dbo.DemoT VALUES (|' @() 'InsertTarget' -Catalog 'mock'
# UPDATE SET：目标表列（无 FROM 时也要提示；跨库三段名；同标签上一句 SELECT 不得抢走别名）
Add-Case '145' 'UPDATE dbo.kucun SET ope|' @('oper') 'UpdateSet' -Catalog 'mock'
Add-Case '146' 'UPDATE rt_kucun.dbo.kucun SET ope|' @('oper') 'UpdateSet' -Catalog 'mock'
Add-Case '147' 'UPDATE rt_kucun.dbo.kucun SET oper where operid = ''x''|' @('operid') 'WhereClause' -Catalog 'mock'
Add-Case '148' ("SELECT * FROM rt_fenjian.dbo.FJ_Items where DD_Item_ID = 1" + [char]10 + [char]10 + "UPDATE  rt_kucun.dbo.kucun set ope|") @('oper') 'UpdateSet' -Catalog 'mock'
Add-Case '149' ("247965692" + [char]10 + "SELECT * FROM rt_fenjian.dbo.FJ_Items_Status" + [char]10 + [char]10 + "UPDATE  rt_kucun.dbo.kucun set ope| where operid = '202410111844658620279545858'") @('oper') 'UpdateSet' -Catalog 'mock'
# UPDATE 目标：库/表名，勿把 kucun. 当成列访问
Add-Case '150' 'UPDATE kuc|' @('dbo.kucun') 'UpdateTarget' @('oper') -Catalog 'mock'
Add-Case '151' 'UPDATE |' @('dbo.kucun') 'UpdateTarget' @('oper') -Catalog 'mock'
Add-Case '152' 'UPDATE kucun.|' @() 'UpdateTarget' @('oper') -Catalog 'mock'
Add-Case '153' 'UPDATE dbo.kuc|' @('dbo.kucun') 'UpdateTarget' @('oper') -Catalog 'mock'
# IP 链接服务器：替换整段 192. 而不是叠成 192.192.168...；dbo. 仍只替换点后
Add-Case '154' 'SELECT * FROM 192.' @() 'FromClause'
Add-Case '155' 'SELECT * FROM 192.168.' @() 'FromClause'
Add-Case '156' 'SELECT * FROM dbo.' @() ''
# db.. 只插入表名，勿变成 newdaku..dbo.table
Add-Case '157' 'SELECT * FROM RtBase..' @('kucun') 'FromClause' @('dbo.kucun') -Catalog 'mock'
Add-Case '158' 'SELECT * FROM RtBase...' @() 'FromClause' @('kucun','dbo.kucun') -Catalog 'mock'
Add-Case '159' 'SELECT * FROM RtBase....' @() 'FromClause' @('kucun','dbo.kucun') -Catalog 'mock'
# 架构已经写出后再打 .. 不是 db.. 省略架构，不应再出表
Add-Case '321' 'SELECT * FROM RtBase.dbo..' @() 'FromClause' @('kucun','dbo.kucun') -Catalog 'mock'
Add-Case '322' 'SELECT * FROM rt_fenjian.dbo..' @() 'FromClause' @('SS_PiCi') -Catalog 'use'
Add-Case '323' 'SELECT * FROM [192.168.1.108].jichushuju.dbo..' @() 'FromClause' @('t_products') -Catalog 'use'
# db..table 后的 WHERE 前缀（省略 dbo）应与 db.dbo.table 一样出关键字
Add-Case '332' 'SELECT * FROM rt_fenjian..FJ_Daitui_Items w' @('WHERE') 'FromClause'
Add-Case '333' 'SELECT * FROM rt_fenjian..FJ_Daitui_Items wh' @('WHERE') 'FromClause'
Add-Case '334' 'SELECT * FROM rt_fenjian.dbo.FJ_Daitui_Items w' @('WHERE') 'FromClause'
Add-Case '335' 'SELECT * FROM RtBase..kucun w' @('WHERE') 'FromClause' -Catalog 'mock'
Add-Case '324' 'SELECT * FROM RtBase.dbo.' @('kucun') 'FromClause' @('dbo.kucun') -Catalog 'mock'
# 跨服务器 server.db..table：列补全走链接服务器缓存，勿在当前库找表
Add-Case '160' 'SELECT * FROM [192.168.1.23].baoxiao..BaoXiao aa where aa.|' @('id') 'MemberAccess' -Catalog 'linked'
Add-Case '161' 'SELECT * FROM [192.168.1.23].baoxiao..BaoXiao  where |' @('id') 'WhereClause' -Catalog 'linked'
Add-Case '162' 'SELECT * FROM [192.168.1.23].baoxiao..BaoXiao  where id|' @('id') 'WhereClause' -Catalog 'linked'
# 库. 提示 dbo / dbo.表；库.dbo. 只提示表名（本地与链接服务器相同）
Add-Case '280' 'SELECT * FROM jichushuju.' @('dbo','dbo.t_products') 'FromClause' -Catalog 'use'
Add-Case '281' 'SELECT * FROM jichushuju.dbo.' @('t_products') 'FromClause' @('dbo.t_products') -Catalog 'use'
Add-Case '282' 'SELECT * FROM [192.168.1.108].jichushuju.' @('dbo','dbo.t_products') 'FromClause' -Catalog 'use'
Add-Case '283' 'SELECT * FROM [192.168.1.108].jichushuju.dbo.' @('t_products') 'FromClause' @('dbo.t_products') -Catalog 'use'
Add-Case '284' 'SELECT * FROM RtBase.' @('dbo','dbo.kucun') 'FromClause' -Catalog 'mock'
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
Add-Case '172' ("USE  rt_storage" + [char]10 + [char]10 + "SELECT * FROM DB_|") @('DB_DiaoBoItem') 'FromClause' -Catalog 'use'
Add-Case '173' ("USE [rt_storage]" + [char]10 + [char]10 + "SELECT * FROM DB_|") @('DB_DiaoBoItem') 'FromClause' -Catalog 'use'
# 前面还有跨库 SELECT 时，USE 仍作用于后面的裸表名
Add-Case '176' ("USE  rt_storage" + [char]10 + "SELECT * FROM jichushuju.dbo.t_products" + [char]10 + "SELECT * FROM DB_|") @('DB_DiaoBoItem') 'FromClause' -Catalog 'use'
# 跨库三段名：前一个 JOIN 已有 ON 时仍要补全后续库.表
Add-Case '178' 'SELECT * FROM t a INNER JOIN u b ON a.id=b.id LEFT JOIN newdaku.dbo.db_book|' @('db_bookinfo_Base') 'FromClause' -Catalog 'use'
# 多个 JOIN 后 WHERE 别名.列：k/c 不能因为前面的 ON 丢别名
Add-Case '181' ("USE rt_storage" + [char]10 + "SELECT a.x FROM DB_DiaoBoItem (nolock) a INNER JOIN DB_YeWuLiuZhuan (nolock) b ON a.x=b.x INNER JOIN BK_KuFang (nolock) k ON b.x=k.k_id where k.|") @('k_id') 'MemberAccess' @('PrimaryCode') -Catalog 'use'
Add-Case '182' 'SELECT a.x FROM t a INNER JOIN u b ON a.x=b.x LEFT JOIN newdaku.dbo.db_bookinfo_Base (nolock) c ON a.x=c.H_ID where c.|' @('DingJia') 'MemberAccess' -Catalog 'use'
# 超大脚本（无 GO）只解析当前语句：前面堆几千行后 where k. 仍要出列
$pad = New-Object System.Text.StringBuilder
for ($i = 0; $i -lt 2500; $i++) { [void]$pad.AppendLine('SELECT 1') }
Add-Case '183' ("USE rt_storage`n" + $pad.ToString() + "SELECT a.x FROM DB_DiaoBoItem (nolock) a INNER JOIN BK_KuFang (nolock) k ON a.x=k.k_id where k.|") @('k_id') 'MemberAccess' @('PrimaryCode') -Catalog 'use'
# 大脚本切片后仍能从前文捞 #临时表 / 表变量 / 变量
Add-Case '185' ("CREATE TABLE #tmp (id int, name nvarchar(50))" + [char]10 + $pad.ToString() + "SELECT * FROM #tmp t WHERE t.|") @('id','name') 'MemberAccess'
Add-Case '186' ("DECLARE @tv TABLE (x int, y int)" + [char]10 + $pad.ToString() + "SELECT * FROM @tv t WHERE t.|") @('x','y') 'MemberAccess'
Add-Case '187' ("DECLARE @foo int" + [char]10 + $pad.ToString() + "SELECT @|") @('@foo') 'LocalVariable'
Add-Case '189' ("CREATE TABLE #tmp (id int)" + [char]10 + $pad.ToString() + "SELECT * FROM #|") @('#tmp') 'FromClause'
# 前面未闭合 /* 时，不能把注释里的 SQL 当代码补全
Add-Case '188' ("SELECT 1`n/*`n" + $pad.ToString() + "SELECT * FROM t WHERE an|") @() '' @('AND')
# 派生表：外层 WHERE 提示 KCZ 不提示内层 KC；跨库 JOIN 表可悬停
$derivedSql = @"
USE master
SELECT COUNT(*) AS total
FROM (SELECT KC.h_id, KC.z_id, round(KC.cost0, 4, 1) AS cost0, sum(stock) AS stock
      FROM rt_storage.dbo.kucun_zong AS KC WITH (NOLOCK)
           INNER JOIN rt_storage.dbo.BK_JiaWei AS JW WITH (NOLOCK) ON JW.j_id = KC.j_id
      WHERE KC.status = 1
      GROUP BY KC.h_id, KC.z_id, round(KC.cost0, 4, 1)) AS KCZ
     INNER JOIN rt_storage.dbo.BK_ZhanDian AS ZD WITH (NOLOCK) ON ZD.z_id = KCZ.z_id
     INNER JOIN BK_KuFang AS KF WITH (NOLOCK) ON KF.k_id = KCZ.k_id
     INNER JOIN RtBase.dbo.GongYingShang AS GHS WITH (NOLOCK) ON GHS.Id = KCZ.ghs_id
     INNER JOIN newdaku.dbo.db_bookInfo_Base AS BOOK WITH (NOLOCK) ON BOOK.h_id = KCZ.h_id
WHERE |
"@
$derivedSqlQi = $derivedSql
Add-Case '190' ($derivedSql.Replace('WHERE |', 'WHERE KC|')) @('KCZ') 'WhereClause' @('KC','JW','kucun_zong')
Add-Case '190b' ($derivedSql.Replace('WHERE |', 'WHERE BO|')) @('BOOK') 'WhereClause' @('KC','kucun_zong')
Add-Case '190c' ($derivedSql.Replace('WHERE |', 'WHERE GHS|')) @('GHS') 'WhereClause' @('KC')
Add-Case '190d' ($derivedSql.Replace('WHERE |', 'WHERE ZD|')) @('ZD') 'WhereClause' @('KC')
Add-Case '190e' ($derivedSql.Replace('WHERE |', 'WHERE KF|')) @('KF') 'WhereClause' @('KC')
Add-Case '191' ($derivedSql.Replace('WHERE |', 'WHERE KCZ.|')) @('h_id','z_id','cost0','stock') 'MemberAccess' @('status','j_id')
Add-Case '191b' 'SELECT * FROM (SELECT sum(stock) FROM t) AS KCZ WHERE KCZ.|' @('stock') 'MemberAccess'
Add-Case '191c' 'SELECT * FROM (SELECT sum(stock) stock FROM t) AS KCZ WHERE KCZ.|' @('stock') 'MemberAccess'
Add-Case '192' 'SELECT * FROM (SELECT aa.x FROM t aa WHERE aa|) AS KCZ' @('aa') 'WhereClause' @('KCZ')
Add-Case '193' 'SELECT * FROM (SELECT a.x FROM t a) AS KCZ WHERE KC|' @('KCZ') 'WhereClause' @('a')
# 别名与表名相同：WHERE ss 仍提示 SS_PiCi，选中插入 SS_PiCi.
$piciSql = @"
select SS_PiCi.PrimaryCode
from rt_fenjian.dbo.SS_PiCi(nolock) as SS_PiCi
left outer join rt_fenjian.dbo.SS_PiCi_Second(nolock) as SS_PiCi_Second on SS_PiCi.PrimaryCode = SS_PiCi_Second.F_PrimaryCode
where ss|
"@
Add-Case '201' $piciSql @('SS_PiCi') 'WhereClause'
Add-Case '202' 'SELECT * FROM dbo.SS_PiCi AS aa where ss|' @() 'WhereClause' @('SS_PiCi')
Add-Case '203' 'SELECT * FROM rt_fenjian.dbo.SS_PiCi(nolock) as SS_PiCi where SS_PiCi.|' @('PrimaryCode') 'MemberAccess' -Catalog 'use'
$procCreateSql = @"
CREATE OR ALTER PROCEDURE dbo.p
AS
SELECT isbn FROM t_products b1 WHERE b1.cfstate = 1 and b1|
"@
Add-Case '217' $procCreateSql @('b1') 'WhereClause' -Catalog 'use'
$procWhereSql = @"
CREATE OR ALTER PROCEDURE dbo.p
AS
SELECT * FROM rt_data_processing..kuagongshi where pr|
"@
Add-Case '219' $procWhereSql @('primarycode') 'WhereClause' -Catalog 'use'
# CTE: derive columns from definition when no explicit column list
Add-Case '220' 'WITH c1 AS (SELECT a,b FROM t) SELECT * FROM c1 WHERE c1.|' @('a','b') 'MemberAccess'
Add-Case '221' 'WITH c1 AS (SELECT a AS x, b FROM t) SELECT * FROM c1 WHERE c1.|' @('x','b') 'MemberAccess'
Add-Case '222' 'WITH c1 AS (SELECT sum(stock) FROM t) SELECT * FROM c1 WHERE c1.|' @('stock') 'MemberAccess'
# CTE scope: statement after semicolon must not see previous CTE
Add-Case '223' 'WITH c1 AS (SELECT a FROM t) SELECT * FROM c1; WITH c2 AS (SELECT b FROM u) SELECT * FROM c2 WHERE c|' @('c2') 'WhereClause' @('c1')
Add-Case '224' 'WITH c1 AS (SELECT a FROM t) SELECT * FROM c1; SELECT * FROM c2 WHERE c|' @('c2') 'WhereClause' @('c1')
# temp table visible across GO + SELECT INTO #t column derivation
Add-Case '225' "CREATE TABLE #tmp (id int, name nvarchar(50))`nGO`nSELECT * FROM #tmp t WHERE t.|" @('id','name') 'MemberAccess'
Add-Case '226' 'SELECT a, b INTO #t FROM src; SELECT * FROM #t t WHERE t.|' @('a','b') 'MemberAccess'
# data type completion in CAST/CONVERT
Add-Case '227' 'SELECT CAST(x AS i|' @('int') 'DataType'
Add-Case '228' 'SELECT CAST(x AS va|' @('varchar','varbinary') 'DataType'
Add-Case '229' 'SELECT CONVERT(i|' @('int') 'DataType'
Add-Case '230' 'SELECT CAST(x + y|' @() '' @('int','bigint')
# window keywords + functions + PARTITION BY column context
Add-Case '233' 'SELECT nth|' @('NTH_VALUE') 'SelectElements'
Add-Case '234' 'SELECT approx|' @('APPROX_COUNT_DISTINCT') 'SelectElements'
Add-Case '235' 'SELECT string_sp|' @('STRING_SPLIT') 'SelectElements'
Add-Case '236' 'SELECT ov|' @('OVER') 'SelectElements'
# 单字符段首匹配（2026-09-10 起）：i 除前缀命中 id 外，还命中 k_id / h_id 的 id 段
Add-Case '237' 'SELECT ROW_NUMBER() OVER (PARTITION BY i|) FROM dbo.DemoT' @('id','k_id','h_id') 'OrderByGroupBy' -Catalog 'mock'
# HAVING only suggests GROUP BY columns + aggregate functions
Add-Case '239' 'SELECT id, k_id FROM dbo.DemoT GROUP BY id HAVING |' @('id','COUNT') 'HavingClause' @('k_id') -Catalog 'mock'
Add-Case '240' 'SELECT id, k_id FROM dbo.DemoT GROUP BY id HAVING co|' @('COUNT') 'HavingClause' -Catalog 'mock'
Add-Case '241' 'SELECT id, k_id FROM dbo.DemoT GROUP BY id HAVING i|' @('id') 'HavingClause' @('k_id') -Catalog 'mock'
# 下划线段匹配（2026-09-10 优化）：k_ / h_ 要命中 k_id / h_id，单字符 i 要命中 id 段
Add-Case '336' 'SELECT * FROM dbo.DemoT WHERE k_|' @('k_id') 'WhereClause' -Catalog 'mock'
Add-Case '337' 'SELECT * FROM dbo.DemoT WHERE i|' @('id','k_id','h_id') 'WhereClause' -Catalog 'mock'
# PIVOT / TABLESAMPLE / GROUP BY ROLLUP keywords + PIVOT IN(...) skip
Add-Case '242' 'SELECT * FROM t PI|' @('PIVOT') 'FromClause'
Add-Case '243' 'SELECT * FROM t TA|' @('TABLESAMPLE') 'FromClause'
Add-Case '244' 'SELECT * FROM t GROUP BY a RO|' @('ROLLUP') 'OrderByGroupBy'
Add-Case '245' 'SELECT * FROM dbo.DemoT PIVOT (SUM(id) FOR CreateRen IN ([A],[B])) AS p WHERE p.|' @('id') 'MemberAccess' @('A','B') -Catalog 'mock'
# additional top-level / CREATE / ALTER keywords
Add-Case '246' 'th|' @('THROW') 'BatchStart'
Add-Case '247' 'pri|' @('PRINT') 'BatchStart'
Add-Case '248' 'db|' @('DBCC') 'BatchStart'
Add-Case '249' 'wa|' @('WAITFOR') 'BatchStart'
Add-Case '250' 'CREATE se|' @('SEQUENCE') 'AfterCreate'
Add-Case '251' 'ALTER se|' @('SEQUENCE') 'AfterAlter'
# HAVING ... AND stays in HavingClause; GROUP BY scope (no cross-statement leak)
Add-Case '253' 'SELECT id, k_id FROM dbo.DemoT GROUP BY id HAVING COUNT(*) > 1 AND i|' @('id') 'HavingClause' @('k_id') -Catalog 'mock'
Add-Case '254' 'SELECT id FROM dbo.DemoT GROUP BY id; SELECT k_id FROM dbo.DemoT HAVING |' @() 'HavingClause' @('id') -Catalog 'mock'
# no-semicolon multi-statement CTE scope (boundary-4): second statement must not see first CTE
Add-Case '255' "WITH c1 AS (SELECT a FROM t) SELECT * FROM c1`nSELECT * FROM c2 WHERE c|" @('c2') 'WhereClause' @('c1')
# CONVERT first arg with precision parens (boundary-3): comma inside type parens is not the arg separator
Add-Case '256' 'SELECT CONVERT(decimal(10,2), x|' @() '' @('int','decimal')
# HAVING/SELECT 函数实参出全部列（MAX/SUM/ISNULL）；裸 HAVING 仍只出 GROUP BY 列
Add-Case '267' 'SELECT id, k_id FROM dbo.DemoT GROUP BY id HAVING max(k|' @('k_id') 'WhereClause' -Catalog 'mock'
Add-Case '268' 'SELECT id, k_id FROM dbo.DemoT GROUP BY id HAVING sum(k|' @('k_id') 'WhereClause' -Catalog 'mock'
Add-Case '269' 'SELECT id, k_id FROM dbo.DemoT GROUP BY id HAVING isnull(k|' @('k_id') 'WhereClause' -Catalog 'mock'
Add-Case '270' 'SELECT max(k|) FROM dbo.DemoT' @('k_id') 'WhereClause' -Catalog 'mock'
# 无分号多语句：下一句别名不得串进上一句
Add-Case '271' ("SELECT bb.| FROM dbo.DemoT" + [char]10 + [char]10 + "SELECT * FROM dbo.DemoT aa INNER JOIN dbo.kucun bb ON aa.id = bb.k_id") @() 'MemberAccess' @('oper') -Catalog 'mock'
Add-Case '272' ("SELECT max(aa.|) FROM dbo.DemoT" + [char]10 + [char]10 + "SELECT * FROM dbo.kucun aa") @() 'MemberAccess' @('oper') -Catalog 'mock'
Add-Case '274' ("SELECT id FROM dbo.DemoT GROUP BY id HAVING max(k|" + [char]10 + [char]10 + "SELECT * FROM dbo.kucun aa") @('k_id') 'WhereClause' @('oper') -Catalog 'mock'
# 空前缀 SELECT 列表仍出表字段（退格删光前缀时引擎侧保持候选）
Add-Case '273' 'SELECT | FROM dbo.DemoT' @('id','k_id') 'SelectElements' -Catalog 'mock'
# SELECT 列别名不提示字段；表提示 NOLOCK；跨库标量函数；cc.* 仍出星号
Add-Case '285' 'SELECT 4 ope| FROM dbo.kucun cc' @() 'SelectAlias' @('oper','operid','FROM','INTO') -Catalog 'mock'
Add-Case '286' 'SELECT cc.k_id ass| FROM dbo.kucun cc' @() 'SelectAlias' @('oper','FROM','INTO') -Catalog 'mock'
Add-Case '287' 'SELECT 4 AS sta| FROM dbo.kucun cc' @() 'SelectAlias' @('oper','FROM','INTO') -Catalog 'mock'
Add-Case '288' 'SELECT * FROM dbo.kucun(no' @('NOLOCK') 'TableHint'
Add-Case '289' 'SELECT * FROM dbo.kucun(' @('NOLOCK','READUNCOMMITTED','READPAST') 'TableHint'
Add-Case '290' 'SELECT Snow' @('SnowflakeID') 'SelectElements' -Catalog 'mock'
Add-Case '291' 'SELECT rt_storage.dbo.Snow' @('SnowflakeID') 'SelectElements' -Catalog 'linked'
Add-Case '292' 'SELECT k| FROM dbo.kucun cc' @('k_id') 'SelectElements' -Catalog 'mock'
Add-Case '293' 'SELECT cc.| FROM dbo.kucun cc' @('*','k_id') 'MemberAccess' -Catalog 'mock'
Add-Case '294' 'SELECT * FROM dbo.kucun WITH (no' @('NOLOCK') 'TableHint'
# 无分号：函数实参不得吃到下一句 FROM 的字段；别名空位不得出 FROM/INTO
Add-Case '295' ("SELECT dbo.SnowflakeID2(|" + [char]10 + [char]10 + "SELECT * FROM dbo.kucun cc") @() 'WhereClause' @('oper','operid','k_id') -Catalog 'mock'
Add-Case '296' ("SELECT rt_storage..SnowflakeID2(|" + [char]10 + [char]10 + "SELECT * FROM dbo.kucun cc") @() 'WhereClause' @('oper','k_id') -Catalog 'mock'
Add-Case '297' ("SELECT * FROM dbo.DemoT" + [char]10 + [char]10 + "SELECT dbo.SnowflakeID2(|" + [char]10 + [char]10 + "SELECT * FROM dbo.kucun cc") @() 'WhereClause' @('oper','k_id','CreateRen') -Catalog 'mock'
Add-Case '298' 'SELECT 4 ' @() 'SelectAlias' @('FROM','INTO')
Add-Case '299' 'SELECT a,b IN' @('INTO') 'SelectElements' @('AS')
Add-Case '300' 'TRUNCATE TABLE k' @('kucun') 'DdlObjectTarget' -Catalog 'mock'
Add-Case '301' 'TRUNCATE TABLE ' @('kucun','DemoT') 'DdlObjectTarget' -Catalog 'mock'
Add-Case '302' 'DROP t' @('TABLE') 'AfterDrop'
Add-Case '303' 'DROP TABLE k' @('kucun') 'DdlObjectTarget' -Catalog 'mock'
Add-Case '304' 'ALTER TABLE k' @('kucun') 'DdlObjectTarget' -Catalog 'mock'
Add-Case '305' 'co' @('COMMIT') 'BatchStart'; Add-Case '306' 'ro' @('ROLLBACK') 'BatchStart'
Add-Case '307' 'MERGE INTO k' @('kucun') 'DdlObjectTarget' -Catalog 'mock'
Add-Case '308' 'tr' @('TRUNCATE') 'BatchStart'
# 按序子序列：ddi / daoitem → DH_DaoHuoItem
Add-Case '310' ("USE rt_storage" + [char]10 + "SELECT * FROM ddi") @('DH_DaoHuoItem') 'FromClause' -Catalog 'use'
Add-Case '311' ("USE rt_storage" + [char]10 + "SELECT * FROM daoitem") @('DH_DaoHuoItem') 'FromClause' -Catalog 'use'
Add-Case '312' 'SELECT * FROM rt_storage.dbo.ddi' @('DH_DaoHuoItem') 'FromClause' -Catalog 'use'
# sys.* / INFORMATION_SCHEMA.* 系统对象（缓存不含 is_ms_shipped）
Add-Case '313' 'SELECT * FROM sys' @('sys') 'FromClause' @('sys.tables','sys.objects','tables') -Catalog 'mock'
Add-Case '314' 'SELECT * FROM sys.' @('tables','objects','columns') 'FromClause' -Catalog 'mock'
Add-Case '315' 'SELECT * FROM sys.tab' @('tables') 'FromClause' -Catalog 'mock'
Add-Case '316' 'SELECT * FROM INFORMATION_SCHEMA' @('INFORMATION_SCHEMA') 'FromClause' @('INFORMATION_SCHEMA.TABLES','TABLES') -Catalog 'mock'
Add-Case '316b' 'SELECT * FROM INFORMATION_SCHEMA.' @('TABLES') 'FromClause' -Catalog 'mock'
# 目录已加载 sys 对象时，以服务器名单为准（含静态表没有的 partitions）
Add-Case '325' 'SELECT * FROM sys.par' @('partitions') 'FromClause' -Catalog 'mock'
# 标了已加载但缓存里没有 sys 对象时，回退静态名单（避免 FROM sys. 空白）
Add-Case '326' 'SELECT * FROM sys.ta' @('tables') 'FromClause' -Catalog 'emptysys'
# sys 表值函数 / 系统过程从目录拉取（DMF）
Add-Case '327' 'SELECT * FROM sys.dm_db_log' @('dm_db_log_info') 'FromClause' -Catalog 'mock'
Add-Case '328' 'EXEC sys.sp_helpt' @('sp_helptext') 'AfterExec' -Catalog 'mock'
# 实例级 sys 目录：当前库缓存没有 sys 对象时，仍用服务器共用目录
Add-Case '329' 'SELECT * FROM sys.dm_db_log' @('dm_db_log_info') 'FromClause' -Catalog 'sharesys'
Add-Case '330' 'SELECT * FROM sys.par' @('partitions') 'FromClause' -Catalog 'sharesys'
# 未写出 sys. 时不把系统对象混进 FROM 候选
Add-Case '331' 'SELECT * FROM ' @() 'FromClause' @('sys.tables','tables','objects','dm_db_log_info') -Catalog 'mock'
# 系统架构从 sys.schemas 拉取（与 dbo 相同），不再写死
Add-Case '317' 'SELECT * FROM RtBase.' @('dbo','sys','guest','db_owner','INFORMATION_SCHEMA','db_datareader') 'FromClause' -Catalog 'mock'
Add-Case '318' 'SELECT * FROM db_' @('db_owner','db_datareader','db_datawriter','db_ddladmin') 'FromClause' -Catalog 'mock'
Add-Case '319' 'SELECT * FROM guest' @('guest') 'FromClause' -Catalog 'mock'
Add-Case '320' 'SELECT * FROM jichushuju.' @('dbo','sys','guest','db_owner') 'FromClause' -Catalog 'use'

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
$routineType = $asm.GetType('AxialSqlTools.IntelliSense.RoutineInfo')
$kindType = $asm.GetType('AxialSqlTools.IntelliSense.RoutineKind')
$snowFn = [Activator]::CreateInstance($routineType)
$snowFn.Schema = 'dbo'
$snowFn.Name = 'SnowflakeID'
$snowFn.Kind = [Enum]::Parse($kindType, 'ScalarFunction')
[void]$mockCatalog.ScalarFunctions.Add($snowFn)
$mockCatalog.RoutinesLoaded = $true

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
$kucunZong = [Activator]::CreateInstance($tableType)
$kucunZong.Schema = 'dbo'
$kucunZong.Name = 'kucun_zong'
foreach ($cn in @('h_id','z_id','k_id','ghs_id','cost0','stock','j_id','status','price')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'int'
    [void]$kucunZong.Columns.Add($col)
}
[void]$storageCatalog.Tables.Add($kucunZong)
$tuiGhs = [Activator]::CreateInstance($tableType)
$tuiGhs.Schema = 'dbo'
$tuiGhs.Name = 'TH_TuiGHS_ShenQing_Item'
foreach ($cn in @('j_id','status','H_ID','K_ID','CeShu','Y_GHSID','Y_Cost0','isqt')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'int'
    [void]$tuiGhs.Columns.Add($col)
}
[void]$storageCatalog.Tables.Add($tuiGhs)
$daoHuo = [Activator]::CreateInstance($tableType)
$daoHuo.Schema = 'dbo'
$daoHuo.Name = 'DH_DaoHuoItem'
foreach ($cn in @('Id','Qty')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'int'
    [void]$daoHuo.Columns.Add($col)
}
[void]$storageCatalog.Tables.Add($daoHuo)
$snowFn2 = [Activator]::CreateInstance($routineType)
$snowFn2.Schema = 'dbo'
$snowFn2.Name = 'SnowflakeID'
$snowFn2.Kind = [Enum]::Parse($kindType, 'ScalarFunction')
[void]$storageCatalog.ScalarFunctions.Add($snowFn2)
$storageCatalog.RoutinesLoaded = $true
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
$ghs = [Activator]::CreateInstance($tableType)
$ghs.Schema = 'dbo'
$ghs.Name = 'GongYingShang'
foreach ($cn in @('Id','SupplierName')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'nvarchar'
    [void]$ghs.Columns.Add($col)
}
[void]$rtbaseCatalog.Tables.Add($ghs)
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

$fenjianCatalog = [Activator]::CreateInstance($catalogType)
$fenjianCatalog.Database = 'rt_fenjian'
$pici = [Activator]::CreateInstance($tableType)
$pici.Schema = 'dbo'
$pici.Name = 'SS_PiCi'
foreach ($cn in @('PrimaryCode','Version')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'nvarchar'
    [void]$pici.Columns.Add($col)
}
[void]$fenjianCatalog.Tables.Add($pici)
$svc.PutCatalog('local', 'rt_fenjian', $fenjianCatalog)

$jichuCatalog = [Activator]::CreateInstance($catalogType)
$jichuCatalog.Database = 'jichushuju'
$tProducts = [Activator]::CreateInstance($tableType)
$tProducts.Schema = 'dbo'
$tProducts.Name = 't_products'
foreach ($cn in @('isbn','cfstate','State')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'nvarchar'
    [void]$tProducts.Columns.Add($col)
}
[void]$jichuCatalog.Tables.Add($tProducts)
$apiLog = [Activator]::CreateInstance($tableType)
$apiLog.Schema = 'dbo'
$apiLog.Name = 't_ProductApiLog'
foreach ($cn in @('Id','ApiName','CreateTime')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'nvarchar'
    [void]$apiLog.Columns.Add($col)
}
[void]$jichuCatalog.Tables.Add($apiLog)
$svc.PutCatalog('192.168.1.108', 'jichushuju', $jichuCatalog)
$svc.PutCatalog('local', 'jichushuju', $jichuCatalog)

$rtDataCatalog = [Activator]::CreateInstance($catalogType)
$rtDataCatalog.Database = 'rt_data_processing'
$kuagong = [Activator]::CreateInstance($tableType)
$kuagong.Schema = 'dbo'
$kuagong.Name = 'kuagongshi'
foreach ($cn in @('primarycode','price')) {
    $col = [Activator]::CreateInstance($colType)
    $col.Name = $cn
    $col.DataType = 'nvarchar'
    [void]$kuagong.Columns.Add($col)
}
[void]$rtDataCatalog.Tables.Add($kuagong)
$svc.PutCatalog('local', 'rt_data_processing', $rtDataCatalog)

function Add-StandardSchemas($catalog) {
    if ($null -eq $catalog) { return }
    foreach ($s in @(
            'db_accessadmin','db_backupoperator','db_datareader','db_datawriter',
            'db_ddladmin','db_denydatareader','db_denydatawriter','db_owner',
            'db_securityadmin','dbo','guest','INFORMATION_SCHEMA','sys'
        )) {
        [void]$catalog.Schemas.Add($s)
    }
}
Add-StandardSchemas $mockCatalog
Add-StandardSchemas $linkedCatalog
Add-StandardSchemas $storageCatalog
Add-StandardSchemas $masterCatalog
Add-StandardSchemas $rtbaseCatalog
Add-StandardSchemas $newdakuCatalog
Add-StandardSchemas $fenjianCatalog
Add-StandardSchemas $jichuCatalog
Add-StandardSchemas $rtDataCatalog

function Add-SystemCatalogViews($catalog) {
    if ($null -eq $catalog) { return }
    foreach ($pair in @(
            @('sys','tables'), @('sys','objects'), @('sys','columns'), @('sys','views'),
            @('sys','partitions'), @('INFORMATION_SCHEMA','TABLES')
        )) {
        $v = [Activator]::CreateInstance($tableType)
        $v.Schema = $pair[0]
        $v.Name = $pair[1]
        $v.IsView = $true
        $col = [Activator]::CreateInstance($colType)
        $col.Name = 'name'
        $col.DataType = 'sysname'
        [void]$v.Columns.Add($col)
        [void]$catalog.Views.Add($v)
    }
    $catalog.SystemObjectsLoaded = $true
}
function Add-SystemRoutines($catalog) {
    if ($null -eq $catalog) { return }
    $paramType = $asm.GetType('AxialSqlTools.IntelliSense.RoutineParam')
    $tf = [Activator]::CreateInstance($routineType)
    $tf.Schema = 'sys'
    $tf.Name = 'dm_db_log_info'
    $tf.Kind = [Enum]::Parse($kindType, 'TableFunction')
    $p = [Activator]::CreateInstance($paramType)
    $p.Name = '@DatabaseId'
    $p.DataType = 'int'
    [void]$tf.Parameters.Add($p)
    [void]$catalog.TableFunctions.Add($tf)
    $proc = [Activator]::CreateInstance($routineType)
    $proc.Schema = 'sys'
    $proc.Name = 'sp_helptext'
    $proc.Kind = [Enum]::Parse($kindType, 'Procedure')
    [void]$catalog.Procedures.Add($proc)
    $catalog.SystemRoutinesLoaded = $true
}
Add-SystemCatalogViews $mockCatalog
Add-SystemCatalogViews $masterCatalog
Add-SystemCatalogViews $jichuCatalog
Add-SystemRoutines $mockCatalog
Add-SystemRoutines $masterCatalog
Add-SystemRoutines $jichuCatalog

$emptySysCatalog = [Activator]::CreateInstance($catalogType)
$emptySysCatalog.Database = 'master'
$emptySysCatalog.IsIndexed = $true
$emptySysCatalog.SystemObjectsLoaded = $true
Add-StandardSchemas $emptySysCatalog

$shareSysCatalog = [Activator]::CreateInstance($catalogType)
$shareSysCatalog.Server = 'axial-sys-share-test'
$shareSysCatalog.IsIndexed = $true
Add-SystemCatalogViews $shareSysCatalog
Add-SystemRoutines $shareSysCatalog
$svc.PutSystemCatalog('axial-sys-share-test', $shareSysCatalog)
$shareDbCatalog = [Activator]::CreateInstance($catalogType)
$shareDbCatalog.Server = 'axial-sys-share-test'
$shareDbCatalog.Database = 'RtBase'
$shareDbCatalog.IsIndexed = $true
Add-StandardSchemas $shareDbCatalog
$shareConn = [Activator]::CreateInstance($connType)
[void]$connType.GetProperty('ServerName').SetValue($shareConn, 'axial-sys-share-test')
[void]$connType.GetProperty('Database').SetValue($shareConn, 'RtBase')

$masterConn = [Activator]::CreateInstance($connType)
[void]$connType.GetProperty('ServerName').SetValue($masterConn, 'local')
[void]$connType.GetProperty('Database').SetValue($masterConn, 'master')

$fail = New-Object System.Collections.Generic.List[string]
$pass = 0
foreach ($c in $cases) {
    $e = [Activator]::CreateInstance($engineType)
    $s = [Activator]::CreateInstance($settingsType)
    $s.includeKeywords = $true
    $cat = $null
    $conn = $null
    switch ($c.Catalog) {
        'mock'     { $cat = $mockCatalog }
        'linked'   { $cat = $mockCatalog; $conn = $linkedConn }
        'use'      { $cat = $masterCatalog; $conn = $masterConn }
        'emptysys' { $cat = $emptySysCatalog }
        'sharesys' { $cat = $shareDbCatalog; $conn = $shareConn }
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
    $ssPiciInsert = $null
    $tProductsInsert = $null
    $tProductsDisplay = $null
    $kucunSuffix = $null
    if ($r.Items) {
        foreach ($it in $r.Items) {
            [void]$n.Add([string]$it.DisplayText)
            [void]$kinds.Add([string]$it.Kind)
            if ([string]$it.Kind -eq 'AllColumns') { $allInsert = [string]$it.InsertText }
            if ([string]$it.DisplayText -eq 'k_id') { $kucunSuffix = [string]$it.DisplaySuffix }
            if ([string]$it.DisplayText -eq 'kucun' -or [string]$it.DisplayText -eq 'dbo.kucun') {
                $kucunInsert = [string]$it.InsertText
            }
            if ([string]$it.DisplayText -eq 't_products' -or [string]$it.DisplayText -eq 'dbo.t_products') {
                $tProductsInsert = [string]$it.InsertText
                $tProductsDisplay = [string]$it.DisplayText
            }
            if ([string]$it.DisplayText -eq 'SS_PiCi') { $ssPiciInsert = [string]$it.InsertText }
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
    if ($c.Name -eq '292' -and $kucunSuffix -ne '(cc)') {
        $ok = $false
        $why = "suffix=$kucunSuffix want=(cc)"
    }
    if ($c.Name -in @('310','311','312')) {
        $wantChars = if ($c.Name -eq '311') { 'daoitem' } else { 'ddi' }
        $hitIt = $null
        if ($r.Items) {
            foreach ($it in $r.Items) {
                $dt = [string]$it.DisplayText
                if ($dt -eq 'DH_DaoHuoItem' -or $dt.EndsWith('.DH_DaoHuoItem')) { $hitIt = $it; break }
            }
        }
        if ($null -ne $hitIt) {
            $idx = $hitIt.MatchIndices
            $gotChars = ''
            if ($idx) {
                foreach ($i in $idx) {
                    $dt = [string]$hitIt.DisplayText
                    if ($i -ge 0 -and $i -lt $dt.Length) { $gotChars += $dt[$i] }
                }
            }
            if ($gotChars.ToLower() -ne $wantChars) {
                $ok = $false
                $why = "highlight=[$gotChars] want=$wantChars display=$($hitIt.DisplayText)"
            }
        }
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
    if ($c.Name -eq '201') {
        if ($ssPiciInsert -notmatch 'SS_PiCi' -or $ssPiciInsert -notlike '*.') {
            $ok = $false
            $why = "SS_PiCi insert=[$ssPiciInsert] want alias."
        }
    }
    if ($c.Name -in @('280','282')) {
        if ($tProductsDisplay -ne 'dbo.t_products') {
            $ok = $false
            $why = "display=[$tProductsDisplay] want dbo.t_products"
        } elseif ($tProductsInsert -notmatch 'dbo') {
            $ok = $false
            $why = "insert missing dbo: $tProductsInsert"
        }
    }
    if ($c.Name -eq '284') {
        if (-not $n.Contains('dbo.kucun')) {
            $ok = $false
            $why = "display missing dbo.kucun"
        } elseif ($kucunInsert -notmatch 'dbo') {
            $ok = $false
            $why = "insert missing dbo: $kucunInsert"
        }
    }
    if ($c.Name -in @('281','283')) {
        if ($tProductsDisplay -ne 't_products') {
            $ok = $false
            $why = "display=[$tProductsDisplay] want t_products"
        } elseif ($tProductsInsert -match 'dbo') {
            $ok = $false
            $why = "insert should be table only: $tProductsInsert"
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

# ~1MB：无 GO、含 GongYingShang（旧 IndexOf GO 假阳性）。二次调用应走指纹缓存，且不得把全文钉在静态字段。
[void]$pad.Clear()
[void]$pad.AppendLine('USE rt_storage')
for ($i = 0; $i -lt 15000; $i++) { [void]$pad.AppendLine('SELECT 1 FROM RtBase.dbo.GongYingShang AS GHS') }
$mbTail = 'SELECT a.x FROM DB_DiaoBoItem (nolock) a INNER JOIN BK_KuFang (nolock) k ON a.x=k.k_id where k.'
$mbSql = $pad.ToString() + $mbTail
$mbCaret = $mbSql.Length
Write-Host ("258 large-doc sqlLen=$($mbSql.Length)")
$sMb = [Activator]::CreateInstance($settingsType)
$sMb.includeKeywords = $true
$eMb = [Activator]::CreateInstance($engineType)
$swMb1 = [Diagnostics.Stopwatch]::StartNew()
$rMb = $get.Invoke($eMb, @($mbSql, $mbCaret, $masterCatalog, $sMb, $masterConn))
$swMb1.Stop()
$eMb2 = [Activator]::CreateInstance($engineType)
$swMb2 = [Diagnostics.Stopwatch]::StartNew()
$rMb2 = $get.Invoke($eMb2, @($mbSql, $mbCaret, $masterCatalog, $sMb, $masterConn))
$swMb2.Stop()
Write-Host ("258 GetCompletion ~1MB first={0:F1}ms second={1:F1}ms" -f $swMb1.Elapsed.TotalMilliseconds, $swMb2.Elapsed.TotalMilliseconds)
$mbNames = New-Object System.Collections.Generic.List[string]
if ($rMb2 -ne $null -and $rMb2.Items) { foreach ($it in $rMb2.Items) { [void]$mbNames.Add([string]$it.DisplayText) } }
$mbHit = $false
foreach ($disp in $mbNames) {
    if ($disp -eq 'k_id' -or $disp.EndsWith('.k_id')) { $mbHit = $true; break }
}
if (-not $mbHit) { [void]$fail.Add("258 large-doc miss k_id ctx=$($rMb2.Context) prefix=[$($rMb2.Prefix)] top=[$($mbNames | Select-Object -First 8)]") }
elseif ($swMb2.Elapsed.TotalMilliseconds -gt 50) { [void]$fail.Add("258 large-doc second GetCompletion $($swMb2.Elapsed.TotalMilliseconds.ToString('F1'))ms > 50ms") }
else { $pass++ }
$qiMb = [Activator]::CreateInstance($qiType)
$mbQiHover = $mbSql.LastIndexOf('BK_KuFang') + 3
$swQiMb1 = [Diagnostics.Stopwatch]::StartNew()
$qiMb1 = $qiMb.GetQuickInfo($mbSql, $mbQiHover, $masterCatalog, $masterConn)
$swQiMb1.Stop()
$swQiMb2 = [Diagnostics.Stopwatch]::StartNew()
$qiMb2 = $qiMb.GetQuickInfo($mbSql, $mbQiHover, $masterCatalog, $masterConn)
$swQiMb2.Stop()
Write-Host ("258 QuickInfo ~1MB first={0:F1}ms second={1:F1}ms" -f $swQiMb1.Elapsed.TotalMilliseconds, $swQiMb2.Elapsed.TotalMilliseconds)
$mbQiOk = $false
if ($null -ne $qiMb2 -and $qiMb2.HeaderLines) {
    $hMb = $qiMb2.HeaderLines -join ' | '
    if ($hMb -match 'BK_KuFang' -and $hMb -match 'rt_storage') { $mbQiOk = $true }
}
if (-not $mbQiOk) { [void]$fail.Add("258 large-doc QuickInfo miss") }
elseif ($swQiMb2.Elapsed.TotalMilliseconds -gt 40) { [void]$fail.Add("258 large-doc second QuickInfo $($swQiMb2.Elapsed.TotalMilliseconds.ToString('F1'))ms > 40ms") }
else { $pass++ }
$mbSql = $null
$rMb = $null
$rMb2 = $null
$qiMb1 = $null
$qiMb2 = $null

[void]$pad.Clear()
$pad.Length = 0
for ($i = 0; $i -lt 5000; $i++) { [void]$pad.AppendLine('AND 1=1') }
$cartSql = @"
SELECT CARTNEW.x
FROM (
SELECT 1 AS x FROM (SELECT 1 AS x) q WHERE 1=1
$($pad.ToString())
) AS CARTNEW
WHERE CARTNEW
"@
[void]$pad.Clear()
$cartDot = "$cartSql."
Write-Host ("259 cartnew sqlLen=$($cartSql.Length)")
$eCart = [Activator]::CreateInstance($engineType)
$sCart = [Activator]::CreateInstance($settingsType)
$sCart.includeKeywords = $true
$rCart = $get.Invoke($eCart, @($cartDot, [int]$cartDot.Length, $masterCatalog, $sCart, $masterConn))
$cartHit = $false
$cartNames = New-Object System.Collections.Generic.List[string]
if ($rCart -ne $null -and $rCart.Items) {
    foreach ($it in $rCart.Items) {
        [void]$cartNames.Add([string]$it.DisplayText)
        if ($it.DisplayText -eq 'x') { $cartHit = $true }
    }
}
if (-not $cartHit) { [void]$fail.Add("259 CARTNEW. miss x ctx=$(if ($rCart) { $rCart.Context } else { 'null' }) prefix=[$(if ($rCart) { $rCart.Prefix } else { '' })] top=[$($cartNames | Select-Object -First 8)]") }
else { $pass++ }
$qiCart = [Activator]::CreateInstance($qiType)
$cartHover = $cartSql.LastIndexOf('CARTNEW') + 3
$qiCartInfo = $qiCart.GetQuickInfo($cartSql, $cartHover, $masterCatalog, $masterConn)
$cartQiOk = $false
$cartQiWhy = 'null'
if ($null -ne $qiCartInfo) {
    $hCart = if ($qiCartInfo.HeaderLines) { ($qiCartInfo.HeaderLines -join ' | ') } else { '' }
    $ddlCart = [string]$qiCartInfo.DdlText
    if ($hCart -match 'CARTNEW' -or $ddlCart -match 'CARTNEW' -or $ddlCart -match 'SELECT 1 AS x') { $cartQiOk = $true }
    else { $cartQiWhy = "headers=[$hCart] ddl=$ddlCart" }
}
if ($cartQiOk) { $pass++ } else { [void]$fail.Add("259 QuickInfo CARTNEW $cartQiWhy") }
$cartSql = $null
$rCart = $null
$qiCartInfo = $null

# 大切片不能切掉 FROM … AS KC。悬停 JOIN/相关子查询里的 KC 都要能解析到 kucun_zong
[void]$pad.Clear()
$pad.Length = 0
for ($i = 0; $i -lt 4000; $i++) { [void]$pad.AppendLine('AND 1=1') }
$kcSql = @"
SELECT COUNT(*) AS total
FROM (SELECT KC.h_id,
             isnull((select sum(CeShu) from rt_storage.dbo.TH_TuiGHS_ShenQing_Item where j_id = 0 and GHS_PRE.Id = KC.ghs_id),0) as cartshuliang
      FROM rt_storage.dbo.kucun_zong AS KC WITH (NOLOCK)
           INNER JOIN RtBase.dbo.GongYingShang AS GHS_PRE WITH (NOLOCK)
                      ON GHS_PRE.Id = KC.ghs_id
      WHERE KC.status = 1
      $($pad.ToString())
      GROUP BY KC.h_id) AS KCZ
WHERE KCZ.stock > 0
"@
[void]$pad.Clear()
Write-Host ("260 kc sqlLen=$($kcSql.Length)")
$kcJoinHover = $kcSql.LastIndexOf('KC.ghs_id') + 1
$kcSubHover = $kcSql.IndexOf('KC.ghs_id') + 1
$qiKcJoin = [Activator]::CreateInstance($qiType)
$qiKcJoinInfo = $qiKcJoin.GetQuickInfo($kcSql, $kcJoinHover, $masterCatalog, $masterConn)
$kcJoinOk = $false
$kcJoinWhy = 'null'
if ($null -ne $qiKcJoinInfo) {
    $hKcJoin = if ($qiKcJoinInfo.HeaderLines) { ($qiKcJoinInfo.HeaderLines -join ' | ') } else { '' }
    if ($hKcJoin -match 'kucun_zong') { $kcJoinOk = $true }
    else { $kcJoinWhy = "headers=[$hKcJoin]" }
} else { $kcJoinWhy = 'null (JOIN ON GHS_PRE.Id = KC.ghs_id)' }
if ($kcJoinOk) { $pass++ } else { [void]$fail.Add("260 QuickInfo JOIN KC $kcJoinWhy") }
$qiKcSub = [Activator]::CreateInstance($qiType)
$qiKcSubInfo = $qiKcSub.GetQuickInfo($kcSql, $kcSubHover, $masterCatalog, $masterConn)
$kcSubOk = $false
$kcSubWhy = 'null'
if ($null -ne $qiKcSubInfo) {
    $hKcSub = if ($qiKcSubInfo.HeaderLines) { ($qiKcSubInfo.HeaderLines -join ' | ') } else { '' }
    if ($hKcSub -match 'kucun_zong') { $kcSubOk = $true }
    else { $kcSubWhy = "headers=[$hKcSub]" }
} else { $kcSubWhy = 'null (subquery GHS_PRE.Id = KC.ghs_id)' }
if ($kcSubOk) { $pass++ } else { [void]$fail.Add("260 QuickInfo subquery KC $kcSubWhy") }
$kcMember = $kcSql.Substring(0, $kcSql.LastIndexOf('KC.ghs_id') + 3)
$eKc = [Activator]::CreateInstance($engineType)
$sKc = [Activator]::CreateInstance($settingsType)
$sKc.includeKeywords = $true
$rKc = $get.Invoke($eKc, @($kcMember, [int]$kcMember.Length, $masterCatalog, $sKc, $masterConn))
$kcHit = $false
$kcNames = New-Object System.Collections.Generic.List[string]
if ($rKc -ne $null -and $rKc.Items) {
    foreach ($it in $rKc.Items) {
        [void]$kcNames.Add([string]$it.DisplayText)
        if ($it.DisplayText -eq 'h_id' -or $it.DisplayText -eq 'ghs_id') { $kcHit = $true }
    }
}
if (-not $kcHit) { [void]$fail.Add("260 KC. miss col ctx=$(if ($rKc) { $rKc.Context } else { 'null' }) prefix=[$(if ($rKc) { $rKc.Prefix } else { '' })] top=[$($kcNames | Select-Object -First 8)]") }
else { $pass++ }
$kcSql = $null
$rKc = $null
$qiKcJoinInfo = $null
$qiKcSubInfo = $null

$qiSql8 = $derivedSql.Replace('|', '')
$qiHover8 = $qiSql8.IndexOf('GongYingShang') + 2
$qi8 = [Activator]::CreateInstance($qiType)
$qiInfo8 = $qi8.GetQuickInfo($qiSql8, $qiHover8, $masterCatalog, $masterConn)
$qi8Ok = $false
$qi8Why = 'null'
if ($null -ne $qiInfo8) {
    $headers8 = if ($qiInfo8.HeaderLines) { ($qiInfo8.HeaderLines -join ' | ') } else { '' }
    if ($headers8 -match 'GongYingShang' -and $headers8 -match 'RtBase') { $qi8Ok = $true }
    else { $qi8Why = "headers=[$headers8]" }
} else { $qi8Why = 'null (derived-table JOIN GongYingShang should resolve RtBase)' }
if ($qi8Ok) { $pass++ } else { [void]$fail.Add("194 QuickInfo GongYingShang $qi8Why") }

$qiHover9 = $qiSql8.IndexOf('db_bookInfo_Base') + 3
$qi9 = [Activator]::CreateInstance($qiType)
$qiInfo9 = $qi9.GetQuickInfo($qiSql8, $qiHover9, $masterCatalog, $masterConn)
$qi9Ok = $false
$qi9Why = 'null'
if ($null -ne $qiInfo9) {
    $headers9 = if ($qiInfo9.HeaderLines) { ($qiInfo9.HeaderLines -join ' | ') } else { '' }
    if ($headers9 -match 'db_bookInfo_Base' -and $headers9 -match 'newdaku') { $qi9Ok = $true }
    else { $qi9Why = "headers=[$headers9]" }
} else { $qi9Why = 'null (derived-table JOIN book should resolve newdaku)' }
if ($qi9Ok) { $pass++ } else { [void]$fail.Add("195 QuickInfo book $qi9Why") }

$qiHover10 = $qiSql8.LastIndexOf('BK_KuFang') + 3
$qi10 = [Activator]::CreateInstance($qiType)
$qiInfo10 = $qi10.GetQuickInfo($qiSql8, $qiHover10, $masterCatalog, $masterConn)
$qi10Ok = $true
$qi10Why = ''
if ($null -ne $qiInfo10) {
    $headers10 = if ($qiInfo10.HeaderLines) { ($qiInfo10.HeaderLines -join ' | ') } else { '' }
    if ($headers10 -match 'rt_storage') {
        $qi10Ok = $false
        $qi10Why = "unqualified BK_KuFang under USE master should not use rt_storage headers=[$headers10]"
    }
}
if ($qi10Ok) { $pass++ } else { [void]$fail.Add("196 QuickInfo BK_KuFang $qi10Why") }

# 内建函数悬停：COUNT / ROUND / SUM / ISNULL
$fnHoverCases = @(
    @{ Id = '197'; Sql = 'SELECT COUNT(*) FROM t'; Word = 'COUNT'; WantFn = 'COUNT'; WantSig = 'COUNT(' },
    @{ Id = '198'; Sql = 'SELECT round(1.23, 2)'; Word = 'round'; WantFn = 'ROUND'; WantSig = 'ROUND(' },
    @{ Id = '199'; Sql = 'SELECT ISNULL(a, 0) FROM t'; Word = 'ISNULL'; WantFn = 'ISNULL'; WantSig = 'ISNULL(' },
    @{ Id = '200'; Sql = 'SELECT SUM(x) FROM t'; Word = 'SUM'; WantFn = 'SUM'; WantSig = 'SUM(' }
)
foreach ($fc in $fnHoverCases) {
    $fnSql = $fc.Sql
    $fnHover = $fnSql.IndexOf($fc.Word, [StringComparison]::OrdinalIgnoreCase) + 1
    $fnQi = [Activator]::CreateInstance($qiType)
    $fnInfo = $fnQi.GetQuickInfo($fnSql, $fnHover, $masterCatalog, $masterConn)
    $fnOk = $false
    $fnWhy = 'null'
    if ($null -ne $fnInfo) {
        $fnHeaders = if ($fnInfo.HeaderLines) { ($fnInfo.HeaderLines -join ' | ') } else { '' }
        $fnDdl = [string]$fnInfo.DdlText
        if ($fnHeaders -match [regex]::Escape($fc.WantFn) -and $fnDdl -match [regex]::Escape($fc.WantSig)) {
            $fnOk = $true
        }
        else { $fnWhy = "headers=[$fnHeaders] ddl=$fnDdl" }
    }
    else { $fnWhy = "null (hover $($fc.Word) should be built-in $($fc.WantFn))" }
    if ($fnOk) { $pass++ } else { [void]$fail.Add("$($fc.Id) QuickInfo $($fc.Word) $fnWhy") }
}

# 派生表悬停：KCZ / KCZ.stock / 标量子查询 AS cartshuliangnew
try {
    $qiDerivedSql = $derivedSqlQi.Replace('WHERE |', 'WHERE KCZ.stock > 0')
    $qiDer = [Activator]::CreateInstance($qiType)
    $qiHoverKcz = $qiDerivedSql.LastIndexOf('KCZ') + 1
    $qiInfoKcz = $qiDer.GetQuickInfo($qiDerivedSql, $qiHoverKcz, $masterCatalog, $masterConn)
    $qiKczOk = $false
    $qiKczWhy = 'null'
    if ($null -ne $qiInfoKcz) {
        $hKcz = if ($qiInfoKcz.HeaderLines) { ($qiInfoKcz.HeaderLines -join ' | ') } else { '' }
        $dKcz = [string]$qiInfoKcz.DdlText
        if ($hKcz -match 'KCZ' -and $dKcz -match 'SELECT' -and $dKcz -match 'kucun_zong') { $qiKczOk = $true }
        else { $qiKczWhy = "headers=[$hKcz] ddl=$dKcz" }
    } else { $qiKczWhy = 'null (hover KCZ should be derived table)' }
    if ($qiKczOk) { $pass++ } else { [void]$fail.Add("205 QuickInfo derived KCZ $qiKczWhy") }

    $qiDer2 = [Activator]::CreateInstance($qiType)
    $qiHoverCol = $qiDerivedSql.LastIndexOf('stock') + 1
    $qiInfoCol = $qiDer2.GetQuickInfo($qiDerivedSql, $qiHoverCol, $masterCatalog, $masterConn)
    $qiColOk = $false
    $qiColWhy = 'null'
    if ($null -ne $qiInfoCol) {
        $hCol = if ($qiInfoCol.HeaderLines) { ($qiInfoCol.HeaderLines -join ' | ') } else { '' }
        $dCol = [string]$qiInfoCol.DdlText
        if ($hCol -match 'stock' -and $hCol -match 'KCZ' -and $dCol -match 'sum\(stock\)') { $qiColOk = $true }
        else { $qiColWhy = "headers=[$hCol] ddl=$dCol" }
    } else { $qiColWhy = 'null (hover KCZ.stock should be derived column)' }
    if ($qiColOk) { $pass++ } else { [void]$fail.Add("206 QuickInfo derived col $qiColWhy") }

    $qiNestedSql = 'SELECT * FROM (SELECT isnull((select 1),0) as cartshuliangnew) AS KCZ WHERE KCZ.cartshuliangnew > 0'
    $qiDer3 = [Activator]::CreateInstance($qiType)
    $qiHoverNested = $qiNestedSql.LastIndexOf('cartshuliangnew') + 2
    $qiInfoNested = $qiDer3.GetQuickInfo($qiNestedSql, $qiHoverNested, $masterCatalog, $masterConn)
    $qiNestedOk = $false
    $qiNestedWhy = 'null'
    if ($null -ne $qiInfoNested) {
        $hNested = if ($qiInfoNested.HeaderLines) { ($qiInfoNested.HeaderLines -join ' | ') } else { '' }
        $dNested = [string]$qiInfoNested.DdlText
        if ($hNested -match 'cartshuliangnew' -and $hNested -match 'KCZ' -and $dNested -match 'isnull') { $qiNestedOk = $true }
        else { $qiNestedWhy = "headers=[$hNested] ddl=$dNested" }
    } else { $qiNestedWhy = 'null (nested subquery AS cartshuliangnew)' }
    if ($qiNestedOk) { $pass++ } else { [void]$fail.Add("207 QuickInfo nested derived col $qiNestedWhy") }

    $qiBareStock = 'SELECT COUNT(*) FROM (SELECT sum(stock) FROM rt_storage.dbo.kucun_zong AS KC) AS KCZ'
    $qiDer4 = [Activator]::CreateInstance($qiType)
    $qiHoverBare = $qiBareStock.IndexOf('stock') + 1
    $qiInfoBare = $qiDer4.GetQuickInfo($qiBareStock, $qiHoverBare, $masterCatalog, $masterConn)
    $qiBareOk = $false
    $qiBareWhy = 'null'
    if ($null -ne $qiInfoBare) {
        $hBare = if ($qiInfoBare.HeaderLines) { ($qiInfoBare.HeaderLines -join ' | ') } else { '' }
        $dBare = [string]$qiInfoBare.DdlText
        if ($hBare -match 'stock' -and ($hBare -match 'kucun_zong' -or $dBare -match 'sum\(stock\)')) { $qiBareOk = $true }
        else { $qiBareWhy = "headers=[$hBare] ddl=$dBare" }
    } else { $qiBareWhy = 'null (sum(stock) without alias should still resolve stock)' }
    if ($qiBareOk) { $pass++ } else { [void]$fail.Add("208 QuickInfo bare stock $qiBareWhy") }

    $qiSubWhere = 'SELECT * FROM (SELECT isnull((select sum(x) from rt_storage.dbo.TH_TuiGHS_ShenQing_Item where j_id = 0 and status in (1,2,5) and H_ID = 1),0) as c) AS KCZ'
    foreach ($qw in @(
        @{ Id = '209'; Word = 'j_id'; Want = 'TH_TuiGHS_ShenQing_Item' },
        @{ Id = '210'; Word = 'status'; Want = 'TH_TuiGHS_ShenQing_Item' },
        @{ Id = '211'; Word = 'H_ID'; Want = 'TH_TuiGHS_ShenQing_Item' }
    )) {
        $qiSub = [Activator]::CreateInstance($qiType)
        $qiHoverSub = $qiSubWhere.IndexOf($qw.Word) + 1
        $qiInfoSub = $qiSub.GetQuickInfo($qiSubWhere, $qiHoverSub, $masterCatalog, $masterConn)
        $qiSubOk = $false
        $qiSubWhy = 'null'
        if ($null -ne $qiInfoSub) {
            $hSub = if ($qiInfoSub.HeaderLines) { ($qiInfoSub.HeaderLines -join ' | ') } else { '' }
            if ($hSub -match [regex]::Escape($qw.Word) -and $hSub -match [regex]::Escape($qw.Want)) { $qiSubOk = $true }
            else { $qiSubWhy = "headers=[$hSub]" }
        } else { $qiSubWhy = "null (hover $($qw.Word) in subquery WHERE)" }
        if ($qiSubOk) { $pass++ } else { [void]$fail.Add("$($qw.Id) QuickInfo subquery $($qw.Word) $qiSubWhy") }
    }

    $qiCorr = 'SELECT * FROM (SELECT isnull((select 1 from rt_storage.dbo.TH_TuiGHS_ShenQing_Item where H_ID = KC.h_id),0) as cartshuliang FROM rt_storage.dbo.kucun_zong AS KC) AS KCZ'
    $qiCorrKc = [Activator]::CreateInstance($qiType)
    $qiHoverCorrKc = $qiCorr.LastIndexOf('KC.h_id') + 1
    $qiInfoCorrKc = $qiCorrKc.GetQuickInfo($qiCorr, $qiHoverCorrKc, $masterCatalog, $masterConn)
    $qiCorrKcOk = $false
    $qiCorrKcWhy = 'null'
    if ($null -ne $qiInfoCorrKc) {
        $hCorrKc = if ($qiInfoCorrKc.HeaderLines) { ($qiInfoCorrKc.HeaderLines -join ' | ') } else { '' }
        if ($hCorrKc -match 'kucun_zong') { $qiCorrKcOk = $true }
        else { $qiCorrKcWhy = "headers=[$hCorrKc]" }
    } else { $qiCorrKcWhy = 'null (hover KC in cartshuliang subquery)' }
    if ($qiCorrKcOk) { $pass++ } else { [void]$fail.Add("214 QuickInfo corr KC $qiCorrKcWhy") }

    $qiCorrHid = [Activator]::CreateInstance($qiType)
    $qiHoverCorrHid = $qiCorr.LastIndexOf('h_id') + 1
    $qiInfoCorrHid = $qiCorrHid.GetQuickInfo($qiCorr, $qiHoverCorrHid, $masterCatalog, $masterConn)
    $qiCorrHidOk = $false
    $qiCorrHidWhy = 'null'
    if ($null -ne $qiInfoCorrHid) {
        $hCorrHid = if ($qiInfoCorrHid.HeaderLines) { ($qiInfoCorrHid.HeaderLines -join ' | ') } else { '' }
        if ($hCorrHid -match 'h_id' -and $hCorrHid -match 'kucun_zong') { $qiCorrHidOk = $true }
        else { $qiCorrHidWhy = "headers=[$hCorrHid]" }
    } else { $qiCorrHidWhy = 'null (hover KC.h_id in cartshuliang subquery)' }
    if ($qiCorrHidOk) { $pass++ } else { [void]$fail.Add("215 QuickInfo corr h_id $qiCorrHidWhy") }
} catch {
    [void]$fail.Add("205-215 EX=$($_.Exception.Message)")
}

# 四段名 dbo.t_products：悬停表名须带链接服务器，不能当成本地 dbo.t_products
try {
    $qiFourSql = 'SELECT isbn FROM [192.168.1.108].jichushuju.dbo.t_products b1 WHERE b1.cfstate = 1'
    $qiFour = [Activator]::CreateInstance($qiType)
    $qiHoverProd = $qiFourSql.IndexOf('t_products') + 2
    $qiInfoProd = $qiFour.GetQuickInfo($qiFourSql, $qiHoverProd, $masterCatalog, $masterConn)
    $qiProdOk = $false
    $qiProdWhy = 'null'
    if ($null -ne $qiInfoProd) {
        $hProd = if ($qiInfoProd.HeaderLines) { ($qiInfoProd.HeaderLines -join ' | ') } else { '' }
        if ($hProd -match 't_products' -and $hProd -match 'jichushuju' -and $hProd -match '192\.168\.1\.108') { $qiProdOk = $true }
        else { $qiProdWhy = "headers=[$hProd]" }
    } else { $qiProdWhy = 'null (hover four-part t_products)' }
    if ($qiProdOk) { $pass++ } else { [void]$fail.Add("221 QuickInfo four-part t_products $qiProdWhy") }

    $qiFourWord = [Activator]::CreateInstance($qiType)
    $qiInfoProdWord = $qiFourWord.GetQuickInfoByWord($qiFourSql, $qiHoverProd, 't_products', $masterCatalog, $masterConn)
    $qiProdWordOk = $false
    $qiProdWordWhy = 'null'
    if ($null -ne $qiInfoProdWord) {
        $hProdW = if ($qiInfoProdWord.HeaderLines) { ($qiInfoProdWord.HeaderLines -join ' | ') } else { '' }
        if ($hProdW -match 't_products' -and $hProdW -match 'jichushuju') { $qiProdWordOk = $true }
        else { $qiProdWordWhy = "headers=[$hProdW]" }
    } else { $qiProdWordWhy = 'null (GetQuickInfoByWord t_products)' }
    if ($qiProdWordOk) { $pass++ } else { [void]$fail.Add("222 QuickInfoByWord four-part t_products $qiProdWordWhy") }

    $qiFourProc = "CREATE OR ALTER PROCEDURE dbo.p`nAS`nSELECT isbn FROM [192.168.1.108].jichushuju.dbo.t_products b1 WHERE b1.isbn != ''"
    $qiFourP = [Activator]::CreateInstance($qiType)
    $qiHoverProdP = $qiFourProc.IndexOf('t_products') + 2
    $qiInfoProdP = $qiFourP.GetQuickInfo($qiFourProc, $qiHoverProdP, $masterCatalog, $masterConn)
    $qiProdPOk = $false
    $qiProdPWhy = 'null'
    if ($null -ne $qiInfoProdP) {
        $hProdP = if ($qiInfoProdP.HeaderLines) { ($qiInfoProdP.HeaderLines -join ' | ') } else { '' }
        if ($hProdP -match 't_products' -and $hProdP -match 'jichushuju') { $qiProdPOk = $true }
        else { $qiProdPWhy = "headers=[$hProdP]" }
    } else { $qiProdPWhy = 'null (CREATE OR ALTER hover t_products)' }
    if ($qiProdPOk) { $pass++ } else { [void]$fail.Add("223 QuickInfo CREATE OR ALTER t_products $qiProdPWhy") }
} catch {
    [void]$fail.Add("221-223 EX=$($_.Exception.Message)")
}

# hover SELECT * columns (JOIN / derived / a.*)
try {
    $starQi = [Activator]::CreateInstance($qiType)
    $starSql = 'SELECT * FROM jichushuju.dbo.t_ProductApiLog'
    $starHover = $starSql.IndexOf('*')
    $starInfo = $starQi.GetQuickInfo($starSql, $starHover, $masterCatalog, $masterConn)
    $starOk = $false
    $starWhy = 'null'
    if ($null -ne $starInfo) {
        $starDdl = [string]$starInfo.DdlText
        $starH = if ($starInfo.HeaderLines) { ($starInfo.HeaderLines -join ' | ') } else { '' }
        if ($starH -match 'SELECT \*' -and $starDdl -match 'Id nvarchar' -and $starDdl -match 'ApiName nvarchar' -and $starDdl -match 'CreateTime nvarchar') { $starOk = $true }
        else { $starWhy = "headers=[$starH] ddl=$starDdl" }
    } else { $starWhy = 'null (hover * of t_ProductApiLog)' }
    if ($starOk) { $pass++ } else { [void]$fail.Add("261 QuickInfo SELECT * $starWhy") }

    $starJoinSql = 'SELECT * FROM rt_storage.dbo.kucun_zong a INNER JOIN rtbase.dbo.UserInfo b ON a.h_id = b.id'
    $starJoinInfo = $starQi.GetQuickInfo($starJoinSql, $starJoinSql.IndexOf('*'), $masterCatalog, $masterConn)
    $starJoinOk = $false
    $starJoinWhy = 'null'
    if ($null -ne $starJoinInfo) {
        $jd = [string]$starJoinInfo.DdlText
        if ($jd -match 'h_id' -and $jd -match 'stock' -and $jd -match '单位名称') { $starJoinOk = $true }
        else { $starJoinWhy = "ddl=$jd" }
    } else { $starJoinWhy = 'null (hover * of JOIN)' }
    if ($starJoinOk) { $pass++ } else { [void]$fail.Add("262 QuickInfo SELECT * JOIN $starJoinWhy") }

    $starQualSql = 'SELECT a.* FROM rt_storage.dbo.kucun_zong a INNER JOIN rtbase.dbo.UserInfo b ON a.h_id = b.id'
    $starQualInfo = $starQi.GetQuickInfo($starQualSql, $starQualSql.IndexOf('*'), $masterCatalog, $masterConn)
    $starQualOk = $false
    $starQualWhy = 'null'
    if ($null -ne $starQualInfo) {
        $qd = [string]$starQualInfo.DdlText
        $qh = if ($starQualInfo.HeaderLines) { ($starQualInfo.HeaderLines -join ' | ') } else { '' }
        if ($qd -match 'h_id' -and $qd -match 'stock' -and $qd -notmatch '单位名称') { $starQualOk = $true }
        else { $starQualWhy = "headers=[$qh] ddl=$qd" }
    } else { $starQualWhy = 'null (hover a.*)' }
    if ($starQualOk) { $pass++ } else { [void]$fail.Add("263 QuickInfo a.* $starQualWhy") }

    $starDerSql = 'SELECT * FROM (SELECT isbn, cfstate FROM jichushuju.dbo.t_products) AS x'
    $starDerInfo = $starQi.GetQuickInfo($starDerSql, $starDerSql.IndexOf('*'), $masterCatalog, $masterConn)
    $starDerOk = $false
    $starDerWhy = 'null'
    if ($null -ne $starDerInfo) {
        $dd = [string]$starDerInfo.DdlText
        $derCols = @($dd -split '\r?\n' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
        if ($derCols -contains 'isbn' -and $derCols -contains 'cfstate' -and $derCols -notcontains 'State') { $starDerOk = $true }
        else { $starDerWhy = "ddl=$dd" }
    } else { $starDerWhy = 'null (hover * of derived)' }
    if ($starDerOk) { $pass++ } else { [void]$fail.Add("264 QuickInfo SELECT * derived $starDerWhy") }

    $starCountSql = 'SELECT COUNT(*) FROM jichushuju.dbo.t_ProductApiLog'
    $starCountInfo = $starQi.GetQuickInfo($starCountSql, $starCountSql.IndexOf('*'), $masterCatalog, $masterConn)
    if ($null -eq $starCountInfo) { $pass++ } else {
        $cd = [string]$starCountInfo.DdlText
        [void]$fail.Add("265 QuickInfo COUNT(*) should be null ddl=$cd")
    }

    $starMulSql = 'SELECT 1 * 2 FROM jichushuju.dbo.t_ProductApiLog'
    $starMulInfo = $starQi.GetQuickInfo($starMulSql, $starMulSql.IndexOf('*'), $masterCatalog, $masterConn)
    if ($null -eq $starMulInfo) { $pass++ } else {
        $md = [string]$starMulInfo.DdlText
        [void]$fail.Add("266 QuickInfo multiply * should be null ddl=$md")
    }
} catch {
    [void]$fail.Add("261-266 EX=$($_.Exception.Message)")
}

# DDL detection + cache invalidation (schema freshness)
try {
    $containsDdl = $svcType.GetMethod('ContainsDdl', [Reflection.BindingFlags]'NonPublic,Static')
    $ddlOk = $true
    $ddlWhy = ''
    if ($null -eq $containsDdl) { $ddlOk = $false; $ddlWhy = 'ContainsDdl method not found' }
    else {
        foreach ($s in @('CREATE TABLE t (id int)', 'ALTER TABLE t ADD c int', 'DROP TABLE t', 'CREATE OR ALTER TABLE t (id int)', 'TRUNCATE TABLE t', 'CREATE VIEW v AS SELECT 1', 'CREATE PROCEDURE p AS SELECT 1', 'CREATE OR ALTER PROCEDURE p AS SELECT 1', 'DROP VIEW v')) {
            if (-not [bool]$containsDdl.Invoke($null, @($s))) { $ddlOk = $false; $ddlWhy = "ContainsDdl true expected: $s"; break }
        }
        if ($ddlOk) {
            foreach ($s in @('SELECT * FROM t', 'INSERT INTO t VALUES (1)', 'UPDATE t SET a=1', 'SELECT COUNT(*) FROM sys.objects')) {
                if ([bool]$containsDdl.Invoke($null, @($s))) { $ddlOk = $false; $ddlWhy = "ContainsDdl false expected: $s"; break }
            }
        }
    }
    if ($ddlOk) { $pass++ } else { [void]$fail.Add("231 DDL $ddlWhy") }

    $ddlConn = [Activator]::CreateInstance($connType)
    [void]$connType.GetProperty('ServerName').SetValue($ddlConn, 'ddltest')
    [void]$connType.GetProperty('Database').SetValue($ddlConn, 'RtBase')
    $svc.PutCatalog('ddltest', 'RtBase', $mockCatalog)
    if ($null -eq $svc.GetCachedCatalog($ddlConn)) { throw 'DDL setup failed: catalog not cached' }
    $svc.InvalidateDdlTargets($ddlConn, 'CREATE TABLE t (id int)')
    if ($null -ne $svc.GetCachedCatalog($ddlConn)) { $ddlOk = $false; $ddlWhy = 'catalog not invalidated after DDL' }
    else { $pass++ }
    if ($ddlWhy) { [void]$fail.Add("232 DDL-invalidate $ddlWhy") }
} catch {
    [void]$fail.Add("231-232 DDL EX=$($_.Exception.Message)")
}

# DDL invalidation must NOT clear query-referenced (non-target) databases
try {
    $ddlConn2 = [Activator]::CreateInstance($connType)
    [void]$connType.GetProperty('ServerName').SetValue($ddlConn2, 'ddlscope')
    [void]$connType.GetProperty('Database').SetValue($ddlConn2, 'RtBase')
    $svc.PutCatalog('ddlscope', 'RtBase', $mockCatalog)
    $svc.PutCatalog('ddlscope', 'rt_data_processing', $rtDataCatalog)
    $procSql = "CREATE OR ALTER PROCEDURE dbo.p`nAS`nSELECT * FROM rt_data_processing..kuagongshi"
    $svc.InvalidateDdlTargets($ddlConn2, $procSql)
    if ($null -eq $svc.GetCachedCatalog($ddlConn2, 'rt_data_processing')) { throw 'query-referenced db was wrongly invalidated' }
    if ($null -ne $svc.GetCachedCatalog($ddlConn2)) { throw 'current db should be invalidated' }
    $pass++
} catch {
    [void]$fail.Add("257 DDL-scope $($_.Exception.Message)")
}

# empty-but-indexed catalog is cached (not treated as unindexed)
try {
    $emptyCat = [Activator]::CreateInstance($catalogType)
    $emptyCat.Database = 'empty_db'
    $emptyCat.IsIndexed = $true
    $emptyConn = [Activator]::CreateInstance($connType)
    [void]$connType.GetProperty('ServerName').SetValue($emptyConn, 'emptytest')
    [void]$connType.GetProperty('Database').SetValue($emptyConn, 'empty_db')
    $svc.PutCatalog('emptytest', 'empty_db', $emptyCat)
    if ($null -eq $svc.GetCachedCatalog($emptyConn)) { throw 'indexed empty catalog should be cached' }
    $pass++

    $emptyCat2 = [Activator]::CreateInstance($catalogType)
    $emptyCat2.Database = 'empty_db2'
    $emptyCat2.IsIndexed = $false
    $emptyConn2 = [Activator]::CreateInstance($connType)
    [void]$connType.GetProperty('ServerName').SetValue($emptyConn2, 'emptytest2')
    [void]$connType.GetProperty('Database').SetValue($emptyConn2, 'empty_db2')
    $svc.PutCatalog('emptytest2', 'empty_db2', $emptyCat2)
    if ($null -ne $svc.GetCachedCatalog($emptyConn2)) { throw 'unindexed empty catalog should not be cached' }
    $pass++
} catch {
    [void]$fail.Add("252 empty-catalog $($_.Exception.Message)")
}

# 261: clamp popup to caret monitor, not primary WorkArea
try {
    Add-Type -AssemblyName WindowsBase -ErrorAction SilentlyContinue
    $placeType = $asm.GetType('AxialSqlTools.IntelliSense.PopupScreenPlacement')
    if ($null -eq $placeType) { throw 'PopupScreenPlacement not found' }
    $clamp = $placeType.GetMethod('ClampToWorkArea')
    if ($null -eq $clamp) { throw 'ClampToWorkArea not found' }
    $workRight = New-Object -TypeName System.Windows.Rect -ArgumentList ([double]1920), ([double]0), ([double]1920), ([double]1080)
    $pt = $clamp.Invoke($null, [object[]]@([double]2500, [double]100, [double]520, [double]240, $workRight))
    if ([math]::Abs($pt.X - 2500) -gt 0.5) { throw "right-monitor X $($pt.X) pulled off caret" }
    if ([math]::Abs($pt.Y - 100) -gt 0.5) { throw "right-monitor Y $($pt.Y)" }
    $ptEdge = $clamp.Invoke($null, [object[]]@([double]3700, [double]100, [double]520, [double]240, $workRight))
    if ($ptEdge.X -lt 1920) { throw "right-edge clamp X $($ptEdge.X) fell onto primary" }
    if (($ptEdge.X + 520) -gt 3840.5) { throw "right-edge clamp X $($ptEdge.X) overflows secondary" }
    $workLeft = New-Object -TypeName System.Windows.Rect -ArgumentList ([double](-1920)), ([double]0), ([double]1920), ([double]1080)
    $ptLeft = $clamp.Invoke($null, [object[]]@([double](-800), [double]100, [double]520, [double]240, $workLeft))
    if ($ptLeft.X -ge 0) { throw "left-monitor X $($ptLeft.X) pulled onto primary" }
    $placeBr = $placeType.GetMethod('PlaceBottomRight')
    if ($null -eq $placeBr) { throw 'PlaceBottomRight not found' }
    $win = New-Object -TypeName System.Windows.Rect -ArgumentList ([double]400), ([double]200), ([double]1200), ([double]800)
    $ptBr = $placeBr.Invoke($null, [object[]]@($win, [double]360, [double]140))
    if ([math]::Abs($ptBr.X - 1224) -gt 0.5) { throw "progress X $($ptBr.X) not SSMS window corner" }
    if ([math]::Abs($ptBr.Y - 844) -gt 0.5) { throw "progress Y $($ptBr.Y) not SSMS window corner" }
    $pass++
} catch {
    [void]$fail.Add("261 popup-screen $($_.Exception.Message)")
}

Write-Host "PASS=$pass FAIL=$($fail.Count)"
$fail | ForEach-Object { Write-Host "  $_" }
if ($fail.Count -gt 0) { exit 1 }
Write-Host "ALL $pass PASS"
exit 0
