using System;
using System.Collections.Generic;
using System.Globalization;
using AxialSqlTools.Properties;
using EnvDTE80;
using Microsoft.VisualStudio.CommandBars;

namespace AxialSqlTools
{
    /// <summary>
    /// Overlays toolbar/menu captions after VSCT loads (VSCT keeps English keys for matching).
    /// Matches both English VSCT captions and previously applied Chinese captions so language can switch live.
    /// </summary>
    public static class MenuTextLocalizer
    {
        // English VSCT/command-bar caption -> Strings resource key
        private static readonly (string EnglishCaption, string ResourceKey)[] Entries =
        {
            ("Axial SQL Tools", "Menu_Toolbar"),
            ("Query Templates", "Menu_QueryTemplates"),
            ("Tools", "Menu_Tools"),
            ("Format Query", "Menu_FormatQuery"),
            ("Export Grid to Excel", "Menu_ExportGridToExcel"),
            ("Export Grid to Email", "Menu_ExportGridToEmail"),
            ("Export Grid as Temp Table", "Menu_ExportGridAsTempTable"),
            ("Export Grid to Google Sheet", "Menu_ExportGridToGoogleSheet"),
            ("Script Definition to New Window", "Menu_ScriptDefinition"),
            ("Quick Search", "Menu_QuickSearch"),
            ("Snippet Manager", "Menu_SnippetManager"),
            ("Select Current Statement", "Menu_SelectCurrentStatement"),
            ("Toggle Block Comment", "Menu_ToggleBlockComment"),
            ("Query History", "Menu_QueryHistory"),
            ("Statistics Summary", "Menu_StatisticsSummary"),
            ("Health Dashboard - Server", "Menu_HealthDashboardServer"),
            ("SQL Server Builds", "Menu_SqlServerBuilds"),
            ("Data Transfer", "Menu_DataTransfer"),
            ("Sync to GitHub", "Menu_SyncToGitHub"),
            ("Refresh Templates", "Menu_RefreshTemplates"),
            ("Open Templates Folder", "Menu_OpenTemplatesFolder"),
            ("Settings", "Menu_Settings"),
            ("About", "Menu_About"),
        };

        public static void Apply(DTE2 application)
        {
            if (application == null)
            {
                return;
            }
            try
            {
                var commandBars = application.CommandBars as CommandBars;
                if (commandBars == null)
                {
                    return;
                }
                var lookup = BuildCaptionToResourceKeyMap();
                foreach (CommandBar bar in commandBars)
                {
                    try
                    {
                        var name = NormalizeCaption(bar?.Name);
                        if (name.IndexOf("Axial", StringComparison.OrdinalIgnoreCase) < 0
                            && !lookup.ContainsKey(name))
                        {
                            continue;
                        }
                        ApplyControl(bar, lookup);
                    }
                    catch
                    {
                    }
                }
            }
            catch
            {
            }
        }

        public static void ApplyFromIde()
        {
            try
            {
                var application = Microsoft.VisualStudio.Shell.Package.GetGlobalService(typeof(EnvDTE.DTE)) as DTE2;
                Apply(application);
            }
            catch
            {
            }
        }

        private static Dictionary<string, string> BuildCaptionToResourceKeyMap()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var zh = CultureInfo.GetCultureInfo("zh-Hans");
            foreach (var entry in Entries)
            {
                AddLookup(map, entry.EnglishCaption, entry.ResourceKey);
                var zhText = Strings.ResourceManager.GetString(entry.ResourceKey, zh);
                AddLookup(map, zhText, entry.ResourceKey);
            }
            return map;
        }

        private static void AddLookup(Dictionary<string, string> map, string caption, string resourceKey)
        {
            var key = NormalizeCaption(caption);
            if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(resourceKey))
            {
                return;
            }
            map[key] = resourceKey;
        }

        private static string NormalizeCaption(string caption)
        {
            if (string.IsNullOrEmpty(caption))
            {
                return string.Empty;
            }
            return caption.Replace("&&", "\0").Replace("&", "").Replace("\0", "&&").Trim();
        }

        private static void ApplyControl(CommandBar bar, Dictionary<string, string> lookup)
        {
            if (bar == null)
            {
                return;
            }
            TrySetCaption(bar, lookup);
            foreach (CommandBarControl control in bar.Controls)
            {
                TrySetCaption(control, lookup);
                if (control is CommandBarPopup popup && popup.CommandBar != null)
                {
                    ApplyControl(popup.CommandBar, lookup);
                }
            }
        }

        private static void TrySetCaption(object target, Dictionary<string, string> lookup)
        {
            try
            {
                if (target is CommandBar bar)
                {
                    if (lookup.TryGetValue(NormalizeCaption(bar.Name), out var resourceKey))
                    {
                        var text = Strings.Get(resourceKey);
                        if (!string.IsNullOrEmpty(text))
                        {
                            try { bar.Name = text; } catch { }
                        }
                    }
                    return;
                }
                if (target is CommandBarControl control)
                {
                    var current = control.Caption;
                    if (string.IsNullOrEmpty(current))
                    {
                        return;
                    }
                    if (lookup.TryGetValue(NormalizeCaption(current), out var resourceKey)
                        || lookup.TryGetValue(current, out resourceKey))
                    {
                        var text = Strings.Get(resourceKey);
                        if (!string.IsNullOrEmpty(text) && !string.Equals(current, text, StringComparison.Ordinal))
                        {
                            control.Caption = text;
                        }
                    }
                }
            }
            catch
            {
            }
        }
    }
}
