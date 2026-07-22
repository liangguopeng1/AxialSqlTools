namespace AxialSqlTools
{
    using AxialSqlTools.Properties;
    using System;
    using System.Diagnostics;
    using System.Diagnostics.CodeAnalysis;
    using System.IO;
    using System.Reflection;
    using System.Windows;
    using System.Windows.Controls;
    using System.Windows.Navigation;

    /// <summary>
    /// Interaction logic for AboutWindowControl.
    /// </summary>
    public partial class AboutWindowControl : UserControl
    {

        private string _logFolder;
        private readonly ToolWindowThemeController _themeController;
        /// <summary>
        /// Initializes a new instance of the <see cref="AboutWindowControl"/> class.
        /// </summary>
        public AboutWindowControl()
        {
            this.InitializeComponent();
            UiLocalization.Apply(this);
            LocalizeAboutHyperlinks();
            Run_LogFolderPrefix.Text = Strings.Get("About_LogFolderPrefix");

            _themeController = new ToolWindowThemeController(this, ApplyThemeBrushResources);

            Version currentVersion = Assembly.GetExecutingAssembly().GetName().Version;
            string currentVersionString = currentVersion.ToString();

            TextBlock_CurrentVersion.Text = $"SSMS extension version {currentVersionString}";

            _logFolder = UserConfigPaths.LogsDirectory;

            HyperlinkText_LogFolder.Text = _logFolder;
        }

        private void LocalizeAboutHyperlinks()
        {
            Run_About_GitHubRepo.Text = Strings.Get("About_GitHubRepo");
            Run_About_ReportBug.Text = Strings.Get("About_ReportBug");
            Run_About_Discussions.Text = Strings.Get("About_Discussions");
            Run_About_Documentation.Text = Strings.Get("About_Documentation");
            Run_About_Releases.Text = Strings.Get("About_Releases");
            Run_About_ReadLicense.Text = Strings.Get("About_ReadLicense");
        }

        private void ApplyThemeBrushResources()
        {
            ToolWindowThemeResources.ApplySharedTheme(this);
        }

        private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            e.Handled = true;
        }

        private void HyperlinkLogFolder_Click(object sender, RoutedEventArgs e)
        {

            if (!Directory.Exists(_logFolder))
            {
                Directory.CreateDirectory(_logFolder);
            }

            // Open the folder in Windows Explorer
            Process.Start("explorer.exe", _logFolder);
        }
    }
}
