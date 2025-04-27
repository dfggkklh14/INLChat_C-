using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace Client.Utility
{
    /// <summary>
    /// ChatBubble.xaml 的交互逻辑
    /// </summary>
    public partial class ChatBubble : UserControl
    {
        public ChatBubble()
        {
            InitializeComponent();
        }

        private void ThumbnailImageLoaded(object sender, RoutedEventArgs e)
        {
            var img = sender as Image;
            if (img != null)
            {
                img.Clip = new RectangleGeometry()
                {
                    Rect = new Rect(0, 0, img.ActualWidth, img.ActualHeight),
                    RadiusX = 8,
                    RadiusY = 8
                };
            }
        }

    }
}