using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMCommandCenter.Core.Infrastructure;

namespace BIMCommandCenter.Core.Revit
{
    public class RunResult
    {
        public bool Ok { get; set; }
        public string Message { get; set; }
        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object> { ["ok"] = Ok, ["message"] = Message };
        public static RunResult Success(string m) => new RunResult { Ok = true, Message = m };
        public static RunResult Fail(string m) => new RunResult { Ok = false, Message = m };
    }

    /// <summary>
    /// Universal dispatcher: routes a catalog entry to the right engine.
    /// Always called on the Revit main thread via RevitDispatcher.
    /// </summary>
    public class ToolRunner
    {
        private readonly AppConfig _config;
        public ToolRunner(AppConfig config) => _config = config;

        public RunResult Run(UIApplication app, Dictionary<string, object> tool)
        {
            string actionType = tool.Str("actionType");
            string target = tool.Str("targetPath");
            string title = tool.Str("title");

            if (actionType != "Url" && app.ActiveUIDocument == null)
                return RunResult.Fail("Open a Revit project first, then run \"" + title + "\".");

            switch (actionType)
            {
                case "Dynamo":
                    return RunDynamo(app, tool, ResolvePath(target));
                case "PyRevit":
                    return RunRibbonCommand(app, target, title);
                case "NativeAddin":
                    return RunNativeAddin(app, target, title, tool);
                case "PostableCommand":
                    return RunPostable(app, target, title);
                case "Url":
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    return RunResult.Success("Opened " + target);
                default:
                    return RunResult.Fail("Unknown actionType \"" + actionType + "\" for " + title);
            }
        }

        private string ResolvePath(string target)
        {
            target = Environment.ExpandEnvironmentVariables(target ?? "");
            if (Path.IsPathRooted(target) || string.IsNullOrWhiteSpace(_config.ScriptsRoot)) return target;
            return Path.Combine(_config.ScriptsRoot, target.TrimStart('\\', '/'));
        }

        // ---------------- Dynamo ----------------

        private RunResult RunDynamo(UIApplication app, Dictionary<string, object> tool, string dynPath)
        {
            if (!File.Exists(dynPath))
                return RunResult.Fail("Dynamo graph not found:\n" + dynPath + "\n\nCheck that the scripts folder is synced on this PC.");

            List<string> missing = FindMissingPackages(tool.Str("packagesRequired"));
            if (missing.Count > 0)
                return RunResult.Fail("Missing Dynamo package(s): " + string.Join(", ", missing) +
                                      ".\nInstall them via Dynamo > Packages, or ask the BIM team to add them to the synced packages folder.");

            bool headless = tool.Str("executionMode") == "Headless";
            return DynamoRunner.Run(app, dynPath, headless);
        }

        /// <summary>Looks for package folders under %AppData%\Dynamo\Dynamo Revit\&lt;version&gt;\packages.</summary>
        private static List<string> FindMissingPackages(string packagesRequired)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(packagesRequired)) return missing;

            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dynamo", "Dynamo Revit");
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Directory.Exists(root))
                foreach (string versionDir in Directory.GetDirectories(root))
                {
                    string pkgDir = Path.Combine(versionDir, "packages");
                    if (Directory.Exists(pkgDir))
                        foreach (string p in Directory.GetDirectories(pkgDir)) installed.Add(Path.GetFileName(p));
                }

            foreach (string entry in packagesRequired.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string name = Regex.Replace(entry, @"\(.*?\)", "").Trim();   // "Clockwork (2.4.0)" -> "Clockwork"
                if (name.Length > 0 && !installed.Contains(name)) missing.Add(name);
            }
            return missing;
        }

        // ---------------- Ribbon buttons (pyRevit + any add-in) ----------------

        /// <summary>
        /// Any ribbon button (pyRevit or third-party add-in) can be posted by its command id, e.g.
        /// "CustomCtrl_%CustomCtrl_%pyRevit%Tools%MyButton". Find ids in the Revit journal after clicking the button once.
        /// </summary>
        private static RunResult RunRibbonCommand(UIApplication app, string commandId, string title)
        {
            RevitCommandId id = RevitCommandId.LookupCommandId(commandId);
            if (id == null)
                return RunResult.Fail("Ribbon command not found: " + commandId + "\nIs the add-in / pyRevit extension loaded on this PC?");
            if (!app.CanPostCommand(id))
                return RunResult.Fail("Revit can't run \"" + title + "\" right now (another command may be active).");
            app.PostCommand(id);
            return RunResult.Success("Started " + title);
        }

        private static RunResult RunPostable(UIApplication app, string commandName, string title)
        {
            if (!Enum.TryParse(commandName, true, out PostableCommand cmd))
                return RunResult.Fail("Unknown Revit command: " + commandName);
            RevitCommandId id = RevitCommandId.LookupPostableCommandId(cmd);
            if (!app.CanPostCommand(id))
                return RunResult.Fail("Revit can't run \"" + title + "\" right now.");
            app.PostCommand(id);
            return RunResult.Success("Started " + title);
        }

        private RunResult RunNativeAddin(UIApplication app, string target, string title, Dictionary<string, object> tool)
        {
            string resolved = ResolvePath(target);

            // 1. If target is or points to a .dll file:
            if (!string.IsNullOrEmpty(resolved) && resolved.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(resolved))
            {
                return RunAssemblyCommand(app, resolved, title);
            }

            // 2. Try looking up Ribbon Command directly
            RevitCommandId id = RevitCommandId.LookupCommandId(target);
            if (id != null && app.CanPostCommand(id))
            {
                app.PostCommand(id);
                return RunResult.Success("Started " + title);
            }

            // 3. Try alternative ribbon ID variations
            string[] variations = new[]
            {
                "CustomCtrl_%CustomCtrl_%" + target.TrimStart('%'),
                "CustomCtrl_%CustomCtrl_%BIM Health%Analysis%cmdHealthCheck",
                "CustomCtrl_%CustomCtrl_%" + target.Replace("/", "%").Replace("\\", "%")
            };
            foreach (var v in variations)
            {
                id = RevitCommandId.LookupCommandId(v);
                if (id != null && app.CanPostCommand(id))
                {
                    app.PostCommand(id);
                    return RunResult.Success("Started " + title);
                }
            }

            // 4. Fallback: if target or title is Health Check, check if BIMHealthCheck.dll is installed in Revit AddIns
            if (target.IndexOf("Health", StringComparison.OrdinalIgnoreCase) >= 0 || title.IndexOf("Health", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string year = app.Application.VersionNumber;
                string candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", year, "BIMHealthCheck", "BIMHealthCheck.dll");
                if (File.Exists(candidate))
                {
                    return RunAssemblyCommand(app, candidate, title);
                }
            }

            return RunResult.Fail("Ribbon command not found: " + target + "\nMake sure the add-in is installed in Revit.");
        }

        private static RunResult RunAssemblyCommand(UIApplication app, string dllPath, string title)
        {
            try
            {
                Assembly asm = Assembly.LoadFrom(dllPath);

                // Check if it's BIMHealthCheck engine
                Type engineType = asm.GetType("BIMHealthCheck.Engine.HealthCheckEngine");
                if (engineType != null)
                {
                    object engine = Activator.CreateInstance(engineType);
                    MethodInfo runMethod = engineType.GetMethod("RunAllChecks");
                    object score = runMethod.Invoke(engine, new object[] { app.ActiveUIDocument.Document });
                    Type dialogType = asm.GetType("BIMHealthCheck.UI.HealthCheckDialog");
                    var dialog = (System.Windows.Window)Activator.CreateInstance(dialogType, new object[] { score });
                    dialog.ShowDialog();
                    return RunResult.Success("Successfully completed " + title);
                }

                return RunResult.Fail("Could not locate executable entry point in " + Path.GetFileName(dllPath));
            }
            catch (Exception ex)
            {
                return RunResult.Fail("Error executing " + title + ": " + (ex.InnerException?.Message ?? ex.Message));
            }
        }
    }

    /// <summary>
    /// Runs a .dyn through Dynamo for Revit using its journal-key automation entry point.
    /// Uses reflection so we don't compile against a specific Dynamo version.
    /// NOTE: verify on each Revit/Dynamo version you support.
    /// </summary>
    internal static class DynamoRunner
    {
        public static RunResult Run(UIApplication app, string dynPath, bool headless)
        {
            Assembly dynamoRevit = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "DynamoRevitDS");

            if (dynamoRevit == null)
            {
                string revitDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                string candidate = Path.Combine(revitDir, "AddIns", "DynamoForRevit", "Revit", "DynamoRevitDS.dll");
                if (!File.Exists(candidate))
                    return RunResult.Fail("Dynamo for Revit was not found in this Revit installation.");
                dynamoRevit = Assembly.LoadFrom(candidate);
            }

            Type dynType = dynamoRevit.GetType("Dynamo.Applications.DynamoRevit");
            Type dataType = dynamoRevit.GetType("Dynamo.Applications.DynamoRevitCommandData");
            if (dynType == null || dataType == null)
                return RunResult.Fail("This Dynamo version doesn't expose the automation API we use.");

            var journal = new Dictionary<string, string>
            {
                ["dynPath"] = dynPath,
                ["dynShowUI"] = headless ? "false" : "true",
                ["dynAutomation"] = headless ? "true" : "false",
                ["dynPathExecute"] = headless ? "true" : "false",
                ["dynModelShutDown"] = headless ? "true" : "false",
                ["dynForceManualRun"] = headless ? "false" : "true"
            };

            object data = Activator.CreateInstance(dataType);
            dataType.GetProperty("Application")?.SetValue(data, app);
            dataType.GetProperty("JournalData")?.SetValue(data, journal);

            object dynamo = Activator.CreateInstance(dynType);
            MethodInfo execute = dynType.GetMethod("ExecuteCommand", new[] { dataType });
            if (execute == null) return RunResult.Fail("Dynamo ExecuteCommand entry point not found.");

            object result = execute.Invoke(dynamo, new[] { data });
            bool ok = result is Result r ? r == Result.Succeeded : true;

            string name = Path.GetFileNameWithoutExtension(dynPath);
            if (!ok) return RunResult.Fail("Dynamo reported a failure running " + name + ".");
            return RunResult.Success(headless ? name + " ran successfully." : name + " is open in Dynamo — set the inputs and press Run.");
        }
    }
}
