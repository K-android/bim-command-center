using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Autodesk.Revit.UI;

namespace BIMCommandCenter.Loader
{
    /// <summary>
    /// Entry point referenced by BIMCommandCenter.addin.
    /// Keeps itself tiny and stable. All real functionality lives in BIMCommandCenter.Core.dll,
    /// which can be updated centrally (updateSource in config.json) without reinstalling.
    /// </summary>
    public class LoaderApp : IExternalApplication
    {
        private IExternalApplication _core;
        private static string _coreDir;

        public Result OnStartup(UIControlledApplication application)
        {
            try
            {
                string installDir = Path.GetDirectoryName(typeof(LoaderApp).Assembly.Location);
                string bundledCoreDir = Path.Combine(installDir, "core");
                string year = application.ControlledApplication.VersionNumber;

                _coreDir = CoreLocator.Resolve(bundledCoreDir, year);
                AppDomain.CurrentDomain.SetData("BIMCC_InstallDir", installDir);
                AppDomain.CurrentDomain.SetData("BIMCC_CoreDir", _coreDir);
                AppDomain.CurrentDomain.AssemblyResolve += ResolveFromCoreDir;

                Assembly coreAsm = Assembly.LoadFrom(Path.Combine(_coreDir, "BIMCommandCenter.Core.dll"));
                Type coreType = coreAsm.GetType("BIMCommandCenter.Core.CoreApp", throwOnError: true);
                _core = (IExternalApplication)Activator.CreateInstance(coreType);
                return _core.OnStartup(application);
            }
            catch (Exception ex)
            {
                Log("Startup failed: " + ex);
                TaskDialog.Show("BIM Command Center", "BIM Command Center failed to load.\n\n" + ex.Message);
                return Result.Failed;
            }
        }

        public Result OnShutdown(UIControlledApplication application)
        {
            try { return _core?.OnShutdown(application) ?? Result.Succeeded; }
            catch { return Result.Succeeded; }
        }

        private static Assembly ResolveFromCoreDir(object sender, ResolveEventArgs args)
        {
            if (string.IsNullOrEmpty(_coreDir)) return null;
            string name = new AssemblyName(args.Name).Name;
            string candidate = Path.Combine(_coreDir, name + ".dll");
            return File.Exists(candidate) ? Assembly.LoadFrom(candidate) : null;
        }

        internal static void Log(string message)
        {
            try
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMCommandCenter", "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "loader.log"), DateTime.Now.ToString("s") + "  " + message + Environment.NewLine);
            }
            catch { /* logging must never break Revit */ }
        }
    }

    /// <summary>
    /// Decides which Core folder to load: the bundled one, or a newer release from the central update share.
    /// Releases are copied to %LocalAppData% so Revit never locks files on the server.
    /// </summary>
    internal static class CoreLocator
    {
        public static string Resolve(string bundledCoreDir, string year)
        {
            Version bundledVersion = ReadVersion(bundledCoreDir);
            string updateSource = ReadUpdateSource(bundledCoreDir);
            if (string.IsNullOrWhiteSpace(updateSource)) return bundledCoreDir;

            string localRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMCommandCenter", "core", year);

            try
            {
                // Network paths can hang for ~30s when unreachable: never block Revit startup on them.
                var check = Task.Run(() => TryPullUpdate(Path.Combine(updateSource, year), localRoot, bundledVersion));
                if (check.Wait(TimeSpan.FromSeconds(4)) && check.Result != null)
                {
                    LoaderApp.Log("Pulled update into " + check.Result);
                }
            }
            catch (Exception ex)
            {
                LoaderApp.Log("Update check failed: " + ex.Message);
            }

            // Use the newest locally cached release if it beats the bundled one.
            string best = bundledCoreDir;
            Version bestVersion = bundledVersion;
            if (Directory.Exists(localRoot))
            {
                foreach (string dir in Directory.GetDirectories(localRoot))
                {
                    Version v = ReadVersion(dir);
                    if (v > bestVersion && File.Exists(Path.Combine(dir, "BIMCommandCenter.Core.dll")))
                    {
                        best = dir;
                        bestVersion = v;
                    }
                }
            }
            LoaderApp.Log($"Loading core {bestVersion} from {best}");
            return best;
        }

        private static string TryPullUpdate(string remoteDir, string localRoot, Version bundledVersion)
        {
            if (!Directory.Exists(remoteDir)) return null;
            Version remoteVersion = ReadVersion(remoteDir);
            if (remoteVersion <= bundledVersion) return null;

            string target = Path.Combine(localRoot, remoteVersion.ToString());
            if (File.Exists(Path.Combine(target, "BIMCommandCenter.Core.dll"))) return null;

            string staging = target + ".staging-" + Process.GetCurrentProcess().Id;
            CopyDirectory(remoteDir, staging);
            if (Directory.Exists(target)) Directory.Delete(target, true);
            Directory.Move(staging, target);
            return target;
        }

        private static Version ReadVersion(string dir)
        {
            try
            {
                string file = Path.Combine(dir, "version.txt");
                if (File.Exists(file) && Version.TryParse(File.ReadAllText(file).Trim(), out Version v)) return v;
            }
            catch { }
            return new Version(0, 0, 0);
        }

        private static string ReadUpdateSource(string bundledCoreDir)
        {
            // User override first, then the bundled config. Regex keeps the loader dependency-free.
            string[] candidates =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMCommandCenter", "config.json"),
                Path.Combine(bundledCoreDir, "config.json")
            };
            foreach (string file in candidates.Where(File.Exists))
            {
                Match m = Regex.Match(File.ReadAllText(file), "\"updateSource\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
                if (m.Success) return Regex.Unescape(m.Groups[1].Value);
            }
            return null;
        }

        private static void CopyDirectory(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (string file in Directory.GetFiles(source))
                File.Copy(file, Path.Combine(dest, Path.GetFileName(file)), true);
            foreach (string dir in Directory.GetDirectories(source))
                CopyDirectory(dir, Path.Combine(dest, Path.GetFileName(dir)));
        }
    }
}
