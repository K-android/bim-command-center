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
            var status = RhinoInsideDetector.Detect(app);

            // 1. If Rhino.Inside.Revit is active and command is available, invoke it
            if (status.IsInstalled)
            {
                if (status.LaunchCommand != null && app.CanPostCommand(status.LaunchCommand))
                {
                    try
                    {
                        app.PostCommand(status.LaunchCommand);
                        if (File.Exists(scriptPath))
                        {
                            Process.Start(new ProcessStartInfo(scriptPath) { UseShellExecute = true });
                        }
                        return RunResult.Success("Launched Grasshopper via Rhino.Inside.Revit: " + title);
                    }
                    catch (Exception ex)
                    {
                        return RunResult.Fail("Failed to post Rhino.Inside command: " + ex.Message);
                    }
                }

                if (status.IsLoaded)
                {
                    if (File.Exists(scriptPath))
                    {
                        Process.Start(new ProcessStartInfo(scriptPath) { UseShellExecute = true });
                    }
                    return RunResult.Success("Dispatched to active Rhino.Inside session: " + title);
                }

                // Installed but not yet started
                return RunResult.Fail(
                    "Rhino.Inside.Revit is installed, but the Rhino runtime is not currently active.\n\n" +
                    "Please click the 'Start' button on the Rhinoceros tab in Revit to initialize Rhino.Inside, then run this tool again."
                );
            }

            // 2. Rhino.Inside is NOT detected or installed
            return RunResult.Fail(
                "Rhino.Inside.Revit is not detected on this workstation.\n\n" +
                "Grasshopper (.gh) automations require Rhino.Inside.Revit to stream and synchronize geometry live into Revit.\n\n" +
                "Please download and install Rhino.Inside.Revit (free for Rhino 7 & 8 license holders) from:\n" +
                "https://www.rhino3d.com/inside/revit/8/"
            );
        }

        public string ResolvePath(string target)
        {
            if (string.IsNullOrWhiteSpace(target)) return target;
            target = Environment.ExpandEnvironmentVariables(target.Trim());
            if (Path.IsPathRooted(target) && File.Exists(target)) return target;

            // 1. Check central scripts root if set
            if (!string.IsNullOrWhiteSpace(_config.ScriptsRoot))
            {
                string inScripts = Path.Combine(_config.ScriptsRoot, target.TrimStart('\\', '/'));
                if (File.Exists(inScripts)) return inScripts;

                // Also check categorized subfolders if target is a simple filename or relative path
                string fileName = Path.GetFileName(target);
                string[] subfolders = new[] { "Dynamo", "PyRevit", "Custom Addins", "Grasshopper", "Rhino" };
                foreach (var sub in subfolders)
                {
                    string inSub = Path.Combine(_config.ScriptsRoot, sub, fileName);
                    if (File.Exists(inSub)) return inSub;
                }
            }

            // 2. Check bundled core folder
            string inCore = Path.Combine(AppConfig.CoreDir, target.TrimStart('\\', '/'));
            if (File.Exists(inCore)) return inCore;

            return Path.IsPathRooted(target) ? target : inCore;
        }

        // ---------------- Dynamo (Dynamo Player) ----------------

        private RunResult RunDynamo(UIApplication app, Dictionary<string, object> tool, string dynPath)
        {
            if (!File.Exists(dynPath))
                return RunResult.Fail("Dynamo graph not found:\n" + dynPath + "\n\nVerify that the script is located in the Central Library:\n" + _config.ScriptsRoot);

            var pkgCheck = DynamoPackageInspector.CheckPackages(tool.Str("packagesRequired"));
            if (pkgCheck.HasMissing)
            {
                return RunResult.Fail(pkgCheck.Instructions);
            }

            string scriptName = Path.GetFileName(dynPath);

            // Launch Dynamo Player in Revit
            string[] candidateIds = new[]
            {
                "ID_VISUAL_PROGRAMMING_DYNAMOPLAYER",
                "DynamoPlayer",
                "CustomCtrl_%CustomCtrl_%Manage%Visual Programming%DynamoPlayer"
            };

            RevitCommandId playerCmdId = null;
            foreach (var idStr in candidateIds)
            {
                var cmd = RevitCommandId.LookupCommandId(idStr);
                if (cmd != null && app.CanPostCommand(cmd))
                {
                    playerCmdId = cmd;
                    break;
                }
            }

            if (playerCmdId != null)
            {
                app.PostCommand(playerCmdId);
                return RunResult.Success($"Dynamo Player launched.\nSelect \"{scriptName}\" in Dynamo Player to provide inputs and view outputs without opening the node editor.");
            }

            return RunResult.Fail("Revit cannot post the Dynamo Player command right now (another dialog or transaction may be active). Please open Dynamo Player from the Manage ribbon tab.");
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
