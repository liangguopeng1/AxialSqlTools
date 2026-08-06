using System.Collections.Generic;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>悬停 ToolTip 结构化内容。</summary>
    public class QuickInfoData
    {
        public List<string> HeaderLines = new List<string>();
        public string Description;
        public string DdlText;
        /// <summary>是否显示「跳转源码」（存储过程等）。</summary>
        public bool CanGoToSource;
        public string SourceDatabase;
        public string SourceSchema;
        public string SourceObjectName;
        public bool IsEmpty => HeaderLines.Count == 0 && string.IsNullOrEmpty(DdlText);
    }
}
