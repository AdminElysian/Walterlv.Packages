﻿using System;
using System.Windows;
using System.Windows.Interop;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Diagnostics;
using Size = System.Windows.Size;
using Lsj.Util.Win32;
using Lsj.Util.Win32.Enums;
using Walterlv.Windows.Media;

namespace Walterlv.Windows.Interop
{
    /// <summary>
    /// 包装一个 <see cref="Window"/> 成为一个 WPF 控件。
    /// </summary>
    internal class WindowWrapper : FrameworkElement
    {
        static WindowWrapper()
        {
            FocusableProperty.OverrideMetadata(typeof(WindowWrapper), new UIPropertyMetadata(true));
            FocusVisualStyleProperty.OverrideMetadata(typeof(WindowWrapper), new FrameworkPropertyMetadata(null));
        }

        /// <summary>
        /// 创建包装句柄为 <paramref name="childHandle"/> 窗口的 <see cref="WindowWrapper"/> 的实例。
        /// </summary>
        /// <param name="childHandle">要包装的窗口的句柄。</param>
        public WindowWrapper(IntPtr childHandle)
        {
            // 初始化。
            Handle = childHandle;

            // 监听事件。
            IsVisibleChanged += OnIsVisibleChanged;
            GotFocus += OnGotFocus;
            PreviewKeyDown += OnKeyDown;

            // 设置窗口样式为子窗口。这里的样式值与 HwndHost/HwndSource 成对时设置的值一模一样。
            User32.SetWindowLong(childHandle,
                GetWindowLongIndexes.GWL_STYLE,
                (IntPtr)(WindowStyles.WS_CHILDWINDOW | WindowStyles.WS_VISIBLE | WindowStyles.WS_CLIPCHILDREN));
        }

        /// <summary>
        /// 获取包装的窗口的句柄。
        /// </summary>
        public IntPtr Handle { get; }

        private async void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            var parent = Window.GetWindow(this);
            if (IsVisible)
            {
                LayoutUpdated -= OnLayoutUpdated;
                LayoutUpdated += OnLayoutUpdated;
                await ShowChildAsync().ConfigureAwait(false);
            }
            else
            {
                LayoutUpdated -= OnLayoutUpdated;
                await HideChildAsync().ConfigureAwait(false);
            }
        }

        private void OnGotFocus(object sender, RoutedEventArgs e)
        {
            // 设置子窗口获取焦点。
            // 这样，Tab 键的切换以及快捷键将仅在 Shell 端生效。
            User32.SetFocus(Handle);
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            // 当此控件获取焦点的时候，将吞掉全部的键盘事件：
            // 1. 将所有事件转发到子窗口；
            // 2. 避免按键被父窗口响应（此控件获取焦点可理解为已经切换到子窗口了，不能让父窗口处理事件）；
            // 3. 避免焦点转移到其他控件。
            e.Handled = true;
        }

        protected override Size MeasureOverride(Size availableSize) => default;

        protected override Size ArrangeOverride(Size finalSize) =>
            // _ = ArrangeChildAsync(finalSize);
            // Dispatcher.InvokeAsync(() => ArrangeChildAsync(finalSize), DispatcherPriority.Loaded);
            finalSize;

        private void OnLayoutUpdated(object? sender, EventArgs e)
        {
            // 最终决定在 LayoutUpdated 事件里面更新子窗口的位置和尺寸，因为：
            //  1. Arrange 仅在大小改变的时候才会触发，这使得如果外面更新了 Margin 等导致位置改变大小不变时（例如窗口最大化），窗口位置不会刷新。
            //  2. 使用 Loaded 优先级延迟布局可以解决以上问题，但会导致布局更新不及时，部分依赖于布局的代码（例如拖拽调整窗口大小）会计算错误。
            _ = ArrangeChildAsync(new Size(ActualWidth, ActualHeight));
        }

        /// <summary>
        /// 显示子窗口。
        /// </summary>
        private async Task ShowChildAsync()
        {
            // 计算父窗口的句柄。
            var hwndParent = PresentationSource.FromVisual(this) is HwndSource parentSource
                ? parentSource.Handle
                : IntPtr.Zero;

            // 连接子窗口。
            // 注意连接子窗口后，窗口的消息循环会强制同步，这可能降低 UI 的响应性能。详情请参考：
            // https://blog.walterlv.com/post/all-processes-freezes-if-their-windows-are-connected-via-setparent.html
            User32.SetParent(Handle, hwndParent);
            Debug.WriteLine("[Window] 嵌入");

            // 显示子窗口。
            // 注意调用顺序：先嵌入子窗口，这可以避免任务栏中出现窗口图标。
            User32.ShowWindow(Handle, ShowWindowCommands.SW_SHOW);
            // 发送窗口已取消激活消息。
            const int WA_ACTIVE = 0x0001;
            User32.SendMessage(Handle, WindowsMessages.WM_ACTIVATE, WA_ACTIVE, IntPtr.Zero);
        }

        /// <summary>
        /// 隐藏子窗口。
        /// </summary>
        private async Task HideChildAsync()
        {
            // 发送窗口已取消激活消息。
            const int WA_INACTIVE = 0x0000;
            User32.SendMessage(Handle, WindowsMessages.WM_ACTIVATE, WA_INACTIVE, IntPtr.Zero);
            // 隐藏子窗口。
            User32.ShowWindow(Handle, ShowWindowCommands.SW_HIDE);
            // 显示到奇怪的地方。
            RawMoveWindow(-32000, -32000);

            // 断开子窗口的连接。
            // 这是为了避免一直连接窗口对 UI 响应性能的影响。详情请参考：
            // https://blog.walterlv.com/post/all-processes-freezes-if-their-windows-are-connected-via-setparent.html
            User32.SetParent(Handle, IntPtr.Zero);
            Debug.WriteLine("[Window] 取出");
        }

        /// <summary>
        /// 布局子窗口。
        /// </summary>
        /// <param name="size">设定子窗口的显示尺寸。</param>
        private async Task ArrangeChildAsync(Size size)
        {
            var presentationSource = PresentationSource.FromVisual(this);
            if (presentationSource is null)
            {
                // 因为此方法可能被延迟执行，所以可能此元素已经不在可视化树了，这时会进入此分支。
                return;
            }

            // 获取子窗口相对于父窗口的相对坐标。
            var transform = TransformToAncestor(presentationSource.RootVisual);
            var scalingFactor = this.GetScalingRatioToDevice();
            var offset = transform.Transform(default);

            // 转移到后台线程执行代码，这可以让 UI 短暂地立刻响应。
            // await Dispatcher.ResumeBackgroundAsync();

            // 移动子窗口到合适的布局位置。
            var x = (int)(offset.X * scalingFactor.Width);
            var y = (int)(offset.Y * scalingFactor.Height);
            var w = (int)(size.Width * scalingFactor.Width);
            var h = (int)(size.Height * scalingFactor.Height);
            User32.MoveWindow(Handle, x, y, w, h, true);
        }

        /// <summary>
        /// 移动当前子窗口到某个特定的 Win32 坐标的位置。
        /// </summary>
        /// <param name="x">屏幕坐标 X（像素单位）。</param>
        /// <param name="y">屏幕坐标 Y（像素单位）。</param>
        private void RawMoveWindow(int x, int y)
        {
            if (User32.GetWindowRect(Handle, out var rect))
            {
                User32.MoveWindow(Handle, x, y, rect.right - rect.left, rect.bottom - rect.top, true);
            }
        }
    }
}
