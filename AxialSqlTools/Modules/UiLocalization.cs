using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AxialSqlTools.Properties;

namespace AxialSqlTools
{
    /// <summary>
    /// Applies localized text to elements tagged as Tag="loc:ResourceKey".
    /// </summary>
    public static class UiLocalization
    {
        public const string TagPrefix = "loc:";

        public static void Apply(DependencyObject root)
        {
            if (root == null)
            {
                return;
            }
            ApplyNode(root);
            // ColumnDefinition / RowDefinition etc. are DependencyObjects but not Visuals.
            // Only walk FrameworkElement trees; never call VisualTreeHelper on non-Visual nodes.
            if (!(root is FrameworkElement fe))
            {
                return;
            }
            foreach (var child in LogicalTreeHelper.GetChildren(fe))
            {
                if (child is DependencyObject dep)
                {
                    Apply(dep);
                }
            }
            if (!(root is Visual))
            {
                return;
            }
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                Apply(VisualTreeHelper.GetChild(root, i));
            }
        }

        private static void ApplyNode(DependencyObject node)
        {
            if (!(node is FrameworkElement element))
            {
                return;
            }
            var tag = element.Tag as string;
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith(TagPrefix))
            {
                return;
            }
            var key = tag.Substring(TagPrefix.Length);
            var text = Strings.Get(key);
            if (string.IsNullOrEmpty(text))
            {
                return;
            }
            if (element is Window window)
            {
                window.Title = text;
                return;
            }
            if (element is TabItem tabItem)
            {
                tabItem.Header = text;
                return;
            }
            if (element is HeaderedContentControl headered)
            {
                headered.Header = text;
                return;
            }
            if (element is TextBlock textBlock)
            {
                textBlock.Text = text;
                return;
            }
            if (element is ContentControl contentControl && !(element is ComboBox) && !(element is ListBox))
            {
                contentControl.Content = text;
            }
        }
    }
}
