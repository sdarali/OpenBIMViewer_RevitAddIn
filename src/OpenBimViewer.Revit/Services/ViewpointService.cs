using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using OpenBimViewer.Collab;

namespace OpenBimViewer.Revit.Services
{
    /// <summary>
    /// Revit's side of viewpoints: capturing the active 3D view (camera, screenshot, selection)
    /// and showing a BCF camera in the add-in's own 3D view. Call only in Revit's API context
    /// (through <see cref="RevitTasks"/>).
    /// </summary>
    internal sealed class ViewpointService
    {
        private readonly AddinSettings _settings;

        public ViewpointService(AddinSettings settings) => _settings = settings;

        // ---------------------------------------------------------------- capture

        /// <summary>The active 3D view's camera, a PNG of what it shows, and the selected elements' IFC GlobalIds.</summary>
        public ViewpointCapture Capture(UIApplication app, bool withScreenshot = true)
        {
            var uidoc = app.ActiveUIDocument ?? throw new InvalidOperationException("Open a model in Revit first.");
            var doc = uidoc.Document;
            if (!(uidoc.ActiveView is View3D view) || view.IsTemplate)
            {
                throw new InvalidOperationException("Switch to a 3D view first: screenshots and cameras are taken from the active 3D view.");
            }
            return new ViewpointCapture
            {
                Camera = CameraOf(uidoc, view),
                Png = withScreenshot ? Screenshot(doc) : null,
                Selection = SelectedGlobalIds(uidoc),
            };
        }

        /// <summary>Only the screenshot (for replacing an image with the current view).</summary>
        public byte[] CaptureScreenshot(UIApplication app) =>
            Capture(app).Png ?? throw new InvalidOperationException("Revit didn't produce a screenshot.");

        private Camera CameraOf(UIDocument uidoc, View3D view)
        {
            var frame = IfcFrame.For(uidoc.Document, _settings.CoordinateBase);
            var orientation = view.GetOrientation();
            var forward = orientation.ForwardDirection.Normalize();
            var eye = orientation.EyePosition;

            if (!view.IsPerspective)
            {
                // An isometric view's "eye" can sit anywhere along the view direction, often inside the
                // model. Stand back far enough that a perspective camera sees what the view shows.
                var uiView = uidoc.GetOpenUIViews().FirstOrDefault(v => v.ViewId == view.Id);
                var corners = uiView?.GetZoomCorners();
                if (corners != null && corners.Count == 2)
                {
                    var center = (corners[0] + corners[1]) / 2;
                    var up = orientation.UpDirection.Normalize();
                    var height = Math.Abs((corners[1] - corners[0]).DotProduct(up));
                    var distance = height / 2 / Math.Tan(_settings.FieldOfView * Math.PI / 360);
                    eye = center - forward * Math.Max(distance, 1.0);
                }
            }

            return new Camera
            {
                Location = frame.ToIfcPoint(eye),
                Direction = frame.ToIfcDirection(forward),
                Up = frame.ToIfcDirection(orientation.UpDirection),
                FieldOfView = _settings.FieldOfView,
            };
        }

        /// <summary>PNG of exactly what the active view shows.</summary>
        private byte[] Screenshot(Document doc)
        {
            var dir = Path.Combine(Path.GetTempPath(), "OpenBimViewer", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.VisibleRegionOfCurrentView,
                    FilePath = Path.Combine(dir, "snapshot"),
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ZoomType = ZoomFitType.FitToPage,
                    FitDirection = FitDirectionType.Horizontal,
                    PixelSize = _settings.SnapshotWidth,
                };
                doc.ExportImage(options);
                // Revit decides the final file name (it appends the view's name), so take whatever it wrote.
                var file = Directory.GetFiles(dir, "*.png").FirstOrDefault()
                           ?? throw new InvalidOperationException("Revit didn't produce a screenshot.");
                return File.ReadAllBytes(file);
            }
            finally
            {
                try
                {
                    Directory.Delete(dir, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }

        /// <summary>
        /// IFC GlobalIds of the selected elements, as Revit's IFC exporter writes them: the stored
        /// IfcGUID parameter if there is one, otherwise the GUID derived from the element's export id.
        /// </summary>
        private static List<string> SelectedGlobalIds(UIDocument uidoc)
        {
            var doc = uidoc.Document;
            var ids = new List<string>();
            foreach (var id in uidoc.Selection.GetElementIds())
            {
                var element = doc.GetElement(id);
                if (element == null || element is ElementType) continue;
                var stored = element.get_Parameter(BuiltInParameter.IFC_GUID)?.AsString();
                if (IfcGuid.IsValid(stored))
                {
                    ids.Add(stored!);
                    continue;
                }
                try
                {
                    ids.Add(IfcGuid.Encode(ExportUtils.GetExportId(doc, id)));
                }
                catch (Autodesk.Revit.Exceptions.ApplicationException)
                {
                    // Elements without an export id (e.g. some annotation) aren't in the IFC anyway.
                }
            }
            return ids.Distinct().ToList();
        }

        // ---------------------------------------------------------------- show

        /// <summary>
        /// Shows a BCF camera in the add-in's 3D view (<see cref="AddinSettings.ViewName"/>), creating
        /// it the first time and reusing it after that, and makes it the active view.
        /// </summary>
        public string Show(UIApplication app, Camera camera)
        {
            var uidoc = app.ActiveUIDocument ?? throw new InvalidOperationException("Open a model in Revit first.");
            var doc = uidoc.Document;
            if (doc.IsFamilyDocument) throw new InvalidOperationException("Open a project, not a family, to show views.");

            var frame = IfcFrame.For(doc, _settings.CoordinateBase);
            var eye = frame.ToRevitPoint(camera.Location);
            var forward = frame.ToRevitDirection(camera.Direction);
            if (forward.IsZeroLength()) throw new InvalidOperationException("This view has no usable camera.");
            var up = Perpendicular(frame.ToRevitDirection(camera.Up), forward);

            View3D view;
            using (var t = new Transaction(doc, "Open BIM Viewer: show view"))
            {
                t.Start();
                view = FindView(doc) ?? CreateView(doc);
                if (!view.IsPerspective && view.CanToggleBetweenPerspectiveAndIsometric()) view.ToggleToPerspective();
                view.SetOrientation(new ViewOrientation3D(eye, up, forward));
                // Without a far clip the whole model stays visible however far the camera is.
                var farClip = view.get_Parameter(BuiltInParameter.VIEWER_BOUND_ACTIVE_FAR);
                if (farClip != null && !farClip.IsReadOnly) farClip.Set(0);
                t.Commit();
            }

            if (uidoc.ActiveView?.Id != view.Id) uidoc.ActiveView = view;
            else uidoc.RefreshActiveView();
            return view.Name;
        }

        private View3D? FindView(Document doc) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate && string.Equals(v.Name, _settings.ViewName, StringComparison.OrdinalIgnoreCase));

        private View3D CreateView(Document doc)
        {
            var type = new FilteredElementCollector(doc)
                           .OfClass(typeof(ViewFamilyType))
                           .Cast<ViewFamilyType>()
                           .FirstOrDefault(t => t.ViewFamily == ViewFamily.ThreeDimensional)
                       ?? throw new InvalidOperationException("This model has no 3D view type to create a view from.");
            var view = View3D.CreatePerspective(doc, type.Id);
            view.Name = _settings.ViewName;
            return view;
        }

        /// <summary>Up made exactly perpendicular to forward, as Revit requires.</summary>
        private static XYZ Perpendicular(XYZ up, XYZ forward)
        {
            var candidate = up - forward * up.DotProduct(forward);
            if (candidate.GetLength() < 1e-6)
            {
                // Looking straight up or down: any perpendicular will do.
                var helper = Math.Abs(forward.Z) < 0.9 ? XYZ.BasisZ : XYZ.BasisY;
                candidate = helper - forward * helper.DotProduct(forward);
            }
            return candidate.Normalize();
        }
    }
}
