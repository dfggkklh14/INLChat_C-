using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Client.Utility;
using Microsoft.Win32;

namespace Client.Utility
{
    public partial class AvatarControl : UserControl
    {
        private static readonly Uri DefaultAvatarUri = new Uri("pack://application:,,,/icon/default_avatar.png");
        private Action<BitmapImage> _uploadCallback;

        public Action<BitmapImage> UploadCallback
        {
            get => _uploadCallback;
            set => _uploadCallback = value;
        }

        public static readonly DependencyProperty ShowButtonOnHoverProperty =
            DependencyProperty.Register(
                "ShowButtonOnHover",
                typeof(bool),
                typeof(AvatarControl),
                new PropertyMetadata(true));

        public static readonly DependencyProperty AvatarIdProperty =
            DependencyProperty.Register(
                "AvatarId",
                typeof(string),
                typeof(AvatarControl),
                new PropertyMetadata(null, OnAvatarIdChanged));

        public static readonly DependencyProperty IsFromRegisterPageProperty =
            DependencyProperty.Register(
                "IsFromRegisterPage",
                typeof(bool),
                typeof(AvatarControl),
                new PropertyMetadata(false, OnIsFromRegisterPageChanged));

        public bool ShowButtonOnHover
        {
            get => (bool)GetValue(ShowButtonOnHoverProperty);
            set => SetValue(ShowButtonOnHoverProperty, value);
        }

        public string AvatarId
        {
            get => (string)GetValue(AvatarIdProperty);
            set => SetValue(AvatarIdProperty, value);
        }

        public bool IsFromRegisterPage
        {
            get => (bool)GetValue(IsFromRegisterPageProperty);
            set => SetValue(IsFromRegisterPageProperty, value);
        }

        public AvatarControl()
        {
            InitializeComponent();
            InitializeAvatar();
        }

        private static void OnAvatarIdChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (AvatarControl)d;
            control.InitializeAvatar();
        }

        private static void OnIsFromRegisterPageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (AvatarControl)d;
            control.InitializeAvatar();
        }

        private void InitializeAvatar()
        {
            try
            {
                if (IsFromRegisterPage)
                {
                    // 注册页面：直接加载默认头像
                    var defaultImage = new BitmapImage(DefaultAvatarUri);
                    AvatarImage.Source = defaultImage;
                }
                else
                {
                    // 非注册页面：根据 AvatarId 查询文件
                    if (!string.IsNullOrEmpty(AvatarId))
                    {
                        string avatarPath = CacheHelper.GetAvatarPath(AvatarId);
                        if (!string.IsNullOrEmpty(avatarPath) && File.Exists(avatarPath))
                        {
                            var bitmap = LoadBitmapImage(avatarPath);
                            if (bitmap != null)
                            {
                                AvatarImage.Source = bitmap;
                                return;
                            }
                        }
                    }
                    // AvatarId 为空或文件未找到，回退到默认头像
                    var defaultImage = new BitmapImage(DefaultAvatarUri);
                    AvatarImage.Source = defaultImage;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"图像加载失败: {ex.Message}");
                // 回退到灰色占位图
                var rtb = new RenderTargetBitmap(100, 100, 96, 96, PixelFormats.Pbgra32);
                var drawingVisual = new DrawingVisual();
                using (var dc = drawingVisual.RenderOpen())
                {
                    dc.DrawRectangle(
                        new SolidColorBrush(Color.FromRgb(240, 240, 240)),
                        null,
                        new Rect(0, 0, 100, 100));
                }
                rtb.Render(drawingVisual);
                AvatarImage.Source = rtb;
            }
        }

        private BitmapImage LoadBitmapImage(string filePath)
        {
            try
            {
                if (!File.Exists(filePath) || new FileInfo(filePath).Length == 0)
                {
                    Console.WriteLine($"图像文件不存在或为空: {filePath}");
                    return null;
                }

                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(filePath, UriKind.Absolute);
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                bitmap.EndInit();
                bitmap.Freeze();
                return bitmap;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"加载图像失败: {filePath}, 错误: {ex.Message}");
                return null;
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