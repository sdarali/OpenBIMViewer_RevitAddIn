using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using OpenBimViewer.Collab;
using OpenBimViewer.Revit.Services;

namespace OpenBimViewer.Revit.UI
{
    /// <summary>
    /// The dockable panel. Its handlers run on Revit's UI thread, which is also the IFC Collab Core's
    /// logic thread; anything that needs the Revit API goes through <see cref="RevitTasks"/>.
    /// </summary>
    public partial class CollabPanel : Page
    {
        private const string AllStatuses = "All";

        private readonly AddinSettings _settings;
        private readonly RevitTasks _revit;
        private readonly ViewpointService _viewpoints;
        private readonly CollabClient? _client;
        private readonly Dictionary<string, byte[]> _images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        private List<Issue> _issues = new List<Issue>();
        private Issue? _shown;
        private bool _busy;
        private bool _loadingProjects;

        internal CollabPanel(AddinSettings settings, RevitTasks revit, ViewpointService viewpoints)
        {
            InitializeComponent();
            _settings = settings;
            _revit = revit;
            _viewpoints = viewpoints;

            try
            {
                _client = new CollabClient(new DispatcherThread(Dispatcher), new CollabClientOptions
                {
                    CacheDirectory = AddinStorage.CacheFolder,
                    ClientName = "open-bim-revit",
                    SessionJson = SessionStore.Load(),
                    SaveSession = SessionStore.Save,
                    Log = Log.Core,
                    CertificateTrust = new CertificateTrust(settings.TrustedCertificates),
                });
                _client.OnlineChanged += _ => UpdateSession();
            }
            catch (Exception ex)
            {
                Log.Error("The IFC Collab Core couldn't start: " + ex);
                SetStatus("The IFC Collab Core couldn't start: " + ex.Message, error: true);
                SignInButton.IsEnabled = false;
                return;
            }

            var vocabulary = _client.Vocabulary;
            StatusFilter.Items.Add(AllStatuses);
            foreach (var s in vocabulary.TopicStatuses) StatusFilter.Items.Add(s);
            StatusFilter.SelectedIndex = 0;
            foreach (var s in vocabulary.TopicStatuses) StatusBox.Items.Add(s);
            foreach (var t in vocabulary.TopicTypes) TypeBox.Items.Add(t);
            PriorityBox.Items.Add("");
            foreach (var p in vocabulary.Priorities) PriorityBox.Items.Add(p);

            var state = _client.State;
            ServerBox.ItemsSource = state.RecentServers;
            ServerBox.Text = state.ServerUrl;
            EmailBox.Text = state.LastEmail;
            UpdateSession();
            if (state.HasSavedSession)
            {
                SetStatus("Signing in…");
                _ = Run(async () =>
                {
                    await _client.ResumeAsync();
                    UpdateSession();
                    await LoadProjects();
                }, "Sign in again: ");
            }
        }

        /// <summary>Revit is closing.</summary>
        internal void Shutdown() => _client?.Dispose();

        private CollabClient Client => _client ?? throw new InvalidOperationException("The IFC Collab Core isn't running.");

        private Project? CurrentProject => ProjectBox.SelectedItem as Project;

        // ---------------------------------------------------------------- status and busy

        private void SetStatus(string text, bool error = false)
        {
            StatusText.Text = text;
            StatusText.Foreground = error ? Brushes.Firebrick : Brushes.Gray;
        }

        /// <summary>
        /// Runs a UI action: one at a time, with the controls disabled meanwhile and failures shown
        /// in the status line rather than thrown at Revit.
        /// </summary>
        private async Task Run(Func<Task> action, string errorPrefix = "")
        {
            if (_busy) return;
            _busy = true;
            UpdateEnabled();
            try
            {
                await action();
            }
            catch (CollabException ex)
            {
                SetStatus(errorPrefix + Explain(ex), error: true);
            }
            catch (InvalidOperationException ex)
            {
                SetStatus(errorPrefix + ex.Message, error: true);
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException ex)
            {
                Log.Warning("Revit: " + ex);
                SetStatus(errorPrefix + "Revit: " + ex.Message, error: true);
            }
            catch (Exception ex)
            {
                Log.Error(ex.ToString());
                SetStatus(errorPrefix + ex.Message, error: true);
            }
            finally
            {
                _busy = false;
                UpdateEnabled();
            }
        }

        /// <summary>The Core's message, plus why the connection failed when that's known.</summary>
        private string Explain(CollabException ex)
        {
            var transport = Client.LastTransportError;
            return transport != null && !Client.State.Online ? $"{ex.Message} ({transport})" : ex.Message;
        }

        private void UpdateEnabled()
        {
            var signedIn = _client?.State.LoggedIn == true;
            var ready = signedIn && !_busy;
            SignInButton.IsEnabled = !_busy && _client != null;
            SignOutButton.IsEnabled = !_busy;
            ProjectBox.IsEnabled = ready;
            RefreshButton.IsEnabled = ready && CurrentProject != null;
            NewIssueButton.IsEnabled = ready && CurrentProject != null;
            SaveIssueButton.IsEnabled = ready && _shown != null && !_shown.IsLocalOnly;
            PostCommentButton.IsEnabled = ready && _shown != null && !_shown.IsLocalOnly;
            SyncButton.IsEnabled = ready;
        }

        private void UpdateSession()
        {
            if (_client == null) return;
            var state = _client.State;
            SignInPanel.Visibility = state.LoggedIn ? Visibility.Collapsed : Visibility.Visible;
            SessionPanel.Visibility = state.LoggedIn ? Visibility.Visible : Visibility.Collapsed;
            ProjectRow.Visibility = state.LoggedIn ? Visibility.Visible : Visibility.Collapsed;
            Tabs.Visibility = state.LoggedIn ? Visibility.Visible : Visibility.Collapsed;
            SignedInText.Text = $"{state.User} on {Host(state.ServerUrl)}";
            OnlineDot.Fill = state.Online ? new SolidColorBrush(Color.FromRgb(0x43, 0xA0, 0x47)) : Brushes.Orange;
            OnlineDot.ToolTip = state.Online ? "Connected" : "Server unreachable: working from the cache; changes are queued";
            UpdatePending();
            UpdateEnabled();
        }

        private void UpdatePending()
        {
            var project = CurrentProject;
            var pending = project == null || _client == null ? new List<PendingChange>() : _client.PendingChanges(project.Id).ToList();
            PendingPanel.Visibility = pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            var refused = pending.Where(p => p.Error.Length > 0).ToList();
            PendingText.Text = $"{pending.Count} change{(pending.Count == 1 ? "" : "s")} waiting to be sent"
                               + (refused.Count > 0 ? $"; {refused.Count} refused: {refused[0].Error}" : ".");
        }

        private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Authority : url;

        // ---------------------------------------------------------------- sign in / out

        private void Password_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) SignIn_Click(sender, e);
        }

        private async void SignIn_Click(object sender, RoutedEventArgs e)
        {
            var server = ServerBox.Text.Trim();
            var email = EmailBox.Text.Trim();
            var password = PasswordBox.Password;
            if (server.Length == 0 || email.Length == 0 || password.Length == 0)
            {
                SetStatus("Enter the server address, your e-mail and your password.", error: true);
                return;
            }
            await Run(async () =>
            {
                SetStatus("Signing in…");
                await LoginTrustingCertificate(server, email, password);
                PasswordBox.Clear();
                UpdateSession();
                await LoadProjects();
            });
        }

        /// <summary>Signs in; if Windows refused the server's certificate, offers to trust it and tries once more.</summary>
        private async Task LoginTrustingCertificate(string server, string email, string password)
        {
            Client.CertificateTrust.ClearLastRejected();
            try
            {
                await Client.LoginAsync(server, email, password);
            }
            catch (CollabException) when (Client.CertificateTrust.LastRejected != null)
            {
                if (!CertificatePrompt.AskAndTrust(Window.GetWindow(this), _settings, Client.CertificateTrust)) throw;
                await Client.LoginAsync(server, email, password);
            }
        }

        private void SignOut_Click(object sender, RoutedEventArgs e)
        {
            if (_busy || _client == null) return;
            _client.Logout();
            ProjectBox.ItemsSource = null;
            ClearIssues();
            ViewList.ItemsSource = null;
            var state = _client.State;
            ServerBox.ItemsSource = state.RecentServers;
            ServerBox.Text = state.ServerUrl;
            UpdateSession();
            SetStatus("Signed out.");
        }

        // ---------------------------------------------------------------- projects

        private async Task LoadProjects()
        {
            SetStatus("Loading projects…");
            var projects = await Client.GetProjectsAsync();
            _loadingProjects = true;
            try
            {
                ProjectBox.ItemsSource = projects;
                ProjectBox.SelectedItem = projects.FirstOrDefault(p => p.Id == _settings.LastProjectId) ?? projects.FirstOrDefault();
            }
            finally
            {
                _loadingProjects = false;
            }
            if (CurrentProject == null)
            {
                SetStatus("You aren't a member of any project on this server.");
                return;
            }
            await LoadProject();
        }

        private async void Project_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingProjects || CurrentProject == null) return;
            _settings.LastProjectId = CurrentProject.Id;
            _settings.Save();
            await Run(LoadProject);
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await Run(LoadProject);

        private async Task LoadProject()
        {
            var project = CurrentProject;
            if (project == null) return;
            if (_settings.LastProjectId != project.Id)
            {
                _settings.LastProjectId = project.Id;
                _settings.Save();
            }
            SetStatus($"Loading {project.Name}…");
            await LoadIssues(keep: _shown?.Guid);
            var views = await Client.GetViewsAsync(project.Id);
            var items = views.Select(v => new ViewItem(v)).ToList();
            ViewList.ItemsSource = items;
            NoViews.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var item in items) _ = LoadImage(item.Snapshot);
            UpdateSession();
            var offline = Client.State.Online ? "" : " (offline: from the cache)";
            SetStatus($"{project.Name}: {_issues.Count} issue{(_issues.Count == 1 ? "" : "s")}, {items.Count} saved view{(items.Count == 1 ? "" : "s")}{offline}.");
        }

        // ---------------------------------------------------------------- issues

        private async Task LoadIssues(string? keep)
        {
            var project = CurrentProject;
            if (project == null) return;
            _issues = (await Client.GetIssuesAsync(project.Id)).ToList();
            ApplyFilter(keep);
        }

        private void Filter_Changed(object sender, EventArgs e)
        {
            if (_client != null) ApplyFilter(_shown?.Guid);
        }

        private void ApplyFilter(string? keepGuid)
        {
            var status = StatusFilter.SelectedItem as string ?? AllStatuses;
            var search = SearchBox.Text.Trim();
            var vocabulary = Client.Vocabulary;
            var items = _issues
                .Where(i => status == AllStatuses || string.Equals(i.TopicStatus, status, StringComparison.OrdinalIgnoreCase))
                .Where(i => search.Length == 0 || Matches(i, search))
                .OrderByDescending(i => i.Index == 0) // not yet synced first
                .ThenByDescending(i => i.Index)
                .Select(i => new IssueItem(i, vocabulary))
                .ToList();
            IssueList.ItemsSource = items;
            var keep = keepGuid == null ? null : items.FirstOrDefault(i => i.Issue.Guid == keepGuid);
            if (keep != null) IssueList.SelectedItem = keep;
            else ShowIssue(null);
        }

        private static bool Matches(Issue issue, string text) =>
            ("#" + issue.Index).Equals(text, StringComparison.OrdinalIgnoreCase)
            || issue.Title.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
            || issue.AssignedTo.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0
            || issue.Description.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0;

        private void ClearIssues()
        {
            _issues.Clear();
            IssueList.ItemsSource = null;
            ShowIssue(null);
        }

        private void Issue_SelectionChanged(object sender, SelectionChangedEventArgs e) => ShowIssue((IssueList.SelectedItem as IssueItem)?.Issue);

        private void ShowIssue(Issue? issue)
        {
            _shown = issue;
            DetailsPanel.Visibility = issue == null ? Visibility.Collapsed : Visibility.Visible;
            UpdateEnabled();
            if (issue == null) return;

            var created = string.Join(" · ", new[] { issue.CreationAuthor, ImageTools.FormatDate(issue.CreationDate) }.Where(s => s.Length > 0));
            var modified = issue.ModifiedDate.Length > 0 ? $" · changed {ImageTools.FormatDate(issue.ModifiedDate)} by {issue.ModifiedAuthor}" : "";
            IssueHeader.Text = (issue.Index > 0 ? $"#{issue.Index} · " : "Not synced yet · ") + created + modified;
            TitleBox.Text = issue.Title;
            SelectOrAdd(StatusBox, issue.TopicStatus);
            SelectOrAdd(TypeBox, issue.TopicType);
            SelectOrAdd(PriorityBox, issue.Priority);
            AssignedBox.Text = issue.AssignedTo;
            DescriptionBox.Text = issue.Description;

            var image = new SnapshotItem($"Issue #{issue.Index}", issue.SnapshotUrl, issue.Camera, issue.Id, issue.SnapshotViewpointGuid);
            IssueImage.Content = image;
            IssueImage.Visibility = image.HasImage ? Visibility.Visible : Visibility.Collapsed;
            NoIssueImage.Visibility = image.HasImage ? Visibility.Collapsed : Visibility.Visible;
            _ = LoadImage(image);

            var comments = issue.Comments.Select(c => new CommentItem(c,
                c.SnapshotUrl.Length > 0 || c.ViewpointGuid.Length > 0
                    ? new SnapshotItem($"Comment by {c.Author}", c.SnapshotUrl, c.Camera, issue.Id, c.ViewpointGuid)
                    : null)).ToList();
            CommentList.ItemsSource = comments;
            NoComments.Visibility = comments.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            foreach (var c in comments.Where(c => c.Snapshot != null)) _ = LoadImage(c.Snapshot!);
        }

        /// <summary>Selects a value, adding it first if the server used one outside the BCF list.</summary>
        private static void SelectOrAdd(ComboBox box, string value)
        {
            if (!box.Items.Contains(value)) box.Items.Add(value);
            box.SelectedItem = value;
        }

        private async void SaveIssue_Click(object sender, RoutedEventArgs e)
        {
            var issue = _shown;
            var project = CurrentProject;
            if (issue == null || project == null) return;
            var changes = IssueChanges.Diff(issue, TitleBox.Text, DescriptionBox.Text,
                TypeBox.SelectedItem as string ?? issue.TopicType, StatusBox.SelectedItem as string ?? issue.TopicStatus,
                PriorityBox.SelectedItem as string ?? issue.Priority, AssignedBox.Text);
            // BCF has no "no priority" once one is set; the server keeps the old one.
            if (changes.Priority == "") changes.Priority = null;
            if (changes.Title == "")
            {
                SetStatus("An issue needs a title.", error: true);
                return;
            }
            if (changes.IsEmpty)
            {
                SetStatus("Nothing changed.");
                return;
            }
            await Run(async () =>
            {
                SetStatus("Saving…");
                await Client.UpdateIssueAsync(project.Id, issue.Id, changes);
                await LoadIssues(keep: issue.Guid);
                UpdatePending();
                SetStatus(Client.State.Online ? "Saved." : "Saved offline; it will be sent when the server is reachable.");
            });
        }

        private async void NewIssue_Click(object sender, RoutedEventArgs e)
        {
            var project = CurrentProject;
            if (project == null) return;
            await Run(async () =>
            {
                SetStatus("Taking a screenshot of the active 3D view…");
                var capture = await _revit.Run(app => _viewpoints.Capture(app));
                SetStatus("");
                var issue = NewIssueWindow.Ask(this, capture, Client.Vocabulary, () => _revit.Run(app => _viewpoints.Capture(app)));
                if (issue == null)
                {
                    SetStatus("No issue created.");
                    return;
                }
                SetStatus("Creating the issue…");
                var created = await Client.CreateIssueAsync(project.Id, issue);
                SearchBox.Text = "";
                StatusFilter.SelectedIndex = 0;
                await LoadIssues(keep: created.Guid);
                UpdatePending();
                SetStatus(created.IsLocalOnly
                    ? "Issue saved offline; it will be sent when the server is reachable."
                    : $"Issue #{created.Index} created.");
            });
        }

        private async void PostComment_Click(object sender, RoutedEventArgs e)
        {
            var issue = _shown;
            var project = CurrentProject;
            var text = CommentBox.Text.Trim();
            if (issue == null || project == null) return;
            if (text.Length == 0)
            {
                SetStatus("Write the comment first.", error: true);
                return;
            }
            await Run(async () =>
            {
                ViewpointCapture? viewpoint = null;
                if (AttachViewCheck.IsChecked == true)
                {
                    SetStatus("Taking a screenshot of the active 3D view…");
                    viewpoint = await _revit.Run(app => _viewpoints.Capture(app));
                    if (MarkupCheck.IsChecked == true && viewpoint.Png != null)
                    {
                        var edited = MarkupEditorWindow.Edit(this, viewpoint.Png, "comment");
                        if (edited == null)
                        {
                            SetStatus("Comment not posted.");
                            return;
                        }
                        viewpoint.Png = edited;
                    }
                }
                SetStatus("Posting…");
                await Client.AddCommentAsync(project.Id, issue.Id, text, viewpoint);
                CommentBox.Clear();
                AttachViewCheck.IsChecked = false;
                await LoadIssues(keep: issue.Guid);
                UpdatePending();
                SetStatus(Client.State.Online ? "Comment posted." : "Comment saved offline; it will be sent when the server is reachable.");
            });
        }

        private async void Sync_Click(object sender, RoutedEventArgs e)
        {
            var project = CurrentProject;
            if (project == null) return;
            await Run(async () =>
            {
                SetStatus("Sending queued changes…");
                var result = await Client.SyncAsync(project.Id);
                await LoadProject();
                SetStatus(result.Rejected > 0
                    ? $"Sent {result.Applied}; the server refused {result.Rejected} (see the note above)."
                    : $"Sent {result.Applied} change{(result.Applied == 1 ? "" : "s")}.", error: result.Rejected > 0);
            });
        }

        // ---------------------------------------------------------------- images

        private async Task LoadImage(SnapshotItem item)
        {
            if (!item.HasImage) return;
            try
            {
                if (!_images.TryGetValue(item.Url, out var bytes))
                {
                    bytes = await Client.DownloadAsync(item.Url);
                    _images[item.Url] = bytes;
                }
                item.Bytes = bytes;
                item.Image = ImageTools.Decode(bytes);
                item.Status = null;
            }
            catch (Exception ex) when (ex is CollabException || ex is NotSupportedException || ex is ObjectDisposedException)
            {
                item.Status = "Image unavailable: " + ex.Message;
            }
        }

        private static SnapshotItem? ItemOf(object sender) => (sender as FrameworkElement)?.DataContext as SnapshotItem;

        private async void Snapshot_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount != 2 || ItemOf(sender) is not { } item) return;
            e.Handled = true;
            if (item.HasCamera) await Run(() => ShowInRevit(item.Camera!, item.Caption));
            else await OpenViewer(item);
        }

        private async void SnapshotShow_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { Camera: { } camera } item) await Run(() => ShowInRevit(camera, item.Caption));
        }

        private async void SnapshotView_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) await OpenViewer(item);
        }

        private async void SnapshotMarkup_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) await Run(() => Markup(item));
        }

        private async void SnapshotReplaceView_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) await Run(() => ReplaceWithCurrentView(item));
        }

        private async void SnapshotReplaceFile_Click(object sender, RoutedEventArgs e)
        {
            if (ItemOf(sender) is { } item) await Run(() => ReplaceFromFile(item));
        }

        private async void ViewList_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!(ViewList.SelectedItem is ViewItem view)) return;
            if (view.View.Camera == null)
            {
                SetStatus($"\"{view.Name}\" has no camera to show.", error: true);
                return;
            }
            await Run(() => ShowInRevit(view.View.Camera, view.Name));
        }

        private async Task ShowInRevit(Camera camera, string what)
        {
            SetStatus($"Showing {what} in Revit…");
            var viewName = await _revit.Run(app => _viewpoints.Show(app, camera));
            SetStatus($"Showing {what} in the 3D view \"{viewName}\".");
        }

        private async Task OpenViewer(SnapshotItem item)
        {
            if (item.Bytes == null)
            {
                SetStatus(item.Status ?? "The image hasn't loaded yet.", error: item.Status != null);
                return;
            }
            var action = ImageViewerWindow.Show(this, item.Bytes, item.Caption, item.HasCamera, item.CanReplace && CanEditShown);
            switch (action)
            {
                case ImageAction.ShowInRevit:
                    await Run(() => ShowInRevit(item.Camera!, item.Caption));
                    break;
                case ImageAction.Markup:
                    await Run(() => Markup(item));
                    break;
                case ImageAction.ReplaceWithCurrentView:
                    await Run(() => ReplaceWithCurrentView(item));
                    break;
                case ImageAction.ReplaceFromFile:
                    await Run(() => ReplaceFromFile(item));
                    break;
            }
        }

        /// <summary>Replacing images needs the server: offline, the Core can't queue it.</summary>
        private bool CanEditShown => _client?.State.Online == true;

        private async Task Markup(SnapshotItem item)
        {
            if (item.Bytes == null) throw new InvalidOperationException("The image hasn't loaded yet.");
            var edited = MarkupEditorWindow.Edit(this, item.Bytes, item.Caption);
            if (edited != null) await Replace(item, edited);
        }

        private async Task ReplaceWithCurrentView(SnapshotItem item)
        {
            SetStatus("Taking a screenshot of the active 3D view…");
            var png = await _revit.Run(app => _viewpoints.CaptureScreenshot(app));
            await Replace(item, png);
        }

        private async Task ReplaceFromFile(SnapshotItem item)
        {
            var png = ImageTools.PickImageFile(Window.GetWindow(this));
            if (png != null) await Replace(item, png);
        }

        /// <summary>Uploads the new image for the item's viewpoint (the camera stays as it was).</summary>
        private async Task Replace(SnapshotItem item, byte[] png)
        {
            var project = CurrentProject ?? throw new InvalidOperationException("Choose a project first.");
            if (!item.CanReplace) throw new InvalidOperationException("This image can't be replaced.");
            SetStatus("Uploading the new image…");
            var url = await Client.ReplaceSnapshotAsync(project.Id, item.IssueId, item.ViewpointGuid, png);
            _images[url] = png;
            item.Url = url;
            item.Bytes = png;
            item.Image = ImageTools.Decode(png);
            item.Status = null;
            await LoadIssues(keep: _shown?.Guid);
            SetStatus("Image replaced.");
        }
    }
}
