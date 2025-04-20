using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
using PixelFormats = System.Windows.Media.PixelFormats;
using SWPoint = System.Windows.Point;
using SWMouseEventArgs = System.Windows.Input.MouseEventArgs;
using SWMouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using SWKeyEventArgs = System.Windows.Input.KeyEventArgs;
using Microsoft.Extensions.Logging; // 已有
using Microsoft.Extensions.Logging.Abstractions; // 新增

namespace Client.Utility
{
    public partial class ImageCropperWindow : Window
    {
        private readonly string _imagePath;
        private string _processedImagePath;
        private double _scale, _minScale;
        private SWPoint _translation, _lastMousePos;
        private bool _isDragging;
        private const int OutputSize = 100;
        private readonly ILogger<ImageCropperWindow> _logger; // 添加日志记录器

        public BitmapImage ResultImage { get; private set; }

        public ImageCropperWindow(string imagePath)
        {
            InitializeComponent();
            _imagePath = imagePath;

            // 获取 App 的日志工厂并创建日志记录器
            try
            {
                if (Application.Current is App app)
                {
                    _logger = app.LogFactory.CreateLogger<ImageCropperWindow>();
                    _logger.LogInformation("ImageCropperWindow 初始化开始，图像路径: {ImagePath}", _imagePath);
                }
                else
                {
                    _logger = NullLogger<ImageCropperWindow>.Instance; // 回退到空日志记录器
                    _logger.LogWarning("无法获取 App 实例，使用 NullLogger");
                }
            }
            catch (Exception ex)
            {
                _logger = NullLogger<ImageCropperWindow>.Instance;
                _logger.LogError(ex, "初始化日志记录器失败");
            }
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _logger.LogDebug("Window_Loaded 开始执行");
                LoadingOverlay.Visibility = Visibility.Visible;

                _logger.LogInformation("开始规范化图像 DPI，路径: {ImagePath}", _imagePath);
                _processedImagePath = await NormalizeImageDpiAsync(_imagePath);
                _logger.LogInformation("图像 DPI 规范化完成，临时路径: {ProcessedImagePath}", _processedImagePath);

                _logger.LogDebug("开始加载图像");
                var bitmap = await LoadImageAsync(_processedImagePath);
                SourceImage.Source = bitmap;
                _logger.LogInformation("图像加载完成，像素尺寸: {Width}x{Height}", bitmap.PixelWidth, bitmap.PixelHeight);

                if (CropRect.ActualWidth == 0 || CropRect.ActualHeight == 0)
                {
                    _logger.LogDebug("CropRect 大小未就绪，等待 SizeChanged 事件");
                    CropRect.SizeChanged += (s, ev) =>
                    {
                        _logger.LogDebug("CropRect SizeChanged 触发，初始化裁剪区域");
                        InitializeCropRect(bitmap);
                    };
                }
                else
                {
                    _logger.LogDebug("CropRect 大小已就绪，直接初始化裁剪区域");
                    InitializeCropRect(bitmap);
                }

                // 验证 CropRect 和 ConfirmButton 的状态
                _logger.LogDebug("CropRect 状态: Visibility={Visibility}, Width={Width}, Height={Height}", CropRect.Visibility, CropRect.ActualWidth, CropRect.ActualHeight);
                _logger.LogDebug("ConfirmButton 状态: Visibility={Visibility}, Width={Width}, Height={Height}", ConfirmButton.Visibility, ConfirmButton.ActualWidth, ConfirmButton.ActualHeight);

                MainCanvas.PreviewMouseDown += (s, ev) =>
                {
                    _logger.LogDebug("MainCanvas PreviewMouseDown 触发，位置: ({X}, {Y})", ev.GetPosition(MainCanvas).X, ev.GetPosition(MainCanvas).Y);
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "加载图片失败");
                MessageBox.Show($"加载图片失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
                Close();
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                _logger.LogDebug("Window_Loaded 执行完成");
            }
        }

        private void InitializeCropRect(BitmapImage bitmap)
        {
            double iw = bitmap.PixelWidth;
            double ih = bitmap.PixelHeight;
            double cw = CropRect.ActualWidth;
            double ch = CropRect.ActualHeight;

            _logger.LogDebug("InitializeCropRect: CropRect 大小 Width={Width}, Height={Height}", cw, ch);
            if (cw == 0 || ch == 0)
            {
                _logger.LogWarning("CropRect 大小无效，跳过初始化");
                return;
            }

            // 初始化缩放，确保裁剪区域完全覆盖图像
            _minScale = Math.Max(cw / iw, ch / ih);
            _scale = _minScale;
            // 确保缩放后裁剪区域不超过图像尺寸
            if (cw / _scale > iw || ch / _scale > ih)
            {
                _scale = Math.Min(cw / iw, ch / ih);
                _logger.LogDebug("调整缩放以适应图像尺寸，新的缩放: {Scale}", _scale);
            }

            var cropRectPos = CropRect.TranslatePoint(new SWPoint(0, 0), MainCanvas);
            double centerX = cropRectPos.X + cw / 2;
            double centerY = cropRectPos.Y + ch / 2;
            _translation = new SWPoint(centerX - iw * _scale / 2, centerY - ih * _scale / 2);

            UpdateTransform();
            UpdateOverlayClip();
            MainCanvas.SizeChanged -= (s, ev) => UpdateOverlayClip();
            MainCanvas.SizeChanged += (s, ev) => UpdateOverlayClip();
            _logger.LogInformation("裁剪区域初始化完成，缩放: {Scale}, 平移: ({X}, {Y})", _scale, _translation.X, _translation.Y);
        }

        private async Task<string> NormalizeImageDpiAsync(string inputPath)
        {
            return await Task.Run(() =>
            {
                _logger.LogDebug("NormalizeImageDpiAsync: 开始处理图像 DPI，输入路径: {InputPath}", inputPath);
                string tempPath = Path.Combine(Path.GetTempPath(), $"normalized_{Guid.NewGuid()}.png");
                try
                {
                    using (var image = SixLabors.ImageSharp.Image.Load(inputPath))
                    {
                        image.Metadata.VerticalResolution = 96;
                        image.Metadata.HorizontalResolution = 96;
                        image.Metadata.ResolutionUnits = SixLabors.ImageSharp.Metadata.PixelResolutionUnit.PixelsPerInch;
                        image.Save(tempPath, new PngEncoder());
                        _logger.LogDebug("NormalizeImageDpiAsync: 图像 DPI 处理完成，输出路径: {OutputPath}", tempPath);
                    }
                    return tempPath;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "NormalizeImageDpiAsync: 处理图像 DPI 失败，输入路径: {InputPath}", inputPath);
                    throw;
                }
            });
        }

        private async Task<BitmapImage> LoadImageAsync(string path)
        {
            return await Task.Run(() =>
            {
                _logger.LogDebug("LoadImageAsync: 开始加载图像，路径: {Path}", path);
                try
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    _logger.LogDebug("LoadImageAsync: 图像加载成功，路径: {Path}", path);
                    return bitmap;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "LoadImageAsync: 加载图像失败，路径: {Path}", path);
                    throw;
                }
            });
        }

        private void UpdateOverlayClip()
        {
            var cropRectPos = CropRect.TranslatePoint(new SWPoint(0, 0), MainCanvas);
            var cropRect = new Rect(cropRectPos.X, cropRectPos.Y, CropRect.ActualWidth, CropRect.ActualHeight);
            var g1 = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, MainCanvas.ActualWidth, MainCanvas.ActualHeight));
            var g2 = new System.Windows.Media.RectangleGeometry(cropRect);
            OverlayRect.Clip = new System.Windows.Media.CombinedGeometry(System.Windows.Media.GeometryCombineMode.Exclude, g1, g2);
            OverlayRect.Width = MainCanvas.ActualWidth;
            OverlayRect.Height = MainCanvas.ActualHeight;
            _logger.LogDebug("UpdateOverlayClip: 遮罩裁剪更新，CropRect 位置: ({X}, {Y}), 大小: {Width}x{Height}", cropRect.X, cropRect.Y, cropRect.Width, cropRect.Height);
        }

        private void UpdateTransform()
        {
            ImageScaleTransform.ScaleX = _scale;
            ImageScaleTransform.ScaleY = _scale;
            ImageTranslateTransform.X = _translation.X;
            ImageTranslateTransform.Y = _translation.Y;
            _logger.LogDebug("UpdateTransform: 变换更新，缩放: {Scale}, 平移: ({X}, {Y})", _scale, _translation.X, _translation.Y);
        }

        private void ConstrainTranslation()
        {
            var bmp = (BitmapImage)SourceImage.Source;
            var cropRectPos = CropRect.TranslatePoint(new SWPoint(0, 0), MainCanvas);
            var cropRect = new Rect(cropRectPos.X, cropRectPos.Y, CropRect.ActualWidth, CropRect.ActualHeight);
            var imgRect = new Rect(_translation.X, _translation.Y, bmp.PixelWidth * _scale, bmp.PixelHeight * _scale);

            double dx = 0, dy = 0;
            if (imgRect.Width < cropRect.Width || imgRect.Height < cropRect.Height)
            {
                dx = cropRect.X + (cropRect.Width - imgRect.Width) / 2 - imgRect.X;
                dy = cropRect.Y + (cropRect.Height - imgRect.Height) / 2 - imgRect.Y;
            }
            else
            {
                if (imgRect.Left > cropRect.Left) dx = cropRect.Left - imgRect.Left;
                else if (imgRect.Right < cropRect.Right) dx = cropRect.Right - imgRect.Right;
                if (imgRect.Top > cropRect.Top) dy = cropRect.Top - imgRect.Top;
                else if (imgRect.Bottom < cropRect.Bottom) dy = cropRect.Bottom - imgRect.Bottom;
            }
            _translation = new SWPoint(_translation.X + dx, _translation.Y + dy);
            _logger.LogDebug("ConstrainTranslation: 约束平移，dx={Dx}, dy={Dy}, 新平移: ({X}, {Y})", dx, dy, _translation.X, _translation.Y);
        }

        private void OnMouseDown(object sender, SWMouseButtonEventArgs e)
        {
            _logger.LogDebug("OnMouseDown 触发");
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _logger.LogDebug("鼠标左键按下，开始拖动，位置: ({X}, {Y})", e.GetPosition(MainCanvas).X, e.GetPosition(MainCanvas).Y);
                _isDragging = true;
                _lastMousePos = e.GetPosition(MainCanvas);
                MainCanvas.CaptureMouse();
            }
        }

        private void OnMouseMove(object sender, SWMouseEventArgs e)
        {
            if (_isDragging)
            {
                _logger.LogDebug("OnMouseMove 触发，拖动中");
                var pos = e.GetPosition(MainCanvas);
                var delta = pos - _lastMousePos;
                _lastMousePos = pos;
                _translation = new SWPoint(_translation.X + delta.X, _translation.Y + delta.Y);
                ConstrainTranslation();
                UpdateTransform();
            }
        }

        private void OnMouseUp(object sender, SWMouseButtonEventArgs e)
        {
            if (_isDragging)
            {
                _logger.LogDebug("OnMouseUp 触发，停止拖动");
                _isDragging = false;
                MainCanvas.ReleaseMouseCapture();
            }
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            _logger.LogDebug("OnMouseWheel 触发，Delta: {Delta}", e.Delta);
            var pos = e.GetPosition(MainCanvas);
            double factor = e.Delta > 0 ? 1.1 : 1.0 / 1.1;
            double newScale = Math.Max(_minScale, _scale * factor);
            double ix = (pos.X - _translation.X) / _scale;
            double iy = (pos.Y - _translation.Y) / _scale;
            _scale = newScale;
            _translation = new SWPoint(pos.X - ix * _scale, pos.Y - iy * _scale);
            ConstrainTranslation();
            UpdateTransform();
            _logger.LogDebug("缩放更新，新的缩放: {Scale}", _scale);
        }

        private async void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _logger.LogInformation("ConfirmButton_Click: 开始裁剪图像");
                LoadingOverlay.Visibility = Visibility.Visible;
                ConfirmButton.IsEnabled = false;

                var bmp = (BitmapImage)SourceImage.Source;
                var cropRectPos = CropRect.TranslatePoint(new SWPoint(0, 0), MainCanvas);
                var cropRect = new Rect(cropRectPos.X, cropRectPos.Y, CropRect.ActualWidth, CropRect.ActualHeight);
                double x = (cropRect.X - _translation.X) / _scale;
                double y = (cropRect.Y - _translation.Y) / _scale;
                double w = cropRect.Width / _scale;
                double h = cropRect.Height / _scale;

                // 确保 w 和 h 不超过图像尺寸，处理浮点数精度问题
                w = Math.Min(w, bmp.PixelWidth);
                h = Math.Min(h, bmp.PixelHeight);

                // 确保 Math.Clamp 的 max 参数非负
                double maxX = Math.Max(0, bmp.PixelWidth - w);
                double maxY = Math.Max(0, bmp.PixelHeight - h);

                x = Math.Clamp(x, 0, maxX);
                y = Math.Clamp(y, 0, maxY);

                _logger.LogDebug("裁剪参数: x={X}, y={Y}, width={Width}, height={Height}", x, y, w, h);

                ResultImage = await Task.Run(async () =>
                {
                    using (var img = SixLabors.ImageSharp.Image.Load(_processedImagePath))
                    {
                        img.Mutate(ctx => ctx
                            .Crop(new SixLabors.ImageSharp.Rectangle((int)x, (int)y, (int)w, (int)h))
                            .Resize(OutputSize, OutputSize));
                        using (var ms = new MemoryStream())
                        {
                            await img.SaveAsPngAsync(ms);
                            ms.Position = 0;
                            var result = new BitmapImage();
                            result.BeginInit();
                            result.CacheOption = BitmapCacheOption.OnLoad;
                            result.StreamSource = ms;
                            result.EndInit();
                            result.Freeze();
                            return result;
                        }
                    }
                });

                _logger.LogInformation("图像裁剪完成");
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "裁剪图片失败");
                MessageBox.Show($"裁剪图片失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                ConfirmButton.IsEnabled = true;
                _logger.LogDebug("ConfirmButton_Click 执行完成");
            }
        }

        private void Window_KeyDown(object sender, SWKeyEventArgs e)
        {
            _logger.LogDebug("Window_KeyDown 触发，键: {Key}", e.Key);
            if (e.Key == Key.Enter)
            {
                ConfirmButton_Click(null, null);
            }
            else if (e.Key == Key.Escape)
            {
                _logger.LogInformation("用户按下 Escape 键，关闭窗口");
                DialogResult = false;
                Close();
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            try
            {
                if (!string.IsNullOrEmpty(_processedImagePath) && File.Exists(_processedImagePath))
                {
                    File.Delete(_processedImagePath);
                    _logger.LogDebug("临时文件已删除: {ProcessedImagePath}", _processedImagePath);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除临时文件失败: {ProcessedImagePath}", _processedImagePath);
            }
            _logger.LogInformation("ImageCropperWindow 已关闭");
        }
    }
}