using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using AxialSqlTools.IntelliSense;

namespace IntelliSenseMouseTests
{
    /// <summary>
    /// 补全弹框「鼠标选择建议后编辑器卡死」回归测试。
    /// 直接编译并驱动生产类 AxialSqlTools.IntelliSense.CompletionListWindow（真实 WPF、真实布局、真实 hit-test），
    /// 而不是对源码做文本断言。
    ///
    /// 退出码 0 = 全部通过；1 = 有失败。
    /// </summary>
    internal static class Program
    {
        private static readonly List<string> Failures = new List<string>();
        private static int _passed;

        [STAThread]
        private static int Main()
        {
            try
            {
                RunOnStaDispatcher(() =>
                {
                    Console.WriteLine("== IntelliSense 补全弹框鼠标选择回归测试 ==");
                    Console.WriteLine();

                    Test01_HitTargetIsContentElement();
                    Test02_NaiveWalkThrows();
                    Test03_ProductionSelectHandlesRunHitTarget();
                    Test04_ProductionSelectVisualTargets();
                    Test05_MouseUpCommitIsDeferred();
                });
            }
            catch (Exception ex)
            {
                Failures.Add("harness crashed: " + ex);
                Console.WriteLine(ex);
            }

            Console.WriteLine();
            Console.WriteLine("passed=" + _passed + "  failed=" + Failures.Count);
            foreach (var f in Failures) Console.WriteLine("FAIL: " + f);
            Console.WriteLine(Failures.Count == 0 ? "RESULT: PASS" : "RESULT: FAIL");
            return Failures.Count == 0 ? 0 : 1;
        }

        // ---------- 01：命中目标到底是什么 ----------

        private static void Test01_HitTargetIsContentElement()
        {
            using (var host = ProductionWindow.Create())
            {
                var hit = host.HitTargetOfNameText();
                Console.WriteLine("  [01] 名称文本命中目标 = " + (hit == null ? "<null>" : hit.GetType().FullName));
                Check(hit != null, "01a 列表项名称文本可被真实命中测试定位");
                Check(hit is ContentElement && !(hit is Visual),
                    "01b 命中目标是 ContentElement（Run）而不是 Visual —— 卡死根因");
            }
        }

        // ---------- 02：把缺陷固化成回归断言（朴素遍历必抛） ----------

        private static void Test02_NaiveWalkThrows()
        {
            using (var host = ProductionWindow.Create())
            {
                var hit = host.HitTargetOfNameText();
                Exception error = null;
                try { CompletionListHarness.NaiveFindAncestor<ListBoxItem>(hit); }
                catch (Exception ex) { error = ex; }

                Console.WriteLine("  [02] VisualTreeHelper.GetParent 朴素遍历 -> "
                    + (error == null ? "未抛异常" : error.GetType().Name));
                Check(error != null,
                    "02a 朴素 VisualTreeHelper.GetParent 遍历对真实命中目标抛异常（该写法不得再出现）");
            }
        }

        // ---------- 03：生产 SelectItemUnderMouse 必须接受 Run 命中（修复点） ----------

        private static void Test03_ProductionSelectHandlesRunHitTarget()
        {
            using (var host = ProductionWindow.Create())
            {
                host.SetSelectedIndex(1);                       // 先把选择挪到第二项
                var hitRun = host.HitTargetOfNameText();        // 第一项名称文本 -> Run

                Exception error = null;
                try { host.InvokeSelectItemUnderMouse(hitRun); }
                catch (TargetInvocationException tie) { error = tie.InnerException ?? tie; }
                catch (Exception ex) { error = ex; }

                Console.WriteLine("  [03] 生产 SelectItemUnderMouse(Run) -> "
                    + (error == null ? "正常返回" : error.GetType().Name + ": " + error.Message));
                Check(error == null,
                    "03a 处理 Run 命中目标时不抛异常（鼠标点名称文本不再中断输入分派）");

                var selected = host.SelectedItem;
                Check(selected != null && selected.DisplayText == "SELECT",
                    "03b Run 命中目标仍正确选中该项（selected="
                    + (selected == null ? "<null>" : selected.DisplayText) + "）");
            }
        }

        // ---------- 04：Visual 命中目标（留白/字形）行为 ----------

        private static void Test04_ProductionSelectVisualTargets()
        {
            using (var host = ProductionWindow.Create())
            {
                var before = host.SelectedItem;
                host.InvokeSelectItemUnderMouse(host.ListItself);
                Check(ReferenceEquals(before, host.SelectedItem),
                    "04a 命中非候选项区域（ListBox 自身）时不改动选择");

                host.InvokeSelectItemUnderMouse(host.GlyphBorderOfIndex(1));
                var selected = host.SelectedItem;
                Check(selected != null && selected.DisplayText == "SET",
                    "04b Visual 命中目标（Kind 字形 Border）仍能正确选中该项（selected="
                    + (selected == null ? "<null>" : selected.DisplayText) + "）");
            }
        }

        // ---------- 05：鼠标提交必须推迟到路由事件返回之后 ----------

        private static void Test05_MouseUpCommitIsDeferred()
        {
            using (var host = ProductionWindow.Create())
            {
                int commits = 0;
                host.Window.CommitClicked += (s, e) => commits++;

                bool handled = host.InvokeMouseUpHandler();
                Console.WriteLine("  [05] 生产 mouse-up handler handled=" + handled + " commits=" + commits);

                Check(handled, "05a 鼠标抬起被生产 handler 消费（e.Handled=true）");
                Check(commits == 0, "05b 路由事件内不同步提交（commits=" + commits + "）");
                CompletionListHarness.Pump();
                Check(commits == 1, "05c 路由事件返回后异步提交且只提交一次（commits=" + commits + "）");
            }
        }

        // ---------- 基础设施 ----------

        private static void RunOnStaDispatcher(Action body)
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            dispatcher.BeginInvoke(new Action(() =>
            {
                try { body(); }
                catch (Exception ex) { Failures.Add("test body: " + ex.Message); }
                finally { Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Normal); }
            }), DispatcherPriority.Normal);
            Dispatcher.Run();
        }

        private static void Check(bool ok, string name)
        {
            if (ok) { _passed++; Console.WriteLine("  ok  " + name); }
            else { Failures.Add(name); Console.WriteLine("  BAD " + name); }
        }
    }

    /// <summary>把生产 CompletionListWindow 装起来，并暴露测试需要的最小探针。</summary>
    internal sealed class ProductionWindow : IDisposable
    {
        public CompletionListWindow Window { get; }
        private readonly ListBox _list;

        private ProductionWindow(CompletionListWindow window, ListBox list)
        {
            Window = window;
            _list = list;
        }

        public static ProductionWindow Create()
        {
            var items = new List<CompletionItem>
            {
                new CompletionItem
                {
                    DisplayText = "SELECT", DisplaySuffix = "(rt_kucun.dbo.kucun)",
                    InsertText = "SELECT ", Kind = CompletionKind.Keyword,
                    Description = "关键字", MatchIndices = new[] { 0, 1, 2 }
                },
                new CompletionItem
                {
                    DisplayText = "SET", DisplaySuffix = "",
                    InsertText = "SET ", Kind = CompletionKind.Keyword,
                    Description = "关键字", MatchIndices = new[] { 0 }
                }
            };

            // Opacity=0：不干扰桌面，但布局 / hit-test 与真实弹框完全一致
            var window = new CompletionListWindow { Opacity = 0, Topmost = false };
            window.ShowAt(-4000, -4000, items, IntPtr.Zero);
            CompletionListHarness.Pump();
            window.UpdateLayout();
            CompletionListHarness.Pump();

            var list = (ListBox)GetField(window, "ItemsList");
            list.UpdateLayout();
            CompletionListHarness.Pump();
            return new ProductionWindow(window, list);
        }

        public CompletionItem SelectedItem => _list.SelectedItem as CompletionItem;

        public ListBox ListItself => _list;

        public void SetSelectedIndex(int index) => _list.SelectedIndex = index;

        private ListBoxItem Container(int index)
        {
            _list.UpdateLayout();
            CompletionListHarness.Pump();
            return _list.ItemContainerGenerator.ContainerFromIndex(index) as ListBoxItem;
        }

        /// <summary>命中第一项名称文本（由 Run 内联组成）的真实目标。</summary>
        public DependencyObject HitTargetOfNameText()
        {
            var container = Container(0);
            if (container == null) return null;
            var nameText = CompletionListHarness.FindTextBlockByInlineText(container, "SELECT");
            if (nameText == null || nameText.ActualWidth <= 0) return null;
            var pt = CompletionListHarness.CenterOf(nameText, _list);
            return _list.InputHitTest(pt) as DependencyObject;
        }

        /// <summary>第 index 项的 Kind 字形 Border（18x18）—— Visual 命中目标。</summary>
        public DependencyObject GlyphBorderOfIndex(int index)
        {
            var container = Container(index);
            return container == null ? null : FindGlyphBorder(container);
        }

        /// <summary>反射调用生产私有方法（真实被测代码）。</summary>
        public void InvokeSelectItemUnderMouse(DependencyObject source)
        {
            var method = typeof(CompletionListWindow).GetMethod(
                "SelectItemUnderMouse", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null) throw new MissingMethodException("CompletionListWindow.SelectItemUnderMouse");
            method.Invoke(Window, new object[] { source });
        }

        /// <summary>
        /// 直接驱动生产 handler ItemsList_PreviewMouseLeftButtonUp（反射），
        /// 返回 e.Handled，用于断言"提交推迟到路由事件返回之后"。
        /// </summary>
        public bool InvokeMouseUpHandler()
        {
            var method = typeof(CompletionListWindow).GetMethod(
                "ItemsList_PreviewMouseLeftButtonUp", BindingFlags.Instance | BindingFlags.NonPublic);
            if (method == null)
                throw new MissingMethodException("CompletionListWindow.ItemsList_PreviewMouseLeftButtonUp");

            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
                Source = _list
            };
            method.Invoke(Window, new object[] { _list, args });
            return args.Handled;
        }

        /// <summary>经由真实 WPF 路由投递鼠标抬起（用于覆盖路由可达性）。</summary>
        public bool RaiseMouseUpOnFirstItem()
        {
            var container = Container(0);
            if (container == null) throw new InvalidOperationException("item container not generated");
            var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent
            };
            container.RaiseEvent(args);
            return args.Handled;
        }

        private static Border FindGlyphBorder(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Border border
                    && Math.Abs(border.Width - 18) < 0.5
                    && Math.Abs(border.Height - 18) < 0.5)
                    return border;
                var found = FindGlyphBorder(child);
                if (found != null) return found;
            }
            return null;
        }

        private static object GetField(object instance, string name)
        {
            var type = instance.GetType();
            while (type != null)
            {
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (field != null) return field.GetValue(instance);
                type = type.BaseType;
            }
            throw new MissingFieldException(instance.GetType().FullName, name);
        }

        public void Dispose()
        {
            try { Window.Close(); } catch { }
        }
    }
}
