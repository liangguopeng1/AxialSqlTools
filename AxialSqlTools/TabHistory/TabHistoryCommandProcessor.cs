using Aurora;
using Microsoft.VisualStudio.Shell;

namespace AxialSqlTools
{
    /// <summary>
    /// Aurora toolbar command for the Tab History window. Registered explicitly via
    /// CommandRegistry.RegisterCommand so the button always appears on the
    /// "Axial SQL Tools" toolbar even if the vsct command-table resource does not
    /// surface the menu button (SSMS 22 vsct loading quirk).
    /// </summary>
    internal sealed class TabHistoryCommandProcessor : CommandBase
    {
        private readonly AsyncPackage _package;

        public TabHistoryCommandProcessor(Plugin plugin, AsyncPackage package)
            : base("Tab History", "AxialSqlTools.TabHistory", plugin, "Open the Tab History window")
        {
            _package = package;
        }

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
    }
}
