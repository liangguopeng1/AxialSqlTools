using System;
using System.ComponentModel.Design;
using System.Threading.Tasks;
using System.Windows;
using AxialSqlTools.IntelliSense;
using AxialSqlTools.Properties;
using Microsoft.VisualStudio.Shell;
using NLog;
using Task = System.Threading.Tasks.Task;
using UiStrings = AxialSqlTools.Properties.Strings;

namespace AxialSqlTools
{
    internal sealed class RefreshIntelliSenseCacheCommand
    {
        public const int CommandId = 4157;
        public static readonly Guid CommandSet = new Guid("45457e02-6dec-4a4d-ab22-c9ee126d23c5");

        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
        private readonly AsyncPackage package;

        private RefreshIntelliSenseCacheCommand(AsyncPackage package, OleMenuCommandService commandService)
        {
            this.package = package ?? throw new ArgumentNullException(nameof(package));
            commandService = commandService ?? throw new ArgumentNullException(nameof(commandService));
            var menuCommandID = new CommandID(CommandSet, CommandId);
            commandService.AddCommand(new MenuCommand(Execute, menuCommandID));
        }

        public static RefreshIntelliSenseCacheCommand Instance { get; private set; }

        public static async Task InitializeAsync(AsyncPackage package)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
            OleMenuCommandService commandService = await package.GetServiceAsync(typeof(IMenuCommandService)) as OleMenuCommandService;
            Instance = new RefreshIntelliSenseCacheCommand(package, commandService);
        }

        internal static void ExecuteRefresh(AsyncPackage package)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ScriptFactoryAccess.ConnectionInfo conn = null;
            try
            {
                conn = ScriptFactoryAccess.GetConnectionInfoForTabCacheRefresh();
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "RefreshIntelliSenseCache: GetConnectionInfoForTabCacheRefresh failed");
            }
            if (conn == null || string.IsNullOrWhiteSpace(conn.ServerName))
            {
                MessageBox.Show("请先打开已连接的查询标签页。", UiStrings.Common_Error,
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var captured = conn;
            Logger.Info("Refresh IntelliSense cache from Tools menu (tab) server={0} database={1}",
                captured.ServerName, captured.Database);
            _ = package.JoinableTaskFactory.RunAsync(async () =>
            {
                try
                {
                    await MetadataCacheRefreshService.Instance.RefreshWithProgressWindowAsync(captured);
                }
                catch (Exception ex)
                {
                    Logger.Warn(ex, "RefreshIntelliSenseCache failed for {0}", captured.ServerName);
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    MessageBox.Show(ex.Message, UiStrings.Common_Error,
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            });
        }

        private void Execute(object sender, EventArgs e)
        {
            ExecuteRefresh(package);
        }
    }
}
