using System;
using System.ComponentModel.Design;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace AxialSqlTools
{
    internal sealed class TabHistoryWindowCommand
    {
        public const int CommandId = 4156;
        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        private readonly AsyncPackage package;

        private TabHistoryWindowCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));

            var menuCommandID = new CommandID(CommandSet, CommandId);
            var menuItem = new MenuCommand(this.Execute, menuCommandID);
            commandService.AddCommand(menuItem);
        }

        public static TabHistoryWindowCommand Instance { get; private set; }

        private Microsoft.VisualStudio.Shell.IAsyncServiceProvider ServiceProvider => this.package;

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService =
                await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new TabHistoryWindowCommand(package, commandService);
        }

        private void Execute(object sender, EventArgs e)
        {
            ShowWindow(this.package);
        }

        /// <summary>
        /// Opens the Tab History tool window. Invoked by the vsct command and by
        /// TabHistoryCommandProcessor (Aurora registration on the Tools submenu).
        /// </summary>
        public static void ShowWindow(AsyncPackage package)
        {
            package.JoinableTaskFactory.RunAsync(async delegate
            {
                await Microsoft.VisualStudio.Shell.ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                try
                {
                    ToolWindowPane window = await package.ShowToolWindowAsync(
                        typeof(TabHistoryWindow), 0, true, package.DisposalToken);
                    if ((null == window) || (null == window.Frame))
                    {
                        throw new NotSupportedException("Cannot create tool window");
                    }
                    IVsWindowFrame windowFrame = (IVsWindowFrame)window.Frame;
                    windowFrame.SetProperty((int)__VSFPROPID.VSFPROPID_FrameMode, VSFRAMEMODE.VSFM_MdiChild);
                }
                catch (Exception ex)
                {
                    VsShellUtilities.ShowMessageBox(
                        package,
                        "Failed to open Tab History. " + ex.Message,
                        "Axial SQL Tools",
                        OLEMSGICON.OLEMSGICON_CRITICAL,
                        OLEMSGBUTTON.OLEMSGBUTTON_OK,
                        OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
                }
            });
        }
    }
}
