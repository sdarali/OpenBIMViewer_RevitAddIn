using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OpenBimViewer.Revit.UI
{
    /// <summary>What the user picked in the image viewer; the panel carries it out.</summary>
    internal enum ImageAction
    {
        None,
        ShowInRevit,
        Markup,
        ReplaceWithCurrentView,
        ReplaceFromFile,
    }

    /// <summary>An issue or comment image full size, with what can be done to it.</summary>
    internal sealed class ImageViewerWindow : Window
    {
        private readonly byte[] _png;

        public ImageAction Action { get; private set; }

        private ImageViewerWindow(byte[] png, string title, bool hasCamera, bool canReplace)
        {
            _png = png;
            var bitmap = ImageTools.Decode(png);
            Title = title;
            Width = Math.Min(Math.Max(480, bitmap.PixelWidth + 40), SystemParameters.WorkArea.Width * 0.85);
            Height = Math.Min(Math.Max(360, bitmap.PixelHeight + 110), SystemParameters.WorkArea.Height * 0.85);
            ShowInTaskbar = false;
            FontSize = 12;

            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var scroller = new ScrollViewer
            {
                Content = image,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Background = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
            };
            var actualSize = new CheckBox { Content = "Actual size", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            actualSize.Checked += (_, __) => SetActualSize(true);
            actualSize.Unchecked += (_, __) => SetActualSize(false);

            void SetActualSize(bool actual)
            {
                image.Stretch = actual ? Stretch.None : Stretch.Uniform;
                image.Width = actual ? bitmap.PixelWidth : double.NaN;
                image.Height = actual ? bitmap.PixelHeight : double.NaN;
                scroller.HorizontalScrollBarVisibility = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
                scroller.VerticalScrollBarVisibility = actual ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
            }

            var bar = new WrapPanel { Margin = new Thickness(8) };
            bar.Children.Add(actualSize);
            if (hasCamera) bar.Children.Add(ActionButton("Show in Revit", ImageAction.ShowInRevit, "Open this viewpoint's camera in the 3D view"));
            if (canReplace)
            {
                bar.Children.Add(ActionButton("Mark up…", ImageAction.Markup, "Draw on this image and save it as the new image"));
                bar.Children.Add(ActionButton("Replace with current view", ImageAction.ReplaceWithCurrentView, "Take a new screenshot of the active 3D view"));
                bar.Children.Add(ActionButton("Replace from file…", ImageAction.ReplaceFromFile, "Upload a PNG or JPEG instead"));
            }
            var saveAs = new Button { Content = "Save as…", Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 2, 8, 2) };
            saveAs.Click += (_, __) => ImageTools.SaveAs(this, _png, "snapshot.png");
            bar.Children.Add(saveAs);
            var close = new Button { Content = "Close", IsCancel = true, Margin = new Thickness(0, 0, 0, 4), Padding = new Thickness(8, 2, 8, 2) };
            bar.Children.Add(close);

            var root = new DockPanel();
            DockPanel.SetDock(bar, Dock.Bottom);
            root.Children.Add(bar);
            root.Children.Add(scroller);
            Content = root;
        }

        private Button ActionButton(string label, ImageAction action, string tip)
        {
            var button = new Button { Content = label, ToolTip = tip, Margin = new Thickness(0, 0, 6, 4), Padding = new Thickness(8, 2, 8, 2) };
            button.Click += (_, __) =>
            {
                Action = action;
                DialogResult = true;
            };
            return button;
        }

        public static ImageAction Show(DependencyObject? owner, byte[] png, string title, bool hasCamera, bool canReplace)
        {
            var window = new ImageViewerWindow(png, title, hasCamera, canReplace);
            ImageTools.SetOwner(window, owner);
            window.ShowDialog();
            return window.Action;
        }
    }
}
