using AxialSqlTools.Properties;
using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace AxialSqlTools
{
    [Guid("9e4f2a81-5b1c-4d3e-8f60-2a7c9d4e5f61")]
    public class TabHistoryWindow : ToolWindowPane
    {
        public TabHistoryWindow() : base(null)
        {
            this.Caption = Strings.Get("Menu_TabHistory");
            this.Content = new TabHistoryWindowControl();
        }
    }
}
