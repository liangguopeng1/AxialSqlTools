# -*- coding: utf-8 -*-
import re
import html
from pathlib import Path
from xml.etree import ElementTree as ET

root = Path(r"D:\IntelliJ-IDEA\github-workspace\AxialSqlTools\AxialSqlTools\Properties")
en_path = root / "Strings.resx"
zh_path = root / "Strings.zh-Hans.resx"

# Known Chinese translations for core keys (product default UI)
KNOWN = {
    "Settings_WindowTitle": "Axial SQL Tools - 设置",
    "Settings_ToolWindowCaption": "Axial SQL Tools | 设置",
    "Settings_Tab_General": "常规",
    "Settings_Tab_QueryTemplates": "查询模板",
    "Settings_Tab_CodeSnippets": "代码片段",
    "Settings_Tab_QueryHistory": "查询历史",
    "Settings_Tab_CodeFormat": "代码格式",
    "Settings_Tab_ExcelExport": "Excel 导出",
    "Settings_Tab_GoogleSheets": "Google 表格",
    "Settings_Tab_ConnectionColors": "连接颜色",
    "Settings_Tab_Smtp": "SMTP 设置",
    "Settings_Tab_Updates": "更新",
    "Settings_LanguageLabel": "界面语言",
    "Settings_Language_Chinese": "中文",
    "Settings_Language_English": "English",
    "Settings_LanguageHint": "工具窗口将立即刷新。工具栏与菜单文案需重启 SSMS 后完全生效。",
    "Settings_SaveLanguage": "保存语言",
    "Settings_LanguageSaved": "语言已保存。请重启 SSMS 以使工具栏/菜单完全切换。",
    "Settings_Saved": "已保存",
    "Common_Error": "错误",
    "Common_Done": "完成",
    "Common_Save": "保存",
    "Common_Search": "搜索",
    "Common_Server": "服务器",
    "Common_Port": "端口",
    "Common_Database": "数据库",
    "Common_Username": "用户名",
    "Common_Password": "密码",
    "Common_Wiki": "Wiki",
    "Common_FeatureDescriptionIn": "功能说明见",
    "Common_SavedEllipsis": "已保存...",
    "Menu_Toolbar": "Axial SQL Tools",
    "Menu_QueryTemplates": "查询模板",
    "Menu_Tools": "工具",
    "Menu_FormatQuery": "格式化查询",
    "Menu_FormatQuery_Tooltip": "格式化 SQL（按住 Shift 打开高级选项）",
    "Menu_ExportGridToExcel": "导出网格到 Excel",
    "Menu_ExportGridToEmail": "导出网格到邮件",
    "Menu_ExportGridAsTempTable": "导出网格为临时表",
    "Menu_ExportGridToGoogleSheet": "导出网格到 Google 表格",
    "Menu_ScriptDefinition": "脚本定义到新窗口",
    "Menu_QuickSearch": "快速搜索",
    "Menu_SnippetManager": "片段管理器",
    "Menu_SelectCurrentStatement": "选择当前语句",
    "Menu_SelectCurrentStatement_Tooltip": "选中光标处的 SQL 语句（Shift+F5）",
    "Menu_ToggleBlockComment": "切换块注释",
    "Menu_ToggleBlockComment_Tooltip": "用 /* */ 包裹选中 SQL，或移除光标处外侧块注释",
    "Menu_QueryHistory": "查询历史",
    "Menu_StatisticsSummary": "统计摘要",
    "Menu_StatisticsSummary_Tooltip": "打开最近捕获的 SET STATISTICS IO/TIME 输出摘要。",
    "Menu_HealthDashboardServer": "健康仪表板 - 服务器",
    "Menu_SqlServerBuilds": "SQL Server 版本",
    "Menu_DataTransfer": "数据传输",
    "Menu_SyncToGitHub": "同步到 GitHub",
    "Menu_RefreshTemplates": "刷新模板",
    "Menu_OpenTemplatesFolder": "打开模板文件夹",
    "Menu_Settings": "设置",
    "Menu_About": "关于",
    "Window_About": "Axial SQL",
    "Window_DataImport": "数据导入",
    "Window_GridToEmail": "网格到邮件",
    "Window_HealthDashboardServer": "健康仪表板 | 服务器",
    "Window_HealthDashboardServers": "健康仪表板 - 多服务器",
}

# Phrase-level replacements for remaining English UI strings
PHRASES = [
    ("Settings", "设置"),
    ("General", "常规"),
    ("Query Templates", "查询模板"),
    ("Code Snippets", "代码片段"),
    ("Query History", "查询历史"),
    ("Code Format", "代码格式"),
    ("Excel Export", "Excel 导出"),
    ("Google Sheets", "Google 表格"),
    ("Connection Colors", "连接颜色"),
    ("SMTP Settings", "SMTP 设置"),
    ("Updates", "更新"),
    ("Quick Search", "快速搜索"),
    ("Snippet Manager", "片段管理器"),
    ("Data Transfer", "数据传输"),
    ("Data Import", "数据导入"),
    ("Statistics Summary", "统计摘要"),
    ("Sync to GitHub", "同步到 GitHub"),
    ("SQL Server Builds", "SQL Server 版本"),
    ("Health Dashboard", "健康仪表板"),
    ("Grid to Email", "网格到邮件"),
    ("Save", "保存"),
    ("Cancel", "取消"),
    ("Close", "关闭"),
    ("Delete", "删除"),
    ("Refresh", "刷新"),
    ("Search", "搜索"),
    ("Export", "导出"),
    ("Import", "导入"),
    ("Connect", "连接"),
    ("Disconnect", "断开"),
    ("Browse", "浏览"),
    ("Test", "测试"),
    ("Start", "开始"),
    ("Stop", "停止"),
    ("Open", "打开"),
    ("Copy", "复制"),
    ("Error", "错误"),
    ("Warning", "警告"),
    ("Completed", "已完成"),
    ("Failed", "失败"),
    ("Success", "成功"),
    ("Database", "数据库"),
    ("Server", "服务器"),
    ("Username", "用户名"),
    ("Password", "密码"),
    ("Port", "端口"),
    ("Table", "表"),
    ("Schema", "架构"),
    ("Object", "对象"),
    ("Type", "类型"),
    ("Location", "位置"),
    ("Script", "脚本"),
    ("Source", "源"),
    ("Target", "目标"),
    ("Status", "状态"),
    ("Name", "名称"),
    ("Path", "路径"),
    ("Folder", "文件夹"),
    ("File", "文件"),
    ("Yes", "是"),
    ("No", "否"),
    ("All", "全部"),
    ("None", "无"),
    ("Add", "添加"),
    ("Remove", "移除"),
    ("Edit", "编辑"),
    ("New", "新建"),
    ("Apply", "应用"),
    ("Reset", "重置"),
    ("Download", "下载"),
    ("Upload", "上传"),
    ("Authorize", "授权"),
    ("Authorization", "授权"),
    ("configuration", "配置"),
    ("Configuration", "配置"),
    ("required", "必需"),
    ("Required", "必需"),
    ("available", "可用"),
    ("Available", "可用"),
    ("selected", "已选择"),
    ("Selected", "已选择"),
    ("No connection selected", "未选择连接"),
    ("No Data Available", "无可用数据"),
    ("Export Complete", "导出完成"),
    ("Export Failed", "导出失败"),
    ("Authorization Required", "需要授权"),
    ("Feature description in", "功能说明见"),
    ("Match whole words only", "仅匹配整个单词"),
    ("Use wildcards", "使用通配符"),
    ("All user databases on the server", "服务器上的所有用户数据库"),
    ("Stored Procedures", "存储过程"),
    ("Views", "视图"),
    ("Functions", "函数"),
    ("Tables", "表"),
    ("SQL Agent Jobs", "SQL Agent 作业"),
    ("Select Target from Object Explorer", "从对象资源管理器选择目标"),
    ("Search for Object Definitions Across Multiple Databases", "跨多个数据库搜索对象定义"),
    ("Search for:", "搜索："),
    ("Object types:", "对象类型："),
    ("Match Preview", "匹配预览"),
    ("Captured at", "捕获时间"),
    ("Logical reads", "逻辑读取"),
    ("Scans", "扫描"),
    ("Enable Snippets", "启用片段"),
    ("Refresh Templates", "刷新模板"),
    ("Open Templates Folder", "打开模板文件夹"),
    ("The change has been saved", "更改已保存"),
    ("Setting saved", "设置已保存"),
    ("The list of templates has been updated!", "模板列表已更新！"),
    ("No result sets are available for export.", "没有可供导出的结果集。"),
    ("Update on Close", "关闭时更新"),
    ("Release Notes", "发行说明"),
    ("Copied", "已复制"),
    ("Copy All As ...", "全部复制为 ..."),
    ("Copy Selected As ...", "复制所选为 ..."),
    ("Copy Selected Column Names", "复制所选列名"),
    ("Copy All Column Names", "复制全部列名"),
    ("Copied Column Names", "已复制列名"),
    ("No Column Names to Copy", "没有可复制的列名"),
    ("No data to copy", "没有可复制的数据"),
    ("No cells selected to copy", "未选择要复制的单元格"),
    ("No column selected to copy", "未选择要复制的列"),
    ("Open in Excel", "在 Excel 中打开"),
    ("Select an object to script.", "请选择要生成脚本的对象。"),
    ("Select the object to script.", "请选择要生成脚本的对象。"),
    ("Nothing has been selected", "尚未选择任何内容"),
    ("Error getting selected object", "获取所选对象时出错"),
    ("Error selecting current statement", "选择当前语句时出错"),
    ("Error toggling block comment", "切换块注释时出错"),
    ("Error parsing the code", "解析代码时出错"),
    ("Missing Google Sheets configuration", "缺少 Google 表格配置"),
    ("The data has been exported to Google Sheets.", "数据已导出到 Google 表格。"),
    ("Google Sheets is not authorized yet", "尚未授权 Google 表格"),
    ("Google Sheets client ID and client secret are required", "需要 Google 表格客户端 ID 和客户端密钥"),
    ("The exported file could not be found.", "找不到已导出的文件。"),
    ("Unable to open the exported file in Excel.", "无法在 Excel 中打开已导出的文件。"),
    ("Axial SQL Tools v{0} update available.", "Axial SQL Tools v{0} 有可用更新。"),
    ("Export failed: {0}", "导出失败：{0}"),
    ("File {0} doesn't exist!", "文件 {0} 不存在！"),
    ("Values as IN (...)", "值作为 IN (...)"),
    ("hold Shift for compact list", "按住 Shift 显示紧凑列表"),
    ("Treat first row as column headers", "将第一行作为列标题"),
    ("MySQL local infile is disabled", "MySQL local infile 已禁用"),
]


def translate(en: str) -> str:
    if en in KNOWN.values():
        return en
    for k, v in KNOWN.items():
        if en == k:  # shouldn't happen
            return v
    # exact known by looking up if en matches a known English from map values inverted - skip
    out = en
    for src, dst in sorted(PHRASES, key=lambda x: -len(x[0])):
        out = out.replace(src, dst)
    return out


def parse_resx(path: Path):
    # Keep original order via regex
    text = path.read_text(encoding="utf-8")
    items = []
    for m in re.finditer(
        r'<data name="([^"]+)"[^>]*>\s*<value>(.*?)</value>\s*</data>',
        text,
        flags=re.S,
    ):
        items.append((m.group(1), html.unescape(m.group(2))))
    return items, text


def esc(s: str) -> str:
    return (
        s.replace("&", "&amp;")
        .replace("<", "&lt;")
        .replace(">", "&gt;")
    )


items, _ = parse_resx(en_path)
# Apply known overrides first by key
zh_items = []
for name, en_val in items:
    if name in KNOWN:
        zh_items.append((name, KNOWN[name]))
    else:
        zh_items.append((name, translate(en_val)))

header = '''<?xml version="1.0" encoding="utf-8"?>
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
'''

lines = [header]
for name, value in zh_items:
    lines.append(f'  <data name="{name}" xml:space="preserve"><value>{esc(value)}</value></data>\n')
lines.append("</root>\n")
zh_path.write_text("".join(lines), encoding="utf-8")
print(f"Wrote {len(zh_items)} keys to {zh_path}")
# sanity
sample = zh_path.read_text(encoding="utf-8")
assert "设置" in sample
assert "</value></data>" in sample
assert "?/value>" not in sample
print("OK")
