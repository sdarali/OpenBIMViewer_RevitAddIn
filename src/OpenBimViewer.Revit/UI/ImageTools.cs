using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace OpenBimViewer.Revit.UI
{
    internal static class ImageTools
    {
        public static BitmapSource Decode(byte[] bytes)
        {
            using (var stream = new MemoryStream(bytes))
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();
                return image;
            }
        }

        public static byte[] EncodePng(BitmapSource bitmap)
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = new MemoryStream())
            {
                encoder.Save(stream);
                return stream.ToArray();
            }
        }

        /// <summary>Renders an element at exactly width × height pixels.</summary>
        public static byte[] RenderPng(Visual visual, int width, int height)
        {
            var drawing = new DrawingVisual();
            using (var dc = drawing.RenderOpen())
            {
                dc.DrawRectangle(new VisualBrush(visual) { Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height));
            }
            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(drawing);
            return EncodePng(bitmap);
        }

        /// <summary>Asks for a PNG or JPEG and returns it as PNG, or null when cancelled.</summary>
        public static byte[]? PickImageFile(Window? owner)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Choose the new image",
                Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*",
            };
            if (dialog.ShowDialog(owner) != true) return null;
            var bytes = File.ReadAllBytes(dialog.FileName);
            return EncodePng(Decode(bytes));
        }

        public static void SaveAs(Window? owner, byte[] png, string suggestedName)
        {
            var dialog = new SaveFileDialog { Title = "Save image", Filter = "PNG image (*.png)|*.png", FileName = suggestedName };
            if (dialog.ShowDialog(owner) == true) File.WriteAllBytes(dialog.FileName, png);
        }

        /// <summary>Gives a window opened from the dockable panel the panel's window as owner, if it has one.</summary>
        public static void SetOwner(Window window, DependencyObject? from)
        {
            var owner = from == null ? null : Window.GetWindow(from);
            if (owner != null && !ReferenceEquals(owner, window))
            {
                window.Owner = owner;
                window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }
        }

        public static string FormatDate(string iso)
        {
            return DateTime.TryParse(iso, null, System.Globalization.DateTimeStyles.RoundtripKind, out var date)
                ? date.ToLocalTime().ToString("g")
                : iso;
        }
    }
}
