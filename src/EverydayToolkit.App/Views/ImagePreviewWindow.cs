using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace EverydayToolkit.App.Views;

public sealed class ImagePreviewWindow : Window
{
    public ImagePreviewWindow(BitmapSource source)
    {
        Style = (Style)FindResource(typeof(Window));
        Title = $"图片预览 · {source.PixelWidth} × {source.PixelHeight}";
        Width = Math.Min(1040, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(760, SystemParameters.WorkArea.Height - 40);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(20) };
        var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var close = new Button { Content = "关闭", IsCancel = true }; close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Right); toolbar.Children.Add(close);
        var zoom = new ComboBox { Width = 150, ItemsSource = new[] { "适应窗口", "100% 像素" }, SelectedIndex = 0 }; toolbar.Children.Add(zoom);
        DockPanel.SetDock(toolbar, Dock.Top); layout.Children.Add(toolbar);
        var image = new Image { Source = source, Stretch = Stretch.Uniform };
        var scroll = new ScrollViewer { Content = image, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Resize()
        {
            image.Width = zoom.SelectedIndex == 1 ? source.PixelWidth : Math.Max(1, scroll.ActualWidth - 26);
            image.Height = zoom.SelectedIndex == 1 ? source.PixelHeight : Math.Max(1, scroll.ActualHeight - 26);
        }
        scroll.SizeChanged += (_, _) => Resize(); zoom.SelectionChanged += (_, _) => Resize();
        layout.Children.Add(scroll); Content = layout;
        Closed += (_, _) => image.Source = null;
    }
}
