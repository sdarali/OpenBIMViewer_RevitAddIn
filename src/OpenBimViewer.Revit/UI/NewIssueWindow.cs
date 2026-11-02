using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using OpenBimViewer.Collab;

namespace OpenBimViewer.Revit.UI
{
    /// <summary>
    /// A new issue from the current 3D view: title, description, type, priority, the screenshot
    /// (which can be marked up or retaken) and, optionally, the selected elements.
    /// </summary>
    internal sealed class NewIssueWindow : Window
    {
        private readonly Func<Task<ViewpointCapture>> _recapture;
        private readonly TextBox _title = new TextBox { Padding = new Thickness(3, 2, 3, 2) };
        private readonly TextBox _description = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 70,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        private readonly ComboBox _type = new ComboBox();
        private readonly ComboBox _priority = new ComboBox();
        private readonly Image _preview = new Image { MaxHeight = 260, Stretch = Stretch.Uniform };
        private readonly CheckBox _includeSelection = new CheckBox { Margin = new Thickness(0, 8, 0, 0) };
        private readonly Button _create = new Button { Content = "Create issue", IsDefault = true, MinWidth = 100, Padding = new Thickness(10, 3, 10, 3) };
        private ViewpointCapture _capture;

        public NewIssue? Result { get; private set; }

        private NewIssueWindow(ViewpointCapture capture, Vocabulary vocabulary, Func<Task<ViewpointCapture>> recapture)
        {
            _capture = capture;
            _recapture = recapture;
            Title = "New issue from the current view";
            Width = 560;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.CanResizeWithGrip;
            ShowInTaskbar = false;
            FontSize = 12;

            foreach (var t in vocabulary.TopicTypes) _type.Items.Add(t);
            _type.SelectedItem = vocabulary.TopicTypes.Contains("Issue") ? "Issue" : vocabulary.TopicTypes.FirstOrDefault();
            _priority.Items.Add("(none)");
            foreach (var p in vocabulary.Priorities) _priority.Items.Add(p);
            _priority.SelectedIndex = 0;

            var markup = new Button { Content = "Mark up…", Margin = new Thickness(0, 0, 6, 0), Padding = new Thickness(8, 2, 8, 2) };
            markup.Click += (_, __) => Markup();
            var retake = new Button { Content = "Retake from current view", Padding = new Thickness(8, 2, 8, 2) };
            retake.Click += async (_, __) => await Retake(retake);
            var imageButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
            imageButtons.Children.Add(markup);
            imageButtons.Children.Add(retake);

            var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 3, 10, 3) };
            _create.Click += (_, __) => Create();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(_create);
            buttons.Children.Add(cancel);

            var types = new Grid();
            types.ColumnDefinitions.Add(new ColumnDefinition());
            types.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            types.ColumnDefinitions.Add(new ColumnDefinition());
            var typePanel = Labelled("Type", _type);
            var priorityPanel = Labelled("Priority", _priority);
            Grid.SetColumn(priorityPanel, 2);
            types.Children.Add(typePanel);
            types.Children.Add(priorityPanel);

            var form = new StackPanel { Margin = new Thickness(12) };
            form.Children.Add(Labelled("Title", _title));
            form.Children.Add(Labelled("Description", _description));
            form.Children.Add(types);
            form.Children.Add(Label("Screenshot"));
            form.Children.Add(new Border { BorderBrush = Brushes.LightGray, BorderThickness = new Thickness(1), Child = _preview, HorizontalAlignment = HorizontalAlignment.Left });
            form.Children.Add(imageButtons);
            form.Children.Add(_includeSelection);
            form.Children.Add(buttons);
            Content = form;

            _title.TextChanged += (_, __) => _create.IsEnabled = _title.Text.Trim().Length > 0;
            _create.IsEnabled = false;
            ShowCapture();
            Loaded += (_, __) => _title.Focus();
        }

        private static TextBlock Label(string text) => new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 3) };

        private static StackPanel Labelled(string label, UIElement control)
        {
            var panel = new StackPanel();
            panel.Children.Add(Label(label));
            panel.Children.Add(control);
            return panel;
        }

        private void ShowCapture()
        {
            _preview.Source = _capture.Png == null ? null : ImageTools.Decode(_capture.Png);
            var count = _capture.Selection.Count;
            _includeSelection.Content = count == 0
                ? "No elements selected in Revit"
                : $"Link the {count} selected element{(count == 1 ? "" : "s")} (by IFC GlobalId)";
            _includeSelection.IsEnabled = count > 0;
            _includeSelection.IsChecked = count > 0;
        }

        private void Markup()
        {
            if (_capture.Png == null) return;
            var edited = MarkupEditorWindow.Edit(this, _capture.Png, "new issue");
            if (edited == null) return;
            _capture.Png = edited;
            ShowCapture();
        }

        private async Task Retake(Button button)
        {
            button.IsEnabled = false;
            try
            {
                _capture = await _recapture();
                ShowCapture();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                button.IsEnabled = true;
            }
        }

        private void Create()
        {
            var title = _title.Text.Trim();
            if (title.Length == 0) return;
            var selection = _includeSelection.IsChecked == true ? _capture.Selection.ToList() : new System.Collections.Generic.List<string>();
            Result = new NewIssue
            {
                Title = title,
                Description = _description.Text.Trim(),
                TopicType = _type.SelectedItem as string ?? "",
                Priority = _priority.SelectedIndex > 0 ? (string)_priority.SelectedItem : "",
                Components = selection,
                Viewpoint = new ViewpointCapture { Camera = _capture.Camera, Png = _capture.Png, Selection = selection },
            };
            DialogResult = true;
        }

        public static NewIssue? Ask(DependencyObject? owner, ViewpointCapture capture, Vocabulary vocabulary, Func<Task<ViewpointCapture>> recapture)
        {
            var window = new NewIssueWindow(capture, vocabulary, recapture);
            ImageTools.SetOwner(window, owner);
            return window.ShowDialog() == true ? window.Result : null;
        }
    }
}
