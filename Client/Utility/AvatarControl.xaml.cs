using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace Client.Utility
{
    public partial class AvatarControl : System.Windows.Controls.UserControl
    {
        private static readonly System.Uri DefaultAvatarUri = new System.Uri("pack://application:,,,/icon/default_avatar.png");
        private System.Action<System.Windows.Media.Imaging.BitmapImage> _uploadCallback;

        public System.Action<System.Windows.Media.Imaging.BitmapImage> UploadCallback
        {
            get => _uploadCallback;
            set => _uploadCallback = value;
        }

        public static readonly DependencyProperty ShowButtonOnHoverProperty =
            DependencyProperty.Register("ShowButtonOnHover", typeof(bool), typeof(AvatarControl), new PropertyMetadata(true));

        public bool ShowButtonOnHover
        {
            get => (bool)GetValue(ShowButtonOnHoverProperty);
            set => SetValue(ShowButtonOnHoverProperty, value);
        }

        public AvatarControl()
        {
            InitializeComponent();
            InitializeAvatar();
        }

        private void InitializeAvatar()
        {
            try
            {
                var defaultImage = new System.Windows.Media.Imaging.BitmapImage(DefaultAvatarUri);
                (AvatarImage as System.Windows.Controls.Image).Source = defaultImage;
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine($"图像加载失败: {ex.Message}");
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(100, 100, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                var drawingVisual = new System.Windows.Media.DrawingVisual();
                using (var dc = drawingVisual.RenderOpen())
                {
                    dc.DrawRectangle(
                        new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(240, 240, 240)),
                        null,
                        new System.Windows.Rect(0, 0, 100, 100));
                }
                rtb.Render(drawingVisual);
                (AvatarImage as System.Windows.Controls.Image).Source = rtb;
            }
        }

        private void UploadButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var openFileDialog = new OpenFileDialog
                {
                    Filter = "Image Files (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png"
                };
                if (openFileDialog.ShowDialog() == true)
                {
                    var cropper = new ImageCropperWindow(openFileDialog.FileName);
                    if (cropper.ShowDialog() == true)
                    {
                        var croppedImage = cropper.ResultImage;

                        AvatarImage.Source = croppedImage;
                        _uploadCallback?.Invoke(croppedImage);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"图片上传失败: {ex.Message}");
            }
        }

    }
}