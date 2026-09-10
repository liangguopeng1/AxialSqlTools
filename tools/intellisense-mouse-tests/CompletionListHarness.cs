using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using AxialSqlTools.IntelliSense;

namespace IntelliSenseMouseTests
{
    /// <summary>
    /// 测试辅助：定位列表项里的名称 TextBlock（生产里它由 Run 内联组成）并做真实 hit-test。
    /// 生产项模板结构（CompletionListWindow.xaml）：
    ///   DockPanel
    ///     Border(18x18 Kind 字形) -> TextBlock Text=Glyph     ← 纯 Text（Visual）
    ///     TextBlock local:MatchHighlight.Item                  ← 内容是 Run 内联（ContentElement！）
    ///     TextBlock Text=DisplaySuffix                         ← 纯 Text（Visual）
    /// </summary>
    internal static class CompletionListHarness
    {
        public static void Pump()
        {
            Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
        }

        /// <summary>取 TextBlock 的实际文本：内联优先（生产名称列由 Run 组成）。</summary>
        public static string GetInlineText(TextBlock tb)
        {
            if (tb.Inlines.Count > 0)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var inline in tb.Inlines)
                {
                    if (inline is Run r) sb.Append(r.Text);
                }
                if (sb.Length > 0) return sb.ToString();
            }
            return tb.Text ?? string.Empty;
        }

        public static TextBlock FindTextBlockByInlineText(DependencyObject root, string expectedText)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is TextBlock tb && string.Equals(GetInlineText(tb), expectedText, StringComparison.Ordinal))
                    return tb;
                var found = FindTextBlockByInlineText(child, expectedText);
                if (found != null) return found;
            }
            return null;
        }

        /// <summary>定位项名称文本的中点在 list 坐标系里的位置（真实命中点）。</summary>
        public static Point CenterOf(TextBlock nameText, UIElement relativeTo)
        {
            return nameText.TranslatePoint(
                new Point(Math.Max(1, nameText.ActualWidth / 2), Math.Max(1, nameText.ActualHeight / 2)),
                relativeTo);
        }

        /// <summary>与生产实现同构的朴素遍历（用于把缺陷固化成回归断言）。</summary>
        public static T NaiveFindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }
    }
}
