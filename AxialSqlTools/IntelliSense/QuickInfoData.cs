using System.Collections.Generic;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>悬停 ToolTip 结构化内容。</summary>
    public class QuickInfoData
    {
        public List<string> HeaderLines = new List<string>();
        public string Description;
        public string DdlText;
        public bool IsEmpty => HeaderLines.Count == 0 && string.IsNullOrEmpty(DdlText);
    }
}
