using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OpenBimViewer.Collab
{
    public readonly struct Vec3
    {
        public Vec3(double x, double y, double z)
        {
            X = x;
            Y = y;
            Z = z;
        }

        public double X { get; }
        public double Y { get; }
        public double Z { get; }

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        internal static Vec3 From(Dictionary<string, object?>? obj) =>
            obj == null ? default : new Vec3(obj.Num("x"), obj.Num("y"), obj.Num("z"));

        internal Dictionary<string, object?> ToJson() => new Dictionary<string, object?> { ["x"] = X, ["y"] = Y, ["z"] = Z };

        public override string ToString() => string.Format(CultureInfo.InvariantCulture, "({0:0.###}, {1:0.###}, {2:0.###})", X, Y, Z);
    }

    /// <summary>A BCF perspective camera, in IFC coordinates (metres, Z up) as BCF requires.</summary>
    public sealed class Camera
    {
        public Vec3 Location { get; set; }
        public Vec3 Direction { get; set; }
        public Vec3 Up { get; set; } = new Vec3(0, 0, 1);
        /// <summary>Vertical field of view in degrees.</summary>
        public double FieldOfView { get; set; } = 60;

        internal static Camera? From(Dictionary<string, object?>? obj)
        {
            if (obj == null) return null;
            var cam = new Camera
            {
                Location = Vec3.From(obj.Obj("location")),
                Direction = Vec3.From(obj.Obj("direction")),
                Up = Vec3.From(obj.Obj("up")),
                FieldOfView = obj.Num("fieldOfView", 60),
            };
            return cam.Direction.Length > 1e-9 ? cam : null;
        }

        internal Dictionary<string, object?> ToJson() => new Dictionary<string, object?>
        {
            ["location"] = Location.ToJson(),
            ["direction"] = Direction.ToJson(),
            ["up"] = Up.ToJson(),
            ["fieldOfView"] = FieldOfView,
        };
    }

    public sealed class User
    {
        public string Id { get; internal set; } = "";
        public string Email { get; internal set; } = "";
        public string Name { get; internal set; } = "";

        public override string ToString() => string.IsNullOrEmpty(Name) ? Email : $"{Name} ({Email})";
    }

    /// <summary>The Core's session state.</summary>
    public sealed class ClientState
    {
        public bool LoggedIn { get; internal set; }
        public bool HasSavedSession { get; internal set; }
        public bool Online { get; internal set; }
        public string ServerUrl { get; internal set; } = "";
        public string LastEmail { get; internal set; } = "";
        public IReadOnlyList<string> RecentServers { get; internal set; } = Array.Empty<string>();
        public User User { get; internal set; } = new User();

        internal static ClientState From(Dictionary<string, object?> o)
        {
            var user = o.Obj("user") ?? new Dictionary<string, object?>();
            return new ClientState
            {
                LoggedIn = o.Bool("loggedIn"),
                HasSavedSession = o.Bool("hasSavedSession"),
                Online = o.Bool("online"),
                ServerUrl = o.Str("serverUrl"),
                LastEmail = o.Str("lastEmail"),
                RecentServers = o.Strings("recentServers"),
                User = new User { Id = user.Str("id"), Email = user.Str("email"), Name = user.Str("name") },
            };
        }
    }

    /// <summary>The BCF enumerations the server accepts, and the web app's status colours.</summary>
    public sealed class Vocabulary
    {
        public IReadOnlyList<string> TopicTypes { get; internal set; } = Array.Empty<string>();
        public IReadOnlyList<string> TopicStatuses { get; internal set; } = Array.Empty<string>();
        public IReadOnlyList<string> Priorities { get; internal set; } = Array.Empty<string>();
        /// <summary>Status → "#rrggbb".</summary>
        public IReadOnlyDictionary<string, string> StatusColors { get; internal set; } = new Dictionary<string, string>();

        internal static Vocabulary From(Dictionary<string, object?> o)
        {
            var colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in o.Obj("statusColors") ?? new Dictionary<string, object?>())
            {
                if (pair.Value is string hex) colors[pair.Key] = hex;
            }
            return new Vocabulary
            {
                TopicTypes = o.Strings("topicTypes"),
                TopicStatuses = o.Strings("topicStatuses"),
                Priorities = o.Strings("priorities"),
                StatusColors = colors,
            };
        }
    }

    public sealed class Project
    {
        public string Id { get; internal set; } = "";
        public string Name { get; internal set; } = "";
        public string Description { get; internal set; } = "";
        public string Role { get; internal set; } = "";
        public int ModelCount { get; internal set; }

        internal static Project From(Dictionary<string, object?> o) => new Project
        {
            Id = o.Str("id"),
            Name = o.Str("name"),
            Description = o.Str("description"),
            Role = o.Str("role"),
            ModelCount = (int)o.Num("modelCount"),
        };

        public override string ToString() => Name;
    }

    public sealed class SavedView
    {
        public string Id { get; internal set; } = "";
        public string Guid { get; internal set; } = "";
        public string Name { get; internal set; } = "";
        public string CreationAuthor { get; internal set; } = "";
        public string CreatedAt { get; internal set; } = "";
        /// <summary>Absolute URL of the preview image, or empty.</summary>
        public string SnapshotUrl { get; internal set; } = "";
        /// <summary>The project opens at this view.</summary>
        public bool IsStarting { get; internal set; }
        public Camera? Camera { get; internal set; }

        internal static SavedView From(Dictionary<string, object?> o) => new SavedView
        {
            Id = o.Str("id"),
            Guid = o.Str("guid"),
            Name = o.Str("name"),
            CreationAuthor = o.Str("creationAuthor"),
            CreatedAt = o.Str("createdAt"),
            SnapshotUrl = o.Str("snapshotUrl"),
            IsStarting = o.Bool("isStarting"),
            Camera = Camera.From(o.Obj("camera")),
        };
    }

    public sealed class Comment
    {
        public string Author { get; internal set; } = "";
        public string Date { get; internal set; } = "";
        public string Text { get; internal set; } = "";
        /// <summary>The comment's own viewpoint (empty when it has none); its snapshot can be replaced.</summary>
        public string ViewpointGuid { get; internal set; } = "";
        public string SnapshotUrl { get; internal set; } = "";
        public Camera? Camera { get; internal set; }

        internal static Comment From(Dictionary<string, object?> o) => new Comment
        {
            Author = o.Str("author"),
            Date = o.Str("date"),
            Text = o.Str("text"),
            ViewpointGuid = o.Str("viewpointGuid"),
            SnapshotUrl = o.Str("snapshotUrl"),
            Camera = Camera.From(o.Obj("camera")),
        };
    }

    /// <summary>A BCF topic, as the Core reduces it.</summary>
    public sealed class Issue
    {
        public string Id { get; internal set; } = "";
        public string Guid { get; internal set; } = "";
        public int Index { get; internal set; }
        public string Title { get; internal set; } = "";
        public string TopicType { get; internal set; } = "";
        public string TopicStatus { get; internal set; } = "";
        public string Priority { get; internal set; } = "";
        public string AssignedTo { get; internal set; } = "";
        public string Description { get; internal set; } = "";
        public string Stage { get; internal set; } = "";
        public IReadOnlyList<string> Labels { get; internal set; } = Array.Empty<string>();
        public string DueDate { get; internal set; } = "";
        public string CreationAuthor { get; internal set; } = "";
        public string CreationDate { get; internal set; } = "";
        public string ModifiedAuthor { get; internal set; } = "";
        public string ModifiedDate { get; internal set; } = "";
        /// <summary>The main viewpoint, whose snapshot is the issue's image.</summary>
        public string SnapshotViewpointGuid { get; internal set; } = "";
        public string SnapshotUrl { get; internal set; } = "";
        public Camera? Camera { get; internal set; }
        public IReadOnlyList<Comment> Comments { get; internal set; } = Array.Empty<Comment>();

        /// <summary>Not yet on the server (created offline): it has a guid but no id.</summary>
        public bool IsLocalOnly => Id.Length == 0;

        internal static Issue From(Dictionary<string, object?> o) => new Issue
        {
            Id = o.Str("id"),
            Guid = o.Str("guid"),
            Index = (int)o.Num("index"),
            Title = o.Str("title"),
            TopicType = o.Str("topicType"),
            TopicStatus = o.Str("topicStatus"),
            Priority = o.Str("priority"),
            AssignedTo = o.Str("assignedTo"),
            Description = o.Str("description"),
            Stage = o.Str("stage"),
            Labels = o.Strings("labels"),
            DueDate = o.Str("dueDate"),
            CreationAuthor = o.Str("creationAuthor"),
            CreationDate = o.Str("creationDate"),
            ModifiedAuthor = o.Str("modifiedAuthor"),
            ModifiedDate = o.Str("modifiedDate"),
            SnapshotViewpointGuid = o.Str("snapshotViewpointGuid"),
            SnapshotUrl = o.Str("snapshotUrl"),
            Camera = Camera.From(o.Obj("camera")),
            Comments = o.Arr("comments").OfType<Dictionary<string, object?>>().Select(Comment.From).ToList(),
        };
    }

    /// <summary>What a new issue or comment carries from the current view.</summary>
    public sealed class ViewpointCapture
    {
        public Camera? Camera { get; set; }
        /// <summary>PNG bytes, or null for none.</summary>
        public byte[]? Png { get; set; }
        /// <summary>IFC GlobalIds of the selected elements.</summary>
        public IList<string> Selection { get; set; } = new List<string>();

        internal Dictionary<string, object?> ToJson() => new Dictionary<string, object?>
        {
            ["camera"] = Camera?.ToJson(),
            ["png"] = Png == null ? "" : Convert.ToBase64String(Png),
            ["selection"] = Selection.ToList(),
        };
    }

    public sealed class NewIssue
    {
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        /// <summary>Empty for the server's default ("Issue").</summary>
        public string TopicType { get; set; } = "";
        public string Priority { get; set; } = "";
        /// <summary>IFC GlobalIds the issue is about.</summary>
        public IList<string> Components { get; set; } = new List<string>();
        public ViewpointCapture? Viewpoint { get; set; }
    }

    /// <summary>A change to an issue: only the properties that are set (non-null) are sent.</summary>
    public sealed class IssueChanges
    {
        public string? Title { get; set; }
        public string? Description { get; set; }
        public string? TopicType { get; set; }
        public string? TopicStatus { get; set; }
        public string? Priority { get; set; }
        public string? Stage { get; set; }
        /// <summary>An e-mail; "" clears the assignment.</summary>
        public string? AssignedTo { get; set; }
        /// <summary>ISO 8601; "" clears the due date.</summary>
        public string? DueDate { get; set; }
        public IList<string>? Labels { get; set; }

        public bool IsEmpty => Title == null && Description == null && TopicType == null && TopicStatus == null && Priority == null
                               && Stage == null && AssignedTo == null && DueDate == null && Labels == null;

        /// <summary>Only what differs from the issue as it is.</summary>
        public static IssueChanges Diff(Issue current, string title, string description, string topicType, string topicStatus,
            string priority, string assignedTo)
        {
            string? Changed(string now, string wanted) => string.Equals(now, wanted, StringComparison.Ordinal) ? null : wanted;
            return new IssueChanges
            {
                Title = Changed(current.Title, title.Trim()),
                Description = Changed(current.Description, description),
                TopicType = Changed(current.TopicType, topicType),
                TopicStatus = Changed(current.TopicStatus, topicStatus),
                Priority = Changed(current.Priority, priority),
                AssignedTo = Changed(current.AssignedTo, assignedTo.Trim()),
            };
        }

        internal void AddTo(Dictionary<string, object?> args)
        {
            void Put(string key, string? value)
            {
                if (value != null) args[key] = value;
            }
            Put("title", Title);
            Put("description", Description);
            Put("topicType", TopicType);
            Put("topicStatus", TopicStatus);
            Put("priority", Priority);
            Put("stage", Stage);
            Put("assignedTo", AssignedTo);
            Put("dueDate", DueDate);
            if (Labels != null) args["labels"] = Labels.ToList();
        }
    }

    public sealed class SyncResult
    {
        public int Applied { get; internal set; }
        public int Rejected { get; internal set; }
        public int Gone { get; internal set; }
        public int Remaining { get; internal set; }
    }

    /// <summary>A change made offline that is still waiting to be sent.</summary>
    public sealed class PendingChange
    {
        public string OpId { get; internal set; } = "";
        public string Entity { get; internal set; } = "";
        public string Action { get; internal set; } = "";
        public string Label { get; internal set; } = "";
        /// <summary>Why the server refused it last time, if it did.</summary>
        public string Error { get; internal set; } = "";
    }

    /// <summary>A failed Core operation; the message is meant for the user.</summary>
    public sealed class CollabException : Exception
    {
        public CollabException(string message) : base(message) { }
    }
}
