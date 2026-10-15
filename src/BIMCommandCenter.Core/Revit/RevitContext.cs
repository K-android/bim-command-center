using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace BIMCommandCenter.Core.Revit
{
    public static class RevitContext
    {
        public static Dictionary<string, object> Describe(UIApplication app)
        {
            Document doc = app.ActiveUIDocument?.Document;
            View view = app.ActiveUIDocument?.ActiveView;
            var rhinoStatus = RhinoInsideDetector.Detect(app);

            return new Dictionary<string, object>
            {
                ["revitVersion"] = app.Application.VersionNumber,
                ["revitBuild"] = app.Application.VersionBuild,
                ["userName"] = app.Application.Username,
                ["hasDocument"] = doc != null,
                ["documentTitle"] = doc?.Title ?? "",
                ["isWorkshared"] = doc?.IsWorkshared ?? false,
                ["activeView"] = view?.Name ?? "",
                ["activeViewType"] = view?.ViewType.ToString() ?? "",
                ["selectionCount"] = app.ActiveUIDocument?.Selection.GetElementIds().Count ?? 0,
                ["hasRhinoInside"] = rhinoStatus.IsInstalled,
                ["rhinoInsideLoaded"] = rhinoStatus.IsLoaded,
                ["rhinoInsideStatus"] = rhinoStatus.Message,
                ["installedDynamoPackages"] = DynamoPackageInspector.GetInstalledPackages()
            };
        }

        /// <summary>Exports the visible region of the active view to PNG and returns it as base64 (for the AI chat).</summary>
        public static Dictionary<string, object> CaptureActiveView(UIApplication app)
        {
            Document doc = app.ActiveUIDocument?.Document ?? throw new InvalidOperationException("No document is open.");
            View view = app.ActiveUIDocument.ActiveView;
            if (view == null || !view.CanBePrinted)
                throw new InvalidOperationException("The active view can't be captured (try a plan, section, 3D view or sheet).");

            // Revit appends the view name to the file name, so export into a fresh folder and pick up whatever lands there.
            string dir = Path.Combine(Path.GetTempPath(), "bimcc_capture_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var options = new ImageExportOptions
                {
                    ExportRange = ExportRange.VisibleRegionOfCurrentView,
                    FilePath = Path.Combine(dir, "capture"),
                    HLRandWFViewsFileType = ImageFileType.PNG,
                    ShadowViewsFileType = ImageFileType.PNG,
                    ImageResolution = ImageResolution.DPI_150,
                    ZoomType = ZoomFitType.FitToPage,
                    FitDirection = FitDirectionType.Horizontal,
                    PixelSize = 1600
                };
                doc.ExportImage(options);

                string file = Directory.GetFiles(dir, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                              ?? throw new InvalidOperationException("Revit did not produce an image.");
                return new Dictionary<string, object>
                {
                    ["mimeType"] = "image/png",
                    ["base64Data"] = Convert.ToBase64String(File.ReadAllBytes(file)),
                    ["viewName"] = view.Name
                };
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }
    }
}
