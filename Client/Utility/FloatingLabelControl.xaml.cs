// Client.Utility/FloatingLabelControl.xaml.cs
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace Client.Utility
{
    public partial class FloatingLabelControl : UserControl
    {
        public string Message
        {
            get => (string)GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }

        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register(
                nameof(Message),
                typeof(string),
                typeof(FloatingLabelControl),
                new PropertyMetadata(string.Empty)
            );

        /// <summary>
        /// 在 parent 中显示一条浮动提示，offset 控制相对于 parent 的外边距。
        /// 构造时会移除 parent 中已有的 FloatingLabelControl 实例。
        /// </summary>
        public FloatingLabelControl(string message, FrameworkElement parent, Thickness offset)
        {
            InitializeComponent();

            // 设置文本和位置
            Message = message;
            Margin = offset;

            // 如果已经存在其它 FloatingLabelControl，先移除
            if (parent is Panel panel)
            {
                var old = panel.Children
                               .OfType<FloatingLabelControl>()
                               .ToList();
                foreach (var ctrl in old)
                    panel.Children.Remove(ctrl);

                panel.Children.Add(this);
            }
            else if (parent is ContentControl contentControl)
            {
                if (contentControl.Content is FloatingLabelControl oldCc)
                    contentControl.Content = null;
                contentControl.Content = this;
            }
            else
            {
                throw new InvalidOperationException("Parent must be a Panel or ContentControl");
            }

            // 启动动画
            StartAnimation();
        }

        private void StartAnimation()
        {
            // 一段 storyboard：0→1（0.5s），停 0.5s，再 1→0（0.5s）
            var sb = new Storyboard();

            // 淡入
            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromSeconds(0.5)
            };
            Storyboard.SetTarget(fadeIn, LabelBorder);
            Storyboard.SetTargetProperty(fadeIn, new PropertyPath("Opacity"));
            sb.Children.Add(fadeIn);

            // 淡出（在 1.0s 时开始）
            var fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                BeginTime = TimeSpan.FromSeconds(0.5 + 0.5), // 停留 0.5s 后再 0.5s 淡出
                Duration = TimeSpan.FromSeconds(0.5)
            };
            Storyboard.SetTarget(fadeOut, LabelBorder);
            Storyboard.SetTargetProperty(fadeOut, new PropertyPath("Opacity"));
            sb.Children.Add(fadeOut);

            // 动画完成后移除自身
            sb.Completed += (s, e) =>
            {
                if (Parent is Panel p)
                    p.Children.Remove(this);
                else if (Parent is ContentControl cc)
                    cc.Content = null;
            };

            sb.Begin();
        }
    }
}
