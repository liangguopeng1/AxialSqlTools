using System;

// 测试工程不引用 VS SDK / SSMS 程序集，这里给出生产代码实际用到的最小存根，
// 让 AxialSqlTools 的真实源文件（CompletionListWindow.xaml.cs、ToolWindowThemeSupport.cs）
// 能在测试宿主中原样编译运行。仅补齐签名，不改变被测行为。

namespace Microsoft.VisualStudio.Shell
{
    public static class ThreadHelper
    {
        public static void ThrowIfNotOnUIThread()
        {
        }
    }
}

namespace Microsoft.VisualStudio.PlatformUI
{
    public static class EnvironmentColors
    {
        public static readonly object ToolWindowBackgroundBrushKey = new object();
        public static readonly object ToolWindowTextBrushKey = new object();
        public static readonly object ToolWindowBorderBrushKey = new object();
        public static readonly object ControlLinkTextBrushKey = new object();
    }

    public sealed class ThemeChangedEventArgs : EventArgs
    {
    }

    public delegate void ThemeChangedEventHandler(ThemeChangedEventArgs e);

    public static class VSColorTheme
    {
        public static event ThemeChangedEventHandler ThemeChanged;

        public static void RaiseThemeChanged()
        {
            ThemeChanged?.Invoke(new ThemeChangedEventArgs());
        }
    }
}
