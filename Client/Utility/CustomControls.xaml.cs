using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Client.Utility
{
    public class StyleTextBox : TextBox
    {
        // 现有的 TextPlaceholder 属性
        public static readonly DependencyProperty TextPlaceholderProperty =
            DependencyProperty.Register(
                nameof(TextPlaceholder),
                typeof(string),
                typeof(StyleTextBox),
                new PropertyMetadata(string.Empty));

        public string TextPlaceholder
        {
            get => (string)GetValue(TextPlaceholderProperty);
            set => SetValue(TextPlaceholderProperty, value);
        }

        // 新属性：水印的水平对齐
        public static readonly DependencyProperty WatermarkHorizontalAlignmentProperty =
            DependencyProperty.Register(
                nameof(WatermarkHorizontalAlignment),
                typeof(HorizontalAlignment),
                typeof(StyleTextBox),
                new PropertyMetadata(HorizontalAlignment.Left)); // 默认左对齐

        public HorizontalAlignment WatermarkHorizontalAlignment
        {
            get => (HorizontalAlignment)GetValue(WatermarkHorizontalAlignmentProperty);
            set => SetValue(WatermarkHorizontalAlignmentProperty, value);
        }

        // 新属性：水印的垂直对齐
        public static readonly DependencyProperty WatermarkVerticalAlignmentProperty =
            DependencyProperty.Register(
                nameof(WatermarkVerticalAlignment),
                typeof(VerticalAlignment),
                typeof(StyleTextBox),
                new PropertyMetadata(VerticalAlignment.Center)); // 默认居中对齐

        public VerticalAlignment WatermarkVerticalAlignment
        {
            get => (VerticalAlignment)GetValue(WatermarkVerticalAlignmentProperty);
            set => SetValue(WatermarkVerticalAlignmentProperty, value);
        }

        // 新属性：水印的边距
        public static readonly DependencyProperty WatermarkMarginProperty =
            DependencyProperty.Register(
                nameof(WatermarkMargin),
                typeof(Thickness),
                typeof(StyleTextBox),
                new PropertyMetadata(new Thickness(5, 0, 0, 0))); // 默认左边距 5

        public Thickness WatermarkMargin
        {
            get => (Thickness)GetValue(WatermarkMarginProperty);
            set => SetValue(WatermarkMarginProperty, value);
        }

        static StyleTextBox()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(StyleTextBox),
                new FrameworkPropertyMetadata(typeof(StyleTextBox)));
        }
    }

    // StylePasswordBox 保持不变
    public class StylePasswordBox : Control
    {
        public static readonly DependencyProperty TextPlaceholderProperty =
            DependencyProperty.Register(
                nameof(TextPlaceholder),
                typeof(string),
                typeof(StylePasswordBox),
                new PropertyMetadata(string.Empty));

        public string TextPlaceholder
        {
            get => (string)GetValue(TextPlaceholderProperty);
            set => SetValue(TextPlaceholderProperty, value);
        }

        public static readonly DependencyProperty PasswordProperty =
            DependencyProperty.Register(
                nameof(Password),
                typeof(string),
                typeof(StylePasswordBox),
                new FrameworkPropertyMetadata(
                    string.Empty,
                    FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                    OnPasswordPropertyChanged));

        public string Password
        {
            get => (string)GetValue(PasswordProperty);
            set => SetValue(PasswordProperty, value);
        }

        private PasswordBox _passwordBox;

        static StylePasswordBox()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(StylePasswordBox),
                new FrameworkPropertyMetadata(typeof(StylePasswordBox)));
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            _passwordBox = GetTemplateChild("PART_StyleBox") as PasswordBox;
            if (_passwordBox != null)
            {
                _passwordBox.PasswordChanged += PasswordBox_PasswordChanged;
                _passwordBox.Password = Password;
            }
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
        {
            if (_passwordBox != null)
            {
                Password = _passwordBox.Password;
            }
        }

        private static void OnPasswordPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (StylePasswordBox)d;
            if (control._passwordBox != null && control._passwordBox.Password != (string)e.NewValue)
            {
                control._passwordBox.Password = (string)e.NewValue;
            }
        }
    }
}