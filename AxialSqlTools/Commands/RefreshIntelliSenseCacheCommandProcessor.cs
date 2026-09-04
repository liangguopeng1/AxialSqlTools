using Aurora;
using AxialSqlTools.Properties;
using EnvDTE;
using Microsoft.VisualStudio.CommandBars;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace AxialSqlTools
{
    /// <summary>
    /// SSMS 22 经常合并不了新增 vsct 按钮，显式挂到 Tools 子菜单。
    /// </summary>
    internal sealed class RefreshIntelliSenseCacheCommandProcessor : CommandBase
    {
        private readonly AsyncPackage _package;
        private readonly Plugin _plugin;
        private readonly CommandBar _toolsMenu;
        private readonly uint _insertPosition;

        public RefreshIntelliSenseCacheCommandProcessor(
            Plugin plugin, AsyncPackage package, CommandBar toolsMenu, uint insertPosition)
            : base("Refresh Cache", "AxialSqlTools.RefreshIntelliSenseCache", plugin,
                "Refresh IntelliSense cache for the current query tab")
        {
            _plugin = plugin;
            _package = package;
            _toolsMenu = toolsMenu;
            _insertPosition = insertPosition;
        }

        public override int ScriptIconIndex => 2;
        public override int IconIndex => 2;

        public override bool IsEnabled()
        {
            return true;
        }

        public override bool OnCommand()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            RefreshIntelliSenseCacheCommand.ExecuteRefresh(_package);
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
            if (btn != null)
            {
                AssignIcon(btn, ScriptIconIndex);
                try
                {
                    string caption = Strings.Get("Menu_RefreshIntelliSenseCache");
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
