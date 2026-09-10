using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace AxialSqlTools.IntelliSense
{
    /// <summary>
    /// WPF 父节点遍历工具。
    ///
    /// 为什么不能直接用 <see cref="VisualTreeHelper.GetParent(DependencyObject)"/>：
    /// 鼠标/键盘事件的 <c>OriginalSource</c> 可能是 <see cref="Run"/>、<see cref="Span"/> 等
    /// <see cref="ContentElement"/>（补全列表项的名称列就是由 Run 内联组成，见 MatchHighlight），
    /// 它们不是 <see cref="Visual"/>，<c>VisualTreeHelper.GetParent</c> 会抛
    /// InvalidOperationException（"不是 Visual 或 Visual3D"）。
    /// 该异常发生在 WPF 输入分派过程中，会中断鼠标路由并让宿主编辑器失去响应（表现为编辑器卡死），
    /// 且是否命中 Run 取决于鼠标落点（文字 vs 留白）→ 症状呈间歇性。
    /// </summary>
    public static class WpfTreeWalk
    {
        /// <summary>
        /// 取父节点：Visual 走视觉树，ContentElement（Run/Inline 等）走内容树/逻辑树。
        /// 任何异常都吞掉并返回 null —— 该方法的调用点在输入事件路径上，绝不能抛。
        /// </summary>
        public static DependencyObject GetParentSafe(DependencyObject current)
        {
            if (current == null) return null;

            try
            {
                if (current is Visual || current is Visual3D)
                    return VisualTreeHelper.GetParent(current);

                if (current is ContentElement content)
                {
                    DependencyObject parent = ContentOperations.GetParent(content);
                    if (parent != null) return parent;

                    if (content is FrameworkContentElement frameworkContent)
                        return frameworkContent.Parent;
                }

                return LogicalTreeHelper.GetParent(current);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 向上查找最近的 <typeparamref name="T"/> 祖先（含自身）。
        /// 不会抛异常；找不到返回 null。防环：父节点等于自身时终止。
        /// </summary>
        public static T FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T match) return match;

                DependencyObject parent = GetParentSafe(current);
                if (parent == null || ReferenceEquals(parent, current)) return null;
                current = parent;
            }
            return null;
        }
    }
}
