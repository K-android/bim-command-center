using System;
using System.IO;
using Autodesk.Revit.UI;

namespace BIMCommandCenter.Core.Revit
{
    public class RhinoInsideStatus
    {
        public bool IsInstalled { get; set; }
        public bool IsLoaded { get; set; }
        public string Message { get; set; }
        public RevitCommandId LaunchCommand { get; set; }
    }

    public static class RhinoInsideDetector
    {
        private static readonly string[] KnownCommandIds = new[]
        {
            "CustomCtrl_%CustomCtrl_%Rhinoceros%Rhinoceros%CommandStartRhino",
            "CustomCtrl_%CustomCtrl_%Rhino.Inside%Rhino.Inside.Revit%CommandStartRhino",
            "CustomCtrl_%Rhino.Inside.Revit%CommandStartRhino",
            "CustomCtrl_%CustomCtrl_%Rhino.Inside%Rhino.Inside.Revit%CommandGrasshopper",
            "CustomCtrl_%Rhino.Inside.Revit%CommandGrasshopper",
            "CustomCtrl_%CustomCtrl_%Rhinoceros%Rhinoceros%CommandGrasshopper"
        };

        public static RhinoInsideStatus Detect(UIApplication app)
        {
            var status = new RhinoInsideStatus();

            // 1. Check if Rhino.Inside command is registered and postable
            foreach (var id in KnownCommandIds)
            {
                try
                {
                    var cmd = RevitCommandId.LookupCommandId(id);
                    if (cmd != null && cmd.CanHaveBinding)
                    {
                        status.IsInstalled = true;
                        status.IsLoaded = true;
                        status.LaunchCommand = cmd;
                        status.Message = "Rhino.Inside.Revit is active and loaded in this session.";
                        return status;
                    }
                }
                catch { }
            }

            // 2. Check loaded assemblies in current AppDomain
            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                foreach (var asm in assemblies)
                {
                    string name = asm.GetName().Name;
                    if (!string.IsNullOrEmpty(name) && name.StartsWith("RhinoInside.Revit", StringComparison.OrdinalIgnoreCase))
                    {
                        status.IsInstalled = true;
                        status.IsLoaded = true;
                        status.Message = "RhinoInside.Revit assembly is loaded in Revit.";
                        return status;
                    }
                }
            }
            catch { }

            // 3. Check for .addin manifest file in user and machine Revit Addins folders
            try
            {
                string ver = app?.Application?.VersionNumber;
                if (string.IsNullOrEmpty(ver)) ver = "2026";

                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

                string[] searchDirs = new[]
                {
                    Path.Combine(appData, "Autodesk", "Revit", "Addins", ver),
                    Path.Combine(progData, "Autodesk", "Revit", "Addins", ver)
                };

                foreach (var dir in searchDirs)
                {
                    if (Directory.Exists(dir))
                    {
                        var addinFiles = Directory.GetFiles(dir, "*RhinoInside*.addin", SearchOption.AllDirectories);
                        if (addinFiles.Length > 0)
                        {
                            status.IsInstalled = true;
                            status.IsLoaded = false;
                            status.Message = "Rhino.Inside.Revit add-in manifest is present, but not active. Start Rhino from the Rhinoceros tab to initialize.";
                            return status;
                        }
                    }
                }
            }
            catch { }

            // 4. Check typical Rhino / Rhino.Inside install directories
            try
            {
                string progFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                string[] checkPaths = new[]
                {
                    Path.Combine(progFiles, "Rhino 8", "System", "Rhino.Inside.Revit"),
                    Path.Combine(progFiles, "Rhino 7", "System", "Rhino.Inside.Revit"),
                    Path.Combine(progFiles, "Rhino.Inside", "Revit")
                };

                foreach (var p in checkPaths)
                {
                    if (Directory.Exists(p) || File.Exists(p + ".dll"))
                    {
                        status.IsInstalled = true;
                        status.IsLoaded = false;
                        status.Message = "Rhino.Inside.Revit files found on disk, but not loaded in this session.";
                        return status;
                    }
                }
            }
            catch { }

            status.IsInstalled = false;
            status.IsLoaded = false;
            status.Message = "Rhino.Inside.Revit is not installed on this workstation. Install from https://www.rhino3d.com/inside/revit/8/ to run Grasshopper automations.";
            return status;
        }
    }
}
