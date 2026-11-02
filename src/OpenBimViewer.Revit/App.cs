using System;
using System.Reflection;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenBimViewer.Revit.Services;
using OpenBimViewer.Revit.UI;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace OpenBimViewer.Revit
{
    /// <summary>
    /// Registers the dockable "Open BIM Viewer" panel (it must be registered at startup) and a
    /// ribbon button that shows it.
    /// </summary>
    public sealed class App : IExternalApplication
    {
        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("5C2E8A61-0B7D-4F39-A3C4-7E1D9B6F2A85"));
        private const string TabName = "IFC Collab";

        private CollabPanel? _panel;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                var settings = AddinSettings.Load();
                var tasks = new RevitTasks();
                _panel = new CollabPanel(settings, tasks, new ViewpointService(settings));
                application.RegisterDockablePane(PaneId, "Open BIM Viewer", new PaneProvider(_panel));
                CreateRibbon(application);
                Log.Info($"Started in Revit {application.ControlledApplication.VersionNumber}");
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                Log.Error("Startup failed: " + ex);
                TaskDialog.Show("Open BIM Viewer", "The add-in couldn't start: " + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            _panel?.Shutdown();
            return Result.Succeeded;
        }

        private static void CreateRibbon(UIControlledApplication application)
        {
            try
            {
                application.CreateRibbonTab(TabName);
            }
            catch (Autodesk.Revit.Exceptions.ArgumentException)
            {
                // Another IFC Collab add-in already made the tab.
            }
            var panel = application.CreateRibbonPanel(TabName, "Open BIM Viewer");
            var button = new PushButtonData("OpenBimViewer.ShowPanel", "Issues &\nViews", Assembly.GetExecutingAssembly().Location,
                typeof(ShowPanelCommand).FullName)
            {
                ToolTip = "Show the Open BIM Viewer panel: sign in to an IFC Collab server, review issues and saved views, and raise issues from the current 3D view.",
                AvailabilityClassName = typeof(AlwaysAvailable).FullName,
                LargeImage = Icon(32),
                Image = Icon(16),
            };
            panel.AddItem(button);
        }

        /// <summary>A speech bubble with a pin, drawn rather than shipped as an image file.</summary>
        private static BitmapSource Icon(int size)
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                var s = size / 32.0;
                var bubble = new SolidColorBrush(Color.FromRgb(0x1E, 0x6F, 0xD9));
                dc.DrawRoundedRectangle(bubble, null, new Rect(2 * s, 3 * s, 28 * s, 20 * s), 4 * s, 4 * s);
                var tail = new StreamGeometry();
                using (var g = tail.Open())
                {
                    g.BeginFigure(new Point(8 * s, 22 * s), true, true);
                    g.LineTo(new Point(8 * s, 29 * s), true, false);
                    g.LineTo(new Point(15 * s, 22 * s), true, false);
                }
                dc.DrawGeometry(bubble, null, tail);
                dc.DrawEllipse(Brushes.White, null, new Point(16 * s, 13 * s), 5 * s, 5 * s);
                dc.DrawEllipse(bubble, null, new Point(16 * s, 13 * s), 2 * s, 2 * s);
            }
            var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();
            return bitmap;
        }
    }

    internal sealed class PaneProvider : IDockablePaneProvider
    {
        private readonly FrameworkElement _content;

        public PaneProvider(FrameworkElement content) => _content = content;

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = _content;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Tabbed,
                TabBehind = DockablePanes.BuiltInDockablePanes.ProjectBrowser,
            };
        }
    }

    [Transaction(TransactionMode.Manual)]
    public sealed class ShowPanelCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var pane = commandData.Application.GetDockablePane(App.PaneId);
            pane.Show();
            return Result.Succeeded;
        }
    }

    /// <summary>The panel is useful before a model is open (signing in, browsing issues).</summary>
    public sealed class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication applicationData, CategorySet selectedCategories) => true;
    }
}
