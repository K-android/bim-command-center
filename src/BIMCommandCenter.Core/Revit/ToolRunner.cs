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
        public string Status { get; set; } = "Completed";
        public string Message { get; set; }
        public List<string> ChangedItems { get; set; } = new List<string>();
        public List<string> Outputs { get; set; } = new List<string>();
        public List<string> Warnings { get; set; } = new List<string>();
        public List<string> Errors { get; set; } = new List<string>();
        public string ExecutionId { get; set; } = Guid.NewGuid().ToString("N");
        public double DurationSeconds { get; set; }
        public DateTime ExecutedAt { get; set; } = DateTime.Now;

        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
        {
            ["ok"] = Ok,
            ["status"] = Status,
            ["message"] = Message,
            ["changedItems"] = ChangedItems ?? new List<string>(),
            ["outputs"] = Outputs ?? new List<string>(),
            ["warnings"] = Warnings ?? new List<string>(),
            ["errors"] = Errors ?? new List<string>(),
            ["executionId"] = ExecutionId,
            ["durationSeconds"] = DurationSeconds,
            ["executedAt"] = ExecutedAt.ToString("o")
        };

        public static RunResult Success(string m, List<string> changed = null, List<string> outputs = null, List<string> warnings = null) => new RunResult
        {
            Ok = true,
            Status = (warnings != null && warnings.Count > 0) ? "Warning" : "Completed",
            Message = m,
            ChangedItems = changed ?? new List<string>(),
            Outputs = outputs ?? new List<string>(),
            Warnings = warnings ?? new List<string>()
        };

        public static RunResult Fail(string m, List<string> errors = null) => new RunResult
        {
            Ok = false,
            Status = "Failed",
            Message = m,
            Errors = errors ?? (string.IsNullOrEmpty(m) ? new List<string>() : new List<string> { m })
        };
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
                case "Grasshopper":
                case "Rhino.Inside":
                case "Rhino":
                    return RunGrasshopperOrRhino(app, tool, ResolvePath(target));
                case "Url":
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                    return RunResult.Success("Opened " + target);
                default:
                    return RunResult.Fail("Unknown actionType \"" + actionType + "\" for " + title);
            }
        }

        private RunResult RunGrasshopperOrRhino(UIApplication app, Dictionary<string, object> tool, string scriptPath)
        {
            string title = tool.Str("title");

            // 1. Try invoking Rhino.Inside.Revit if loaded in this Revit session
            try
            {
                var rhinoCmd = RevitCommandId.LookupCommandId("CustomCtrl_%CustomCtrl_%Rhinoceros%Rhinoceros%CommandStartRhino");
                if (rhinoCmd != null && app.CanPostCommand(rhinoCmd))
                {
                    app.PostCommand(rhinoCmd);
                    if (File.Exists(scriptPath))
                    {
                        Process.Start(new ProcessStartInfo(scriptPath) { UseShellExecute = true });
                    }
                    return RunResult.Success("Launched Rhino.Inside session with " + title);
                }
            }
            catch { }

            // 2. Check for Standalone Rhino executable (Rhino 8 or Rhino 7)
            string[] possibleRhinoPaths = new[]
            {
                @"C:\Program Files\Rhino 8\System\Rhino.exe",
                @"C:\Program Files\Rhino 7\System\Rhino.exe",
                @"C:\Program Files\Rhinoceros 7\System\Rhino.exe"
            };

            foreach (var rExe in possibleRhinoPaths)
            {
                if (File.Exists(rExe))
                {
                    var psi = new ProcessStartInfo(rExe);
                    if (File.Exists(scriptPath))
                    {
                        psi.Arguments = $"\"{scriptPath}\"";
                    }
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    return RunResult.Success("Launched standalone Rhino: " + title);
                }
            }

            // 3. Fallback: If .gh file exists, try default Windows file association
            if (File.Exists(scriptPath))
            {
                Process.Start(new ProcessStartInfo(scriptPath) { UseShellExecute = true });
                return RunResult.Success("Opened " + Path.GetFileName(scriptPath) + " in default viewer");
            }

            return RunResult.Fail("Grasshopper definition requires Rhino 7/8 or Rhino.Inside.Revit on this workstation.\n\nFile: " + scriptPath);
        }

        private string ResolvePath(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return target;
            target = Environment.ExpandEnvironmentVariables(target.Trim());
            if (Path.IsPathRooted(target) && File.Exists(target)) return target;

            // 1. Check central scripts root if set
            if (!string.IsNullOrWhiteSpace(_config.ScriptsRoot))
            {
                string inScripts = Path.Combine(_config.ScriptsRoot, target.TrimStart('\\', '/'));
                if (File.Exists(inScripts)) return inScripts;
            }

            // 2. Check bundled core folder
            string inCore = Path.Combine(AppConfig.CoreDir, target.TrimStart('\\', '/'));
            if (File.Exists(inCore)) return inCore;

            return Path.IsPathRooted(target) ? target : inCore;
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

            // 4. Fallback: if target or title is Health Check, check:
            //    A) Local user AppData add-in (picks up newer manual builds)
            //    B) Bundled tools/BIMHealthCheck.dll shipped with BIM Command Center
            if (target.IndexOf("Health", StringComparison.OrdinalIgnoreCase) >= 0 || title.IndexOf("Health", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string year = app.Application.VersionNumber;
                string candidate = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Autodesk", "Revit", "Addins", year, "BIMHealthCheck", "BIMHealthCheck.dll");
                if (File.Exists(candidate))
                {
                    return RunAssemblyCommand(app, candidate, title);
                }

                string bundled = Path.Combine(AppConfig.CoreDir, "tools", "BIMHealthCheck.dll");
                if (File.Exists(bundled))
                {
                    return RunAssemblyCommand(app, bundled, title);
                }
            }

            return RunResult.Fail("Command or add-in not found: " + target + "\nMake sure the add-in is installed or located in your tools folder.");
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
