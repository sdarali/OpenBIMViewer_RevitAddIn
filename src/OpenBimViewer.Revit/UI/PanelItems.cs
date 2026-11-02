using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using OpenBimViewer.Collab;

namespace OpenBimViewer.Revit.UI
{
    internal abstract class Observable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        protected void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// An image shown in the panel: an issue's, a comment's or a saved view's. Double-click shows
    /// its camera in Revit (when it has one); issue and comment images can also be replaced.
    /// </summary>
    internal sealed class SnapshotItem : Observable
    {
        private string _url;
        private ImageSource? _image;
        private string? _status;

        public SnapshotItem(string caption, string url, Camera? camera, string issueId = "", string viewpointGuid = "")
        {
            Caption = caption;
            _url = url;
            Camera = camera;
            IssueId = issueId;
            ViewpointGuid = viewpointGuid;
            _status = url.Length == 0 ? "No image" : "Loading…";
        }

        public string Caption { get; }
        public Camera? Camera { get; }
        /// <summary>The issue whose viewpoint this is (empty for saved views).</summary>
        public string IssueId { get; }
        /// <summary>The viewpoint whose snapshot this is, which is what gets replaced.</summary>
        public string ViewpointGuid { get; }
        /// <summary>The PNG/JPEG as downloaded or last saved.</summary>
        public byte[]? Bytes { get; set; }

        public string Url
        {
            get => _url;
            set => Set(ref _url, value);
        }

        public ImageSource? Image
        {
            get => _image;
            set => Set(ref _image, value);
        }

        /// <summary>"Loading…", an error, or null once the image shows.</summary>
        public string? Status
        {
            get => _status;
            set
            {
                Set(ref _status, value);
                Changed(nameof(HasStatus));
            }
        }

        public bool HasStatus => !string.IsNullOrEmpty(_status);
        public bool HasCamera => Camera != null;
        public bool CanReplace => IssueId.Length > 0 && ViewpointGuid.Length > 0;
        public bool HasImage => _url.Length > 0;

        public string Hint => HasCamera
            ? Caption + ": double-click to show this view in Revit. Right-click for more."
            : Caption + ": double-click to open. Right-click for more.";
    }

    internal sealed class CommentItem
    {
        public CommentItem(Comment comment, SnapshotItem? snapshot)
        {
            Author = comment.Author;
            Date = ImageTools.FormatDate(comment.Date);
            Text = comment.Text;
            Snapshot = snapshot;
        }

        public string Author { get; }
        public string Date { get; }
        public string Text { get; }
        public SnapshotItem? Snapshot { get; }
        public bool HasSnapshot => Snapshot != null;
    }

    internal sealed class ViewItem
    {
        public ViewItem(SavedView view)
        {
            View = view;
            Snapshot = new SnapshotItem(view.Name, view.SnapshotUrl, view.Camera);
            Meta = string.Join(" · ", new[] { view.CreationAuthor, ImageTools.FormatDate(view.CreatedAt) }.Where(s => s.Length > 0));
        }

        public SavedView View { get; }
        public string Name => View.Name;
        public string Badge => View.IsStarting ? " · starting view" : "";
        public string Meta { get; }
        public SnapshotItem Snapshot { get; }
    }

    internal sealed class IssueItem
    {
        public IssueItem(Issue issue, Vocabulary vocabulary)
        {
            Issue = issue;
            var hex = vocabulary.StatusColors.TryGetValue(issue.TopicStatus, out var c) ? c : "#808080";
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            StatusBrush = brush;
            Meta = string.Join(" · ", new[]
            {
                issue.TopicType,
                issue.Priority,
                issue.AssignedTo.Length > 0 ? "→ " + issue.AssignedTo : "",
                issue.IsLocalOnly ? "not synced yet" : "",
            }.Where(s => s.Length > 0));
        }

        public Issue Issue { get; }
        public string Number => Issue.Index > 0 ? "#" + Issue.Index : "new";
        public string Title => Issue.Title;
        public string Status => Issue.TopicStatus;
        public Brush StatusBrush { get; }
        public string Meta { get; }
    }
}
