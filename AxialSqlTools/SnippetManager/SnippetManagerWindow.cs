using AxialSqlTools.Properties;
using System;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace AxialSqlTools
{
    [Guid("a7b2c3d4-e5f6-4789-abcd-ef0123456789")]
    public class SnippetManagerWindow : ToolWindowPane
    {
        public SnippetManagerWindow() : base(null)
        {
            this.Caption = Strings.Get("Menu_SnippetManager");
            this.Content = new SnippetManagerWindowControl();
        }
    }
}
