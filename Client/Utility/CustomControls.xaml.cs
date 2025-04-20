using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Client.Utility
{
    public class StyleTextBox : TextBox
    {
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

        static StyleTextBox()
        {
            DefaultStyleKeyProperty.OverrideMetadata(
                typeof(StyleTextBox),
                new FrameworkPropertyMetadata(typeof(StyleTextBox)));
        }
    }

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
                // 初始化 PasswordBox 的值
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