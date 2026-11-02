using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace OpenBimViewer.Revit.UI
{
    /// <summary>
    /// Draw on a screenshot: pen, highlighter, line, arrow, rectangle, ellipse and text, in a choice
    /// of colours and widths, with undo/redo. Shapes are drawn in the image's own pixels, so the
    /// saved PNG has the screenshot's resolution whatever the window size.
    /// </summary>
    internal sealed class MarkupEditorWindow : Window
    {
        private enum Tool { Pen, Highlighter, Line, Arrow, Rectangle, Ellipse, Text }

        private static readonly Color[] Palette =
        {
            Color.FromRgb(0xE5, 0x39, 0x35), Color.FromRgb(0xFB, 0x8C, 0x00), Color.FromRgb(0xFD, 0xD8, 0x35),
            Color.FromRgb(0x43, 0xA0, 0x47), Color.FromRgb(0x1E, 0x88, 0xE5), Colors.Black, Colors.White,
        };

        private readonly int _width;
        private readonly int _height;
        private readonly Grid _surface;
        private readonly Canvas _canvas;
        private readonly Stack<UIElement> _done = new Stack<UIElement>();
        private readonly Stack<UIElement> _undone = new Stack<UIElement>();
        private readonly Button _undoButton;
        private readonly Button _redoButton;

        private Tool _tool = Tool.Arrow;
        private Color _color = Palette[0];
        private double _thickness;
        private Point _start;
        private Shape? _drawing;
        private TextBox? _editing;

        /// <summary>The marked-up image, set when the user saves.</summary>
        public byte[]? Result { get; private set; }

        public MarkupEditorWindow(byte[] png, string title)
        {
            var bitmap = ImageTools.Decode(png);
            _width = bitmap.PixelWidth;
            _height = bitmap.PixelHeight;
            _thickness = Math.Max(2, Math.Round(_width / 300.0));

            Title = "Mark up: " + title;
            Width = Math.Min(1280, SystemParameters.WorkArea.Width * 0.85);
            Height = Math.Min(860, SystemParameters.WorkArea.Height * 0.85);
            ShowInTaskbar = false;
            FontSize = 12;

            // The image at 1 px = 1 unit (whatever DPI the PNG claims), the drawing canvas over it.
            _surface = new Grid { Width = _width, Height = _height, ClipToBounds = true };
            _surface.Children.Add(new Image { Source = bitmap, Width = _width, Height = _height, Stretch = Stretch.Fill });
            _canvas = new Canvas { Width = _width, Height = _height, Background = Brushes.Transparent, Cursor = Cursors.Cross };
            _canvas.MouseLeftButtonDown += Canvas_MouseDown;
            _canvas.MouseMove += Canvas_MouseMove;
            _canvas.MouseLeftButtonUp += Canvas_MouseUp;
            _surface.Children.Add(_canvas);

            var tools = new ToolBar();
            foreach (var tool in (Tool[])Enum.GetValues(typeof(Tool)))
            {
                var button = new RadioButton
                {
                    Content = ToolLabel(tool),
                    ToolTip = tool.ToString(),
                    GroupName = "tool",
                    IsChecked = tool == _tool,
                    Padding = new Thickness(6, 2, 6, 2),
                    Style = TryFindResource(ToolBar.RadioButtonStyleKey) as Style,
                };
                button.Checked += (_, __) =>
                {
                    CommitText();
                    _tool = tool;
                    _canvas.Cursor = tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
                };
                tools.Items.Add(button);
            }
            tools.Items.Add(new Separator());
            foreach (var color in Palette)
            {
                var swatch = new RadioButton
                {
                    GroupName = "color",
                    IsChecked = color == _color,
                    ToolTip = "Colour",
                    Style = TryFindResource(ToolBar.RadioButtonStyleKey) as Style,
                    Content = new Border
                    {
                        Width = 16,
                        Height = 16,
                        Background = new SolidColorBrush(color),
                        BorderBrush = Brushes.Gray,
                        BorderThickness = new Thickness(1),
                    },
                };
                swatch.Checked += (_, __) => _color = color;
                tools.Items.Add(swatch);
            }
            tools.Items.Add(new Separator());
            var widths = new ComboBox { Width = 80, ToolTip = "Line width" };
            var thin = Math.Max(1, Math.Round(_width / 600.0));
            foreach (var w in new[] { ("Thin", thin), ("Medium", _thickness), ("Thick", _thickness * 2), ("Heavy", _thickness * 4) })
            {
                widths.Items.Add(new ComboBoxItem { Content = w.Item1, Tag = w.Item2 });
            }
            widths.SelectedIndex = 1;
            widths.SelectionChanged += (_, __) => _thickness = (double)((ComboBoxItem)widths.SelectedItem).Tag;
            tools.Items.Add(widths);
            tools.Items.Add(new Separator());
            _undoButton = ToolButton("Undo", "Undo (Ctrl+Z)", Undo);
            _redoButton = ToolButton("Redo", "Redo (Ctrl+Y)", Redo);
            tools.Items.Add(_undoButton);
            tools.Items.Add(_redoButton);
            tools.Items.Add(ToolButton("Clear", "Remove all markup", Clear));

            var save = new Button { Content = "Save", IsDefault = false, MinWidth = 80, Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(10, 3, 10, 3) };
            save.Click += (_, __) => Save();
            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Padding = new Thickness(10, 3, 10, 3) };
            var hint = new TextBlock
            {
                Text = "Draw on the image, then Save. Text: click where it goes, type, press Enter.",
                Foreground = Brushes.Gray,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var bottom = new DockPanel { Margin = new Thickness(8), LastChildFill = true };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(save);
            buttons.Children.Add(cancel);
            DockPanel.SetDock(buttons, Dock.Right);
            bottom.Children.Add(buttons);
            bottom.Children.Add(hint);

            var stage = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
                Padding = new Thickness(8),
                Child = new Viewbox { Child = _surface, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.Both },
            };

            var root = new DockPanel();
            var tray = new ToolBarTray();
            tray.ToolBars.Add(tools);
            DockPanel.SetDock(tray, Dock.Top);
            DockPanel.SetDock(bottom, Dock.Bottom);
            root.Children.Add(tray);
            root.Children.Add(bottom);
            root.Children.Add(stage);
            Content = root;

            PreviewKeyDown += OnKey;
            UpdateButtons();
        }

        private static string ToolLabel(Tool tool)
        {
            switch (tool)
            {
                case Tool.Pen: return "✎ Pen";
                case Tool.Highlighter: return "▍ Highlight";
                case Tool.Line: return "╱ Line";
                case Tool.Arrow: return "➚ Arrow";
                case Tool.Rectangle: return "▭ Box";
                case Tool.Ellipse: return "◯ Ellipse";
                default: return "T Text";
            }
        }

        private static Button ToolButton(string label, string tip, Action action)
        {
            var button = new Button { Content = label, ToolTip = tip, Padding = new Thickness(6, 2, 6, 2) };
            button.Click += (_, __) => action();
            return button;
        }

        // ---------------------------------------------------------------- drawing

        private Brush StrokeBrush(byte alpha = 0xFF)
        {
            var brush = new SolidColorBrush(Color.FromArgb(alpha, _color.R, _color.G, _color.B));
            brush.Freeze();
            return brush;
        }

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            var p = e.GetPosition(_canvas);
            if (_tool == Tool.Text)
            {
                CommitText();
                StartText(p);
                e.Handled = true;
                return;
            }
            CommitText();
            _start = p;
            _drawing = NewShape(p);
            _canvas.Children.Add(_drawing);
            _canvas.CaptureMouse();
            e.Handled = true;
        }

        private Shape NewShape(Point p)
        {
            switch (_tool)
            {
                case Tool.Pen:
                case Tool.Highlighter:
                    var highlight = _tool == Tool.Highlighter;
                    return new Polyline
                    {
                        Points = new PointCollection { p },
                        Stroke = StrokeBrush(highlight ? (byte)0x66 : (byte)0xFF),
                        StrokeThickness = highlight ? _thickness * 5 : _thickness,
                        StrokeLineJoin = PenLineJoin.Round,
                        StrokeStartLineCap = highlight ? PenLineCap.Flat : PenLineCap.Round,
                        StrokeEndLineCap = highlight ? PenLineCap.Flat : PenLineCap.Round,
                    };
                case Tool.Line:
                    return new Line { X1 = p.X, Y1 = p.Y, X2 = p.X, Y2 = p.Y, Stroke = StrokeBrush(), StrokeThickness = _thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
                case Tool.Arrow:
                    return new Path { Stroke = StrokeBrush(), Fill = StrokeBrush(), StrokeThickness = _thickness, StrokeLineJoin = PenLineJoin.Round, Data = Arrow(p, p) };
                case Tool.Rectangle:
                    return new Rectangle { Stroke = StrokeBrush(), StrokeThickness = _thickness };
                default:
                    return new Ellipse { Stroke = StrokeBrush(), StrokeThickness = _thickness };
            }
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_drawing == null || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(_canvas);
            p = new Point(Math.Max(0, Math.Min(_width, p.X)), Math.Max(0, Math.Min(_height, p.Y)));
            switch (_drawing)
            {
                case Polyline line:
                    line.Points.Add(p);
                    break;
                case Line straight:
                    straight.X2 = p.X;
                    straight.Y2 = p.Y;
                    break;
                case Path arrow:
                    arrow.Data = Arrow(_start, p);
                    break;
                default:
                    Canvas.SetLeft(_drawing, Math.Min(_start.X, p.X));
                    Canvas.SetTop(_drawing, Math.Min(_start.Y, p.Y));
                    _drawing.Width = Math.Abs(p.X - _start.X);
                    _drawing.Height = Math.Abs(p.Y - _start.Y);
                    break;
            }
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_drawing == null) return;
            _canvas.ReleaseMouseCapture();
            var p = e.GetPosition(_canvas);
            // A click without a drag leaves nothing worth keeping (except a dot from the pen).
            var tiny = (p - _start).Length < 2 && !(_drawing is Polyline);
            if (tiny) _canvas.Children.Remove(_drawing);
            else Added(_drawing);
            _drawing = null;
        }

        /// <summary>A line with a filled head at <paramref name="to"/>, sized from the line width.</summary>
        private Geometry Arrow(Point from, Point to)
        {
            var group = new GeometryGroup();
            var v = to - from;
            var length = v.Length;
            if (length < 1)
            {
                group.Children.Add(new LineGeometry(from, to));
                return group;
            }
            v /= length;
            var headLength = Math.Min(length, _thickness * 5 + 8);
            var headWidth = headLength * 0.6;
            var normal = new Vector(-v.Y, v.X);
            var baseCenter = to - v * headLength;
            group.Children.Add(new LineGeometry(from, baseCenter));
            var head = new StreamGeometry();
            using (var g = head.Open())
            {
                g.BeginFigure(to, true, true);
                g.LineTo(baseCenter + normal * headWidth, true, true);
                g.LineTo(baseCenter - normal * headWidth, true, true);
            }
            group.Children.Add(head);
            return group;
        }

        private void StartText(Point p)
        {
            _editing = new TextBox
            {
                MinWidth = 60,
                FontSize = Math.Max(12, _thickness * 7),
                FontWeight = FontWeights.SemiBold,
                Foreground = StrokeBrush(),
                Background = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                BorderBrush = StrokeBrush(),
                AcceptsReturn = false,
            };
            Canvas.SetLeft(_editing, p.X);
            Canvas.SetTop(_editing, p.Y);
            _editing.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter || e.Key == Key.Escape)
                {
                    CommitText();
                    e.Handled = true;
                }
            };
            _editing.LostKeyboardFocus += (_, __) => CommitText();
            _canvas.Children.Add(_editing);
            Dispatcher.BeginInvoke(new Action(() => _editing?.Focus()));
        }

        private void CommitText()
        {
            var box = _editing;
            if (box == null) return;
            _editing = null;
            _canvas.Children.Remove(box);
            if (string.IsNullOrWhiteSpace(box.Text)) return;
            var text = new TextBlock
            {
                Text = box.Text,
                FontSize = box.FontSize,
                FontWeight = box.FontWeight,
                Foreground = box.Foreground,
                Background = box.Background,
                Padding = new Thickness(4, 1, 4, 1),
            };
            Canvas.SetLeft(text, Canvas.GetLeft(box));
            Canvas.SetTop(text, Canvas.GetTop(box));
            _canvas.Children.Add(text);
            Added(text);
        }

        // ---------------------------------------------------------------- history

        private void Added(UIElement element)
        {
            _done.Push(element);
            _undone.Clear();
            UpdateButtons();
        }

        private void Undo()
        {
            CommitText();
            if (_done.Count == 0) return;
            var element = _done.Pop();
            _canvas.Children.Remove(element);
            _undone.Push(element);
            UpdateButtons();
        }

        private void Redo()
        {
            if (_undone.Count == 0) return;
            var element = _undone.Pop();
            _canvas.Children.Add(element);
            _done.Push(element);
            UpdateButtons();
        }

        private void Clear()
        {
            CommitText();
            if (_done.Count == 0) return;
            if (MessageBox.Show(this, "Remove all markup?", Title, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            _canvas.Children.Clear();
            _done.Clear();
            _undone.Clear();
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            _undoButton.IsEnabled = _done.Count > 0;
            _redoButton.IsEnabled = _undone.Count > 0;
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            if (_editing != null) return;
            if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
            {
                Undo();
                e.Handled = true;
            }
            else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
            {
                Redo();
                e.Handled = true;
            }
        }

        private void Save()
        {
            CommitText();
            _surface.UpdateLayout();
            Result = ImageTools.RenderPng(_surface, _width, _height);
            DialogResult = true;
        }

        /// <summary>Shows the editor; returns the marked-up PNG, or null when cancelled.</summary>
        public static byte[]? Edit(DependencyObject? owner, byte[] png, string title)
        {
            var window = new MarkupEditorWindow(png, title);
            ImageTools.SetOwner(window, owner);
            return window.ShowDialog() == true ? window.Result : null;
        }
    }
}
