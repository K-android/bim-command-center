using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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

        public RunResult Run(UIApplication app, Dictionary<string, object> tool, Dictionary<string, string> inputs = null)
        {
            string actionType = tool.Str("actionType");
            string target = tool.Str("targetPath");
            string title = tool.Str("title");

            if (actionType != "Url" && app.ActiveUIDocument == null)
                return RunResult.Fail("Open a Revit project first, then run \"" + title + "\".");

            switch (actionType)
            {
                case "Dynamo":
                    return RunDynamo(app, tool, ResolvePath(target), inputs);
                case "PyRevit":
                    return RunPyRevit(app, target, title, tool);
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
            if (Path.IsPathRooted(target))
            {
                if (File.Exists(target)) return target;
                if (Directory.Exists(target))
                {
                    string innerScript = Path.Combine(target, "script.py");
                    if (File.Exists(innerScript)) return innerScript;
                    return target;
                }
            }

            // 1. Check central scripts root if set
            if (!string.IsNullOrWhiteSpace(_config.ScriptsRoot))
            {
                string inScripts = Path.Combine(_config.ScriptsRoot, target.TrimStart('\\', '/'));
                if (File.Exists(inScripts)) return inScripts;
                if (Directory.Exists(inScripts))
                {
                    string innerScript = Path.Combine(inScripts, "script.py");
                    if (File.Exists(innerScript)) return innerScript;
                }

                // Also check categorized subfolders if target is a simple filename or relative path
                string fileName = Path.GetFileName(target);
                string[] subfolders = new[] { "Dynamo", "PyRevit", "Custom Addins", "Grasshopper", "Rhino" };
                foreach (var sub in subfolders)
                {
                    string inSub = Path.Combine(_config.ScriptsRoot, sub, fileName);
                    if (File.Exists(inSub)) return inSub;
                    if (Directory.Exists(inSub))
                    {
                        string innerScript = Path.Combine(inSub, "script.py");
                        if (File.Exists(innerScript)) return innerScript;
                    }
                }
            }

            // 2. Check bundled core folder
            string inCore = Path.Combine(AppConfig.CoreDir, target.TrimStart('\\', '/'));
            if (File.Exists(inCore)) return inCore;
            if (Directory.Exists(inCore))
            {
                string innerScript = Path.Combine(inCore, "script.py");
                if (File.Exists(innerScript)) return innerScript;
            }

            return Path.IsPathRooted(target) ? target : inCore;
        }

        // ---------------- Dynamo (Direct Headless & Parameter Injection) ----------------

        private RunResult RunDynamo(UIApplication app, Dictionary<string, object> tool, string dynPath, Dictionary<string, string> userInputs = null)
        {
            if (string.IsNullOrWhiteSpace(dynPath) || !File.Exists(dynPath))
            {
                // Fallback attempt to resolve path from tool targetPath
                string rawTarget = tool.Str("targetPath");
                string resolved = ResolvePath(rawTarget);
                if (File.Exists(resolved))
                {
                    dynPath = resolved;
                }
                else
                {
                    return RunResult.Fail("Dynamo graph not found:\n" + (dynPath ?? rawTarget) + "\n\nVerify that the script is located in the Central Library:\n" + _config.ScriptsRoot);
                }
            }

            var pkgCheck = DynamoPackageInspector.CheckPackages(tool.Str("packagesRequired"));
            string scriptName = Path.GetFileName(dynPath);

            // If user explicitly requested opening Dynamo Player UI via fallback button
            if (userInputs != null && userInputs.ContainsKey("__open_in_player__"))
            {
                return LaunchDynamoPlayer(app, dynPath, tool, pkgCheck);
            }

            // Step 1 & Step 3: Direct Execution with Parameter Injection!
            // Supports both headless injection and native Data-Shapes dialog popup!
            bool runNativeUi = userInputs != null && userInputs.ContainsKey("__run_native_ui__");
            try
            {
                var graphInfo = DynamoPackageInspector.InspectGraph(dynPath);
                if (graphInfo != null && graphInfo.HasDataShapesForm)
                {
                    // Data-Shapes UI.MultipleInputForm++ requires non-headless Dynamo evaluation so its IronPython WinForms dialog
                    // and multi-output ports (User Inputs, Was Run, Was Cancelled) execute natively.
                    runNativeUi = true;
                }
            }
            catch { }

            string pathToExecute = dynPath;
            bool isTemp = false;

            if ((userInputs != null && userInputs.Count > 0) || runNativeUi)
            {
                string temp = DynamoPackageInspector.CreateTempGraphWithInputs(dynPath, userInputs ?? new Dictionary<string, string>());
                if (!string.IsNullOrEmpty(temp) && temp != dynPath && File.Exists(temp))
                {
                    pathToExecute = temp;
                    isTemp = true;
                }
            }

            try
            {
                Log.Write($"Running Dynamo script '{scriptName}' (runNativeUi={runNativeUi}) via DynamoRevit journal automation.");
                var runRes = DynamoRunner.Run(app, pathToExecute, headless: !runNativeUi, enableNativeUi: runNativeUi);
                if (pkgCheck != null && pkgCheck.HasMissing && runRes != null)
                {
                    runRes.Warnings.Add("Note: Missing Dynamo package(s): " + string.Join(", ", pkgCheck.Missing) + ". Install via Revit Manage > Dynamo > Packages.");
                }

                if (runRes != null && !runRes.Ok)
                {
                    Log.Write($"Headless Dynamo run was not ok ({runRes.Message}). Falling back to Dynamo Player.");
                    var playerRes = LaunchDynamoPlayer(app, dynPath, tool, pkgCheck);
                    if (playerRes != null && playerRes.Ok)
                    {
                        playerRes.Warnings.Add("Notice: Script launched in Dynamo Player for interactive execution.");
                        return playerRes;
                    }
                }

                return runRes ?? RunResult.Fail("Dynamo execution did not return a result.");
            }
            catch (Exception ex)
            {
                Log.Write("Headless Dynamo execution exception: " + ex.Message + ". Falling back to Dynamo Player.");
                var playerRes = LaunchDynamoPlayer(app, dynPath, tool, pkgCheck);
                if (playerRes != null && playerRes.Ok)
                {
                    playerRes.Warnings.Add("Notice: Script launched in Dynamo Player for interactive execution.");
                    return playerRes;
                }
                return RunResult.Fail("Error executing " + scriptName + ": " + ex.Message);
            }
            finally
            {
                if (isTemp)
                {
                    try { File.Delete(pathToExecute); } catch { }
                }
            }
        }

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SetFocus(IntPtr hWnd);

        public RunResult LaunchDynamoPlayer(UIApplication app, string dynPath, Dictionary<string, object> tool = null, DynamoPackageCheckResult pkgCheck = null)
        {
            try
            {
                string scriptDir = Path.GetDirectoryName(dynPath);
                DynamoPlayerHelper.EnsureFolderInDynamoSettings(scriptDir, "Central Library");
            }
            catch (Exception ex)
            {
                Log.Write("EnsureFolderInDynamoSettings failed: " + ex.Message);
            }

            // 1. Only skip launching if the actual "Dynamo Player" top-level window is currently open & visible on screen.
            if (DynamoPlayerHelper.IsDynamoPlayerWindowOpen(out IntPtr playerHwnd))
            {
                Log.Write("Dynamo Player window is already open. Bringing to front and sending graph open request.");
                DynamoPlayerHelper.BringWindowToFront(playerHwnd);
                _ = Task.Run(() => DynamoPlayerHelper.TryOpenGraphInDynamoPlayerAsync(dynPath));
                return RunResult.Success($"Dynamo Player is open. Loaded \"{Path.GetFileName(dynPath)}\".");
            }

            // 2. Transfer window focus to Revit main window so Revit 2025/2026 (.NET 8) allows command execution
            if (app != null && app.MainWindowHandle != IntPtr.Zero)
            {
                try
                {
                    SetForegroundWindow(app.MainWindowHandle);
                    SetFocus(app.MainWindowHandle);
                }
                catch { }
            }

            // 3. Try PostableCommand.DynamoPlayer first
            if (Enum.TryParse("DynamoPlayer", true, out PostableCommand dpPostable))
            {
                try
                {
                    RevitCommandId postableId = RevitCommandId.LookupPostableCommandId(dpPostable);
                    if (postableId != null && app.CanPostCommand(postableId))
                    {
                        app.PostCommand(postableId);
                        _ = Task.Run(() => DynamoPlayerHelper.TryOpenGraphInDynamoPlayerAsync(dynPath));
                        return RunResult.Success($"Opened Dynamo Player for \"{Path.GetFileName(dynPath)}\".");
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("PostableCommand.DynamoPlayer failed: " + ex.Message);
                }
            }

            // 4. Try standard Revit command IDs
            string[] candidateIds = new[]
            {
                "ID_PLAYLIST_DYNAMO",
                "ID_STEEL_PLAYLIST_DYNAMO",
                "ID_ANALYTICAL_AUTOMATION_PLAYLIST_DYNAMO",
                "CustomCtrl_%CustomCtrl_%Manage%Visual Programming%DynamoPlayer",
                "CustomCtrl_%CustomCtrl_%Manage%Visual Programming%VisualProgramming_DynamoPlayer",
                "DynamoPlayer",
                "ID_VISUAL_PROGRAMMING_DYNAMOPLAYER"
            };

            foreach (var idStr in candidateIds)
            {
                var cmd = RevitCommandId.LookupCommandId(idStr);
                if (cmd != null && app.CanPostCommand(cmd))
                {
                    try
                    {
                        app.PostCommand(cmd);
                        _ = Task.Run(() => DynamoPlayerHelper.TryOpenGraphInDynamoPlayerAsync(dynPath));
                        return RunResult.Success($"Opened Dynamo Player for \"{Path.GetFileName(dynPath)}\".");
                    }
                    catch (Exception ex)
                    {
                        Log.Write("PostCommand failed for " + idStr + ": " + ex.Message);
                    }
                }
            }

            // 5. Fallback: ComponentManager direct ribbon execution (traverses SplitButtons & bypasses WebView2 focus restrictions)
            if (DynamoPlayerHelper.TryInvokeRibbonDynamoPlayer())
            {
                _ = Task.Run(() => DynamoPlayerHelper.TryOpenGraphInDynamoPlayerAsync(dynPath));
                return RunResult.Success($"Opened Dynamo Player for \"{Path.GetFileName(dynPath)}\".");
            }

            // 5. Fallback: Dynamo canvas itself if player command is unavailable
            var canvasCmd = RevitCommandId.LookupCommandId("ID_VISUAL_PROGRAMMING_DYNAMO");
            if (canvasCmd != null && app.CanPostCommand(canvasCmd))
            {
                try
                {
                    app.PostCommand(canvasCmd);
                    return RunResult.Success($"Dynamo Player was busy; opened Dynamo canvas for \"{Path.GetFileName(dynPath)}\".");
                }
                catch { }
            }

            return RunResult.Fail("Dynamo Player command ('ID_PLAYLIST_DYNAMO') is busy or another transaction is active in Revit. Try clicking inside Revit view to restore focus.");
        }

        private RunResult RunPyRevit(UIApplication app, string target, string title, Dictionary<string, object> tool)
        {
            string resolved = ResolvePath(target);

            // If target or resolved is a directory, look for script.py inside
            if (!string.IsNullOrEmpty(resolved) && Directory.Exists(resolved))
            {
                string scriptInDir = Path.Combine(resolved, "script.py");
                if (File.Exists(scriptInDir)) resolved = scriptInDir;
            }
            else if (!string.IsNullOrEmpty(target) && Directory.Exists(target))
            {
                string scriptInDir = Path.Combine(target, "script.py");
                if (File.Exists(scriptInDir)) resolved = scriptInDir;
            }

            // 1. If target is already a registered Ribbon Command ID:
            RevitCommandId id = RevitCommandId.LookupCommandId(target);
            if (id != null && app.CanPostCommand(id))
            {
                app.PostCommand(id);
                return RunResult.Success("Started " + title);
            }

            // 2. Try looking up ribbon command by script name or button variations
            string baseName = Path.GetFileNameWithoutExtension(resolved ?? target);
            if (string.Equals(baseName, "script", StringComparison.OrdinalIgnoreCase))
            {
                string dir = Path.GetDirectoryName(resolved ?? target);
                if (!string.IsNullOrEmpty(dir))
                {
                    string parentFolder = Path.GetFileName(dir);
                    baseName = Regex.Replace(parentFolder, @"\.(pushbutton|smartbutton|urlbutton|splitbutton|pulldown)$", "", RegexOptions.IgnoreCase);
                }
            }

            string[] variations = new[]
            {
                "CustomCtrl_%CustomCtrl_%" + target.TrimStart('%'),
                "CustomCtrl_%CustomCtrl_%pyRevit%" + baseName,
                "CustomCtrl_%CustomCtrl_%pyRevit%Tools%" + baseName,
                "CustomCtrl_%CustomCtrl_%" + baseName,
                "CustomCtrl_%CustomCtrl_%" + title.Replace(" ", "")
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

            // 3. Search Revit Ribbon dynamically via ComponentManager for any matching button
            string ribbonCmdId = FindRibbonCommandId(baseName, title);
            if (!string.IsNullOrEmpty(ribbonCmdId))
            {
                id = RevitCommandId.LookupCommandId(ribbonCmdId);
                if (id != null && app.CanPostCommand(id))
                {
                    app.PostCommand(id);
                    return RunResult.Success("Started " + title);
                }
            }

            // 4. Standalone & Central Execution: Run via embedded PythonRunner!
            // Zero-install for team members — pyRevit does NOT need to be installed on every machine!
            if (!string.IsNullOrEmpty(resolved) && (resolved.EndsWith(".py", StringComparison.OrdinalIgnoreCase) || File.Exists(resolved)))
            {
                return PythonRunner.Run(app, resolved, title);
            }

            return RunResult.Fail(
                $"Python script not found: \"{title}\".\n\n" +
                $"Path checked: {resolved ?? target}\n\n" +
                "Verify that the script is located in the Central Library:\n" + _config.ScriptsRoot
            );
        }

        private static string FindRibbonCommandId(string baseName, string title)
        {
            try
            {
                Assembly adWindows = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AdWindows");
                if (adWindows == null) return null;

                Type cmType = adWindows.GetType("Autodesk.Windows.ComponentManager");
                PropertyInfo rcProp = cmType?.GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static);
                object ribbon = rcProp?.GetValue(null);
                if (ribbon == null) return null;

                PropertyInfo tabsProp = ribbon.GetType().GetProperty("Tabs");
                IEnumerable tabs = tabsProp?.GetValue(ribbon) as IEnumerable;
                if (tabs == null) return null;

                string cleanBase = Regex.Replace(baseName ?? "", @"[^a-zA-Z0-9]", "").ToLowerInvariant();
                string cleanTitle = Regex.Replace(title ?? "", @"[^a-zA-Z0-9]", "").ToLowerInvariant();

                foreach (object tab in tabs)
                {
                    PropertyInfo panelsProp = tab.GetType().GetProperty("Panels");
                    IEnumerable panels = panelsProp?.GetValue(tab) as IEnumerable;
                    if (panels == null) continue;

                    foreach (object panel in panels)
                    {
                        PropertyInfo sourceProp = panel.GetType().GetProperty("Source");
                        object source = sourceProp?.GetValue(panel);
                        if (source == null) continue;

                        PropertyInfo itemsProp = source.GetType().GetProperty("Items");
                        IEnumerable items = itemsProp?.GetValue(source) as IEnumerable;
                        if (items == null) continue;

                        foreach (object item in items)
                        {
                            string match = MatchRibbonItem(item, cleanBase, cleanTitle);
                            if (!string.IsNullOrEmpty(match)) return match;
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private static string MatchRibbonItem(object item, string cleanBase, string cleanTitle)
        {
            if (item == null) return null;
            Type itemType = item.GetType();

            string id = itemType.GetProperty("Id")?.GetValue(item) as string;
            string text = itemType.GetProperty("Text")?.GetValue(item) as string;

            string cleanId = Regex.Replace(id ?? "", @"[^a-zA-Z0-9]", "").ToLowerInvariant();
            string cleanText = Regex.Replace(text ?? "", @"[^a-zA-Z0-9]", "").ToLowerInvariant();

            if (!string.IsNullOrEmpty(id))
            {
                if ((!string.IsNullOrEmpty(cleanBase) && (cleanId.EndsWith(cleanBase) || cleanText.Equals(cleanBase))) ||
                    (!string.IsNullOrEmpty(cleanTitle) && (cleanId.EndsWith(cleanTitle) || cleanText.Equals(cleanTitle))))
                {
                    return id;
                }
            }

            PropertyInfo subItemsProp = itemType.GetProperty("Items");
            if (subItemsProp != null)
            {
                IEnumerable subItems = subItemsProp.GetValue(item) as IEnumerable;
                if (subItems != null)
                {
                    foreach (object sub in subItems)
                    {
                        string match = MatchRibbonItem(sub, cleanBase, cleanTitle);
                        if (!string.IsNullOrEmpty(match)) return match;
                    }
                }
            }

            return null;
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
                string cmdClass = tool != null ? tool.Str("commandClass") : null;
                return RunAssemblyCommand(app, resolved, title, cmdClass);
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

            return RunResult.Fail("Command or add-in not found: " + target + "\nMake sure the add-in is located in your central folder.");
        }

        private static readonly HashSet<string> RegisteredProbeDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool _resolverInitialized = false;

        private static void EnsureAssemblyResolver(string directory)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

            lock (RegisteredProbeDirectories)
            {
                RegisteredProbeDirectories.Add(directory);

                foreach (string sub in new[] { "lib", "libs", "bin", "packages", "deps", "dependencies", "runtimes" })
                {
                    string subDir = Path.Combine(directory, sub);
                    if (Directory.Exists(subDir)) RegisteredProbeDirectories.Add(subDir);
                }

                if (!_resolverInitialized)
                {
                    _resolverInitialized = true;
                    AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
                    {
                        try
                        {
                            string asmName = new AssemblyName(args.Name).Name;
                            if (string.IsNullOrEmpty(asmName)) return null;

                            // 1. Check if already loaded in AppDomain to prevent "Assembly with same name is already loaded" in .NET 8
                            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                            {
                                try
                                {
                                    if (!loaded.IsDynamic && string.Equals(loaded.GetName().Name, asmName, StringComparison.OrdinalIgnoreCase))
                                        return loaded;
                                }
                                catch { }
                            }

                            lock (RegisteredProbeDirectories)
                            {
                                foreach (var probeDir in RegisteredProbeDirectories)
                                {
                                    string candidate = Path.Combine(probeDir, asmName + ".dll");
                                    if (File.Exists(candidate))
                                    {
                                        return LoadAssemblySafely(candidate);
                                    }
                                }
                            }
                        }
                        catch { }
                        return null;
                    };
                }
            }
        }

        /// <summary>
        /// Loads a .NET assembly from disk without throwing "Assembly with same name is already loaded"
        /// when the add-in or dependency is already loaded in Revit's AppDomain (e.g. Revit 2025/2026 .NET 8 ALC).
        /// Works seamlessly whether the user has the add-in locally installed in Revit or is running zero-install from the central folder.
        /// </summary>
        internal static Assembly LoadAssemblySafely(string dllPath)
        {
            if (string.IsNullOrWhiteSpace(dllPath) || !File.Exists(dllPath))
                return null;

            string fullPath = Path.GetFullPath(dllPath);
            string targetSimpleName = Path.GetFileNameWithoutExtension(fullPath);
            try
            {
                var defName = AssemblyName.GetAssemblyName(fullPath);
                if (!string.IsNullOrEmpty(defName?.Name))
                    targetSimpleName = defName.Name;
            }
            catch { }

            Assembly matchedByName = null;
            foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    if (loaded.IsDynamic) continue;
                    if (!string.IsNullOrEmpty(loaded.Location) &&
                        string.Equals(Path.GetFullPath(loaded.Location), fullPath, StringComparison.OrdinalIgnoreCase))
                    {
                        return loaded;
                    }
                    if (matchedByName == null &&
                        string.Equals(loaded.GetName().Name, targetSimpleName, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedByName = loaded;
                    }
                }
                catch { }
            }

            // If an assembly with the exact same AssemblyName is already loaded in Revit (either from local Addins or a prior run),
            // reuse it directly so .NET 8 AssemblyLoadContext.Default does not throw FileLoadException("Assembly with same name is already loaded").
            if (matchedByName != null)
            {
                return matchedByName;
            }

            try
            {
                return Assembly.LoadFrom(fullPath);
            }
            catch (Exception ex)
            {
                Log.Write($"LoadAssemblySafely: LoadFrom({fullPath}) threw {ex.GetType().Name}: {ex.Message}. Attempting fallback.");
                foreach (var loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        if (!loaded.IsDynamic && string.Equals(loaded.GetName().Name, targetSimpleName, StringComparison.OrdinalIgnoreCase))
                            return loaded;
                    }
                    catch { }
                }

                try
                {
                    return Assembly.Load(File.ReadAllBytes(fullPath));
                }
                catch
                {
                    throw;
                }
            }
        }

        private static ExternalCommandData CreateExternalCommandData(UIApplication app)
        {
            ExternalCommandData data = null;

            // 1. Try non-public constructors
            try
            {
                var ctors = typeof(ExternalCommandData).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                foreach (var ctor in ctors)
                {
                    var parameters = ctor.GetParameters();
                    if (parameters.Length == 0)
                    {
                        data = (ExternalCommandData)ctor.Invoke(null);
                        break;
                    }
                    if (parameters.Length == 1 && parameters[0].ParameterType.IsAssignableFrom(typeof(UIApplication)))
                    {
                        data = (ExternalCommandData)ctor.Invoke(new object[] { app });
                        break;
                    }
                }
            }
            catch { }

            // 2. Fallback to uninitialized object
            if (data == null)
            {
                try
                {
#if NET8_0_OR_GREATER
                    data = (ExternalCommandData)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(ExternalCommandData));
#else
                    data = (ExternalCommandData)System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(ExternalCommandData));
#endif
                }
                catch { }
            }

            if (data != null)
            {
                // Populate internal fields
                foreach (var field in typeof(ExternalCommandData).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    try
                    {
                        if (field.FieldType.IsAssignableFrom(typeof(UIApplication)))
                        {
                            field.SetValue(data, app);
                        }
                        else if (field.FieldType.IsAssignableFrom(typeof(Autodesk.Revit.DB.View)) && app.ActiveUIDocument != null)
                        {
                            field.SetValue(data, app.ActiveUIDocument.ActiveView);
                        }
                        else if (field.FieldType.IsAssignableFrom(typeof(Dictionary<string, string>)) ||
                                 typeof(IDictionary<string, string>).IsAssignableFrom(field.FieldType))
                        {
                            field.SetValue(data, new Dictionary<string, string>());
                        }
                    }
                    catch { }
                }

                // Populate properties if writable
                foreach (var prop in typeof(ExternalCommandData).GetProperties(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    try
                    {
                        if (prop.CanWrite)
                        {
                            if (prop.PropertyType.IsAssignableFrom(typeof(UIApplication))) prop.SetValue(data, app);
                            else if (prop.PropertyType.IsAssignableFrom(typeof(Autodesk.Revit.DB.View)) && app.ActiveUIDocument != null) prop.SetValue(data, app.ActiveUIDocument.ActiveView);
                        }
                    }
                    catch { }
                }
            }

            return data;
        }

        private static RunResult RunAssemblyCommand(UIApplication app, string dllPath, string title, string commandClassName = null)
        {
            try
            {
                string dir = Path.GetDirectoryName(dllPath);
                EnsureAssemblyResolver(dir);

                Assembly asm = LoadAssemblySafely(dllPath);
                if (asm == null)
                {
                    return RunResult.Fail("Could not load assembly: " + Path.GetFileName(dllPath));
                }

                // 1. Check if it's BIMHealthCheck engine
                Type healthCheckType = asm.GetType("BIMHealthCheck.Engine.HealthCheckEngine");
                if (healthCheckType != null)
                {
                    object engine = Activator.CreateInstance(healthCheckType);
                    MethodInfo runMethod = healthCheckType.GetMethod("RunAllChecks");
                    object score = runMethod.Invoke(engine, new object[] { app.ActiveUIDocument.Document });
                    Type dialogType = asm.GetType("BIMHealthCheck.UI.HealthCheckDialog");
                    var dialog = (System.Windows.Window)Activator.CreateInstance(dialogType, new object[] { score });
                    dialog.ShowDialog();
                    return RunResult.Success("Successfully completed " + title);
                }

                // 2. Identify candidate IExternalCommand type
                Type cmdType = null;
                if (!string.IsNullOrWhiteSpace(commandClassName))
                {
                    var candidate = asm.GetType(commandClassName, false, true);
                    if (candidate != null && typeof(IExternalCommand).IsAssignableFrom(candidate) && !candidate.IsAbstract && !candidate.IsInterface)
                    {
                        cmdType = candidate;
                    }
                }

                if (cmdType == null)
                {
                    // Check companion .addin file if not explicitly specified (only match IExternalCommand, not IExternalApplication)
                    string[] addinFiles = Directory.GetFiles(dir, "*.addin");
                    foreach (var af in addinFiles)
                    {
                        try
                        {
                            string xml = File.ReadAllText(af);
                            var matches = Regex.Matches(xml, @"<FullClassName>\s*([a-zA-Z0-9_\.]+)\s*</FullClassName>");
                            foreach (Match match in matches)
                            {
                                string candidateClassName = match.Groups[1].Value.Trim();
                                var candidate = asm.GetType(candidateClassName, false, true);
                                if (candidate != null && typeof(IExternalCommand).IsAssignableFrom(candidate) && !candidate.IsAbstract && !candidate.IsInterface)
                                {
                                    cmdType = candidate;
                                    break;
                                }
                            }
                            if (cmdType != null) break;
                        }
                        catch { }
                    }
                }

                if (cmdType == null)
                {
                    try
                    {
                        var types = asm.GetTypes();
                        string baseName = Path.GetFileNameWithoutExtension(dllPath);

                        // Prefer an IExternalCommand whose name matches the DLL or title, or any IExternalCommand in the assembly
                        cmdType = types.FirstOrDefault(t =>
                            t != null && typeof(IExternalCommand).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface &&
                            (t.Name.IndexOf(baseName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                             t.Name.Equals("Command", StringComparison.OrdinalIgnoreCase)))
                            ?? types.FirstOrDefault(t =>
                                t != null && typeof(IExternalCommand).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);

                        if (cmdType == null)
                        {
                            cmdType = types.FirstOrDefault(t =>
                                t != null &&
                                (t.Name.Equals(baseName, StringComparison.OrdinalIgnoreCase) ||
                                 t.Name.Equals("Command", StringComparison.OrdinalIgnoreCase) ||
                                 t.Name.EndsWith("Command", StringComparison.OrdinalIgnoreCase)) &&
                                !typeof(IExternalApplication).IsAssignableFrom(t) &&
                                !t.IsAbstract && !t.IsInterface);
                        }
                    }
                    catch (ReflectionTypeLoadException rtle)
                    {
                        cmdType = rtle.Types.FirstOrDefault(t =>
                            t != null && typeof(IExternalCommand).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                    }
                }

                // If no IExternalCommand was found in this DLL (or if it is purely an IExternalApplication wrapper),
                // check sibling DLLs in the same folder or invoke its registered Ribbon command!
                if (cmdType == null)
                {
                    foreach (var siblingDll in Directory.GetFiles(dir, "*.dll"))
                    {
                        if (string.Equals(Path.GetFullPath(siblingDll), Path.GetFullPath(dllPath), StringComparison.OrdinalIgnoreCase))
                            continue;
                        string sName = Path.GetFileName(siblingDll);
                        if (sName.StartsWith("RevitAPI", StringComparison.OrdinalIgnoreCase) ||
                            sName.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                            sName.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                            sName.StartsWith("Newtonsoft.", StringComparison.OrdinalIgnoreCase))
                            continue;
                        try
                        {
                            Assembly sibAsm = LoadAssemblySafely(siblingDll);
                            var sibTypes = sibAsm?.GetTypes();
                            if (sibTypes != null)
                            {
                                cmdType = sibTypes.FirstOrDefault(t =>
                                    t != null && typeof(IExternalCommand).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
                                if (cmdType != null)
                                {
                                    asm = sibAsm;
                                    break;
                                }
                            }
                        }
                        catch { }
                    }
                }

                if (cmdType == null)
                {
                    string ribbonId = FindRibbonCommandId(Path.GetFileNameWithoutExtension(dllPath), title);
                    if (!string.IsNullOrEmpty(ribbonId))
                    {
                        return RunRibbonCommand(app, ribbonId, title);
                    }

                    return RunResult.Fail("Could not locate IExternalCommand entry point in " + Path.GetFileName(dllPath) +
                        ".\nEnsure the C# class implements Autodesk.Revit.UI.IExternalCommand.");
                }

                // 3. Execute IExternalCommand
                object cmdInstance = Activator.CreateInstance(cmdType);
                var commandData = CreateExternalCommandData(app);
                string message = string.Empty;
                var elements = new Autodesk.Revit.DB.ElementSet();

                MethodInfo executeMethod = cmdType.GetMethod("Execute", new[] { typeof(ExternalCommandData), typeof(string).MakeByRefType(), typeof(Autodesk.Revit.DB.ElementSet) });
                if (executeMethod != null)
                {
                    object[] invokeArgs = new object[] { commandData, message, elements };
                    object retObj = executeMethod.Invoke(cmdInstance, invokeArgs);

                    if (retObj is Autodesk.Revit.UI.Result result)
                    {
                        if (result == Autodesk.Revit.UI.Result.Succeeded)
                        {
                            return RunResult.Success($"Successfully executed {title}.");
                        }
                        else if (result == Autodesk.Revit.UI.Result.Cancelled)
                        {
                            return RunResult.Success($"Cancelled {title}.");
                        }
                        else
                        {
                            string returnedMsg = invokeArgs[1] as string;
                            return RunResult.Fail(!string.IsNullOrWhiteSpace(returnedMsg) ? returnedMsg : $"{title} execution returned Failed.");
                        }
                    }

                    return RunResult.Success($"Executed {title}.");
                }

                // Fallback for custom Execute(UIApplication) or Execute(Document) methods
                MethodInfo appMethod = cmdType.GetMethod("Execute", new[] { typeof(UIApplication) }) ?? cmdType.GetMethod("Run", new[] { typeof(UIApplication) });
                if (appMethod != null)
                {
                    appMethod.Invoke(cmdInstance, new object[] { app });
                    return RunResult.Success($"Successfully executed {title}.");
                }

                return RunResult.Fail("No compatible Execute method found on " + cmdType.FullName);
            }
            catch (Exception ex)
            {
                Exception inner = ex.InnerException ?? ex;
                Log.Write("RunAssemblyCommand error: " + inner);

                // Fallback: if reflection execution failed because the add-in is already loaded in Revit's Ribbon, trigger its Ribbon Command directly
                string ribbonId = FindRibbonCommandId(Path.GetFileNameWithoutExtension(dllPath), title);
                if (!string.IsNullOrEmpty(ribbonId))
                {
                    var ribRes = RunRibbonCommand(app, ribbonId, title);
                    if (ribRes != null && ribRes.Ok) return ribRes;
                }

                return RunResult.Fail("Error executing " + title + ": " + inner.Message);
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
        public static RunResult Run(UIApplication app, string dynPath, bool headless, bool enableNativeUi = false)
        {
            Assembly dynamoRevit = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "DynamoRevitDS" || a.GetName().Name == "DynamoRevit");

            if (dynamoRevit == null)
            {
                try
                {
                    string revitDir = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);
                    string version = app.Application.VersionNumber;
                    string[] candidates = new[]
                    {
                        Path.Combine(revitDir, "AddIns", "DynamoForRevit", "Revit", "DynamoRevitDS.dll"),
                        Path.Combine(revitDir, "AddIns", "DynamoForRevit", "DynamoRevitDS.dll"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", "Revit " + version, "AddIns", "DynamoForRevit", "Revit", "DynamoRevitDS.dll"),
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Autodesk", "Revit", "Addins", version, "DynamoForRevit", "DynamoRevitDS.dll")
                    };
                    foreach (var candidate in candidates)
                    {
                        if (File.Exists(candidate))
                        {
                            dynamoRevit = Assembly.LoadFrom(candidate);
                            break;
                        }
                    }
                }
                catch { }
            }

            if (dynamoRevit != null)
            {
                Type dynType = dynamoRevit.GetType("Dynamo.Applications.DynamoRevit");
                Type dataType = dynamoRevit.GetType("Dynamo.Applications.DynamoRevitCommandData");
                if (dynType != null && dataType != null)
                {
                    var journal = new Dictionary<string, string>
                    {
                        ["dynPath"] = dynPath,
                        ["dynShowUI"] = "false",
                        ["dynAutomation"] = enableNativeUi ? "false" : (headless ? "true" : "false"),
                        ["dynPathExecute"] = "true",
                        ["dynModelShutDown"] = "false",
                        ["dynForceManualRun"] = "true"
                    };

                    object data = Activator.CreateInstance(dataType);
                    dataType.GetProperty("Application")?.SetValue(data, app);
                    dataType.GetProperty("JournalData")?.SetValue(data, journal);

                    object dynamo = Activator.CreateInstance(dynType);
                    MethodInfo execute = dynType.GetMethod("ExecuteCommand", new[] { dataType });
                    if (execute != null)
                    {
                        try
                        {
                            object result = execute.Invoke(dynamo, new[] { data });
                            bool ok = result is Result r ? r == Result.Succeeded : true;

                            string name = Path.GetFileNameWithoutExtension(dynPath);
                            if (!ok) return RunResult.Fail("Dynamo reported a failure running " + name + ".");
                            return RunResult.Success(enableNativeUi ? name + " executed with Data-Shapes form." : (headless ? name + " ran successfully." : name + " opened in Dynamo — verify parameters and click Run."));
                        }
                        catch (Exception ex)
                        {
                            Exception inner = ex.InnerException ?? ex;
                            Log.Write("Dynamo ExecuteCommand exception: " + inner);
                            return RunResult.Fail("Dynamo execution error: " + inner.Message);
                        }
                    }
                }
            }

            // Fallback 3: Post the standard Dynamo command or open file via shell
            var dynamoCmd = RevitCommandId.LookupCommandId("ID_VISUAL_PROGRAMMING_DYNAMO");
            if (dynamoCmd != null && app.CanPostCommand(dynamoCmd))
            {
                app.PostCommand(dynamoCmd);
                return RunResult.Success($"Dynamo opened. Please open \"{Path.GetFileName(dynPath)}\".");
            }

            // Fallback 4: Launch using Windows shell association
            try
            {
                Process.Start(new ProcessStartInfo(dynPath) { UseShellExecute = true });
                return RunResult.Success("Opened " + Path.GetFileName(dynPath) + " in Dynamo.");
            }
            catch (Exception ex)
            {
                return RunResult.Fail("Could not launch Dynamo: " + ex.Message);
            }
        }
    }
}
