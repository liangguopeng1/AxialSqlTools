using Aurora;
using AxialSqlTools.Properties;
using EnvDTE;
using Microsoft.VisualStudio.CommandBars;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace AxialSqlTools
{
    /// <summary>
    /// Aurora command that opens Tab History. Inserted into the Tools submenu
    /// immediately after Query History (SSMS 22 often fails to merge new vsct buttons).
    /// </summary>
    internal sealed class TabHistoryCommandProcessor : CommandBase
    {
        private readonly AsyncPackage _package;
        private readonly Plugin _plugin;
        private readonly CommandBar _toolsMenu;
        private readonly uint _insertPosition;

        /// <param name="insertPosition">1-based CommandBar position (Query History.Index + 1).</param>
        public TabHistoryCommandProcessor(Plugin plugin, AsyncPackage package, CommandBar toolsMenu, uint insertPosition)
            : base("Tab History", "AxialSqlTools.TabHistory", plugin, "Open the Tab History window")
        {
            _plugin = plugin;
            _package = package;
            _toolsMenu = toolsMenu;
            _insertPosition = insertPosition;
        }

        // ImageList index 1 = open-folder（与查询历史的 clock 图标区分）
        public override int ScriptIconIndex => 1;
        public override int IconIndex => 1;

        public override bool IsEnabled()
        {
            return true;
        }

        public override bool OnCommand()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            TabHistoryWindowCommand.ShowWindow(_package);
            return true;
        }

        public override bool RegisterGUI(OleMenuCommand vsCommand, CommandBar bar, bool toolBarOnly)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            CommandBar target = _toolsMenu ?? bar;
            if (target == null)
                return true;

            CommandBarButton btn = null;
            try
            {
                btn = target.Controls[CanonicalName] as CommandBarButton;
            }
            catch { }

            if (btn == null)
            {
                object newControl;
                _plugin.ProfferCommands.AddCommandBarControl(
                    CanonicalName,
                    target,
                    _insertPosition,
                    (uint)vsCommandBarType.vsCommandBarTypeToolbar,
                    out newControl);
                btn = newControl as CommandBarButton;
            }
            else
            {
                try
                {
                    if (btn.Index != (int)_insertPosition)
                    {
                        CommandBarControl before = null;
                        try { before = target.Controls[(int)_insertPosition]; } catch { }
                        if (before != null && !ReferenceEquals(before, btn))
                            btn.Move(before, false);
                    }
                }
                catch { }
            }

            if (btn != null)
            {
                AssignIcon(btn, ScriptIconIndex);
                try
                {
                    string caption = Strings.Get("Menu_TabHistory");
                    if (!string.IsNullOrEmpty(caption))
                        btn.Caption = caption;
                    btn.BeginGroup = false;
                }
                catch { }
            }
            return true;
        }
    }
}
