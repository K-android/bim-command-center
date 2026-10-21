using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMCommandCenter.Core.Infrastructure;
using IronPython.Hosting;
using Microsoft.Scripting.Hosting;

namespace BIMCommandCenter.Core.Revit
{
    /// <summary>
    /// Embedded zero-install Python execution engine.
    /// Allows all team members to run pyRevit pushbuttons and loose .py scripts
    /// directly from central storage without requiring pyRevit to be installed on their workstations.
    /// Also leverages installed pyRevit libraries if available.
    /// </summary>
    public static class PythonRunner
    {
        private static bool _resolverRegistered = false;
        private static readonly HashSet<string> RegisteredProbingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static RunResult Run(UIApplication app, string scriptPath, string title)
        {
            if (string.IsNullOrWhiteSpace(scriptPath))
                return RunResult.Fail("Python script path was empty.");

            // If scriptPath points to a pushbutton directory, resolve script.py inside
            if (Directory.Exists(scriptPath))
            {
                string candidate = Path.Combine(scriptPath, "script.py");
                if (File.Exists(candidate)) scriptPath = candidate;
            }

            if (!File.Exists(scriptPath))
            {
                return RunResult.Fail(
                    $"Python script not found: \"{title}\".\n\n" +
                    $"Path: {scriptPath}\n\n" +
                    "Verify that script.py exists inside the pushbutton bundle or central library."
                );
            }

            var outputBuffer = new MemoryStream();
            var outputWriter = new StreamWriter(outputBuffer, Encoding.UTF8);

            try
            {
                // Create standalone IronPython engine (Zero-install, works on any machine!)
                var engine = Python.CreateEngine();

                // Redirect stdout & stderr so script output and print statements are captured
                engine.Runtime.IO.SetOutput(outputBuffer, outputWriter);
                engine.Runtime.IO.SetErrorOutput(outputBuffer, outputWriter);

                // Configure search paths (sys.path)
                string scriptDir = Path.GetDirectoryName(scriptPath);
                var searchPaths = new List<string> { scriptDir };

                // 1. Script's own and ancestor lib / bin / packages subfolders (walks up to ScriptsRoot)
                var config = AppConfig.Load();
                DirectoryInfo currentDir = new DirectoryInfo(scriptDir);
                while (currentDir != null)
                {
                    foreach (string folderName in new[] { "lib", "libs", "bin", "packages", "deps" })
                    {
                        string candidate = Path.Combine(currentDir.FullName, folderName);
                        if (Directory.Exists(candidate) && !searchPaths.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                        {
                            searchPaths.Add(candidate);
                        }
                    }

                    if (!string.IsNullOrEmpty(config.ScriptsRoot) &&
                        string.Equals(currentDir.FullName.TrimEnd('\\', '/'), config.ScriptsRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
                    {
                        break;
                    }
                    currentDir = currentDir.Parent;
                }

                // 2. Central shared lib folders if present in Central Scripts Root
                if (!string.IsNullOrEmpty(config.ScriptsRoot))
                {
                    string centralLib = Path.Combine(config.ScriptsRoot, "lib");
                    if (Directory.Exists(centralLib) && !searchPaths.Contains(centralLib, StringComparer.OrdinalIgnoreCase))
                        searchPaths.Add(centralLib);

                    string centralPyLib = Path.Combine(config.ScriptsRoot, "PyRevit", "lib");
                    if (Directory.Exists(centralPyLib) && !searchPaths.Contains(centralPyLib, StringComparer.OrdinalIgnoreCase))
                        searchPaths.Add(centralPyLib);
                }

                // 3. PyRevit system libraries if pyRevit is installed on this PC
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

                string[] pyrevitDirs = new[]
                {
                    Path.Combine(appData, "pyRevit-Master", "pyrevitlib"),
                    Path.Combine(appData, "pyRevit-Master", "site-packages"),
                    Path.Combine(appData, "pyRevit", "pyrevitlib"),
                    Path.Combine(appData, "pyRevit", "site-packages"),
                    Path.Combine(localAppData, "pyRevit-Master", "pyrevitlib"),
                    Path.Combine(localAppData, "pyRevit", "pyrevitlib"),
                    Path.Combine(commonAppData, "pyRevit", "pyrevitlib"),
                    Path.Combine(commonAppData, "pyRevit", "site-packages")
                };
                foreach (var pDir in pyrevitDirs)
                {
                    if (Directory.Exists(pDir) && !searchPaths.Contains(pDir, StringComparer.OrdinalIgnoreCase))
                    {
                        searchPaths.Add(pDir);
                    }
                }

                // 4. Bundled compatibility shim folder (provides pyrevit, DB, UI, forms, script, rpw shims)
                string corePyLib = Path.Combine(AppConfig.CoreDir, "pylib");
                if (Directory.Exists(corePyLib) && !searchPaths.Contains(corePyLib, StringComparer.OrdinalIgnoreCase))
                {
                    searchPaths.Add(corePyLib);
                }

                // 5. DynamoForRevit paths so scripts using RevitServices can resolve
                string revitYear = app.Application.VersionNumber;
                string dynamoRevitDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Autodesk", "Revit " + revitYear, "AddIns", "DynamoForRevit", "Revit");
                if (Directory.Exists(dynamoRevitDir) && !searchPaths.Contains(dynamoRevitDir, StringComparer.OrdinalIgnoreCase))
                {
                    searchPaths.Add(dynamoRevitDir);
                }

                engine.SetSearchPaths(searchPaths);
                RegisterProbingPaths(searchPaths);

                // Load essential Revit assemblies into the Python engine
                engine.Runtime.LoadAssembly(typeof(UIApplication).Assembly); // RevitAPIUI
                engine.Runtime.LoadAssembly(typeof(Document).Assembly);      // RevitAPI

                var adWindows = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "AdWindows");
                if (adWindows != null) engine.Runtime.LoadAssembly(adWindows);

                // Register global Revit handles into builtins (accessible everywhere including pyrevit and lib modules)
                Document activeDoc = app.ActiveUIDocument?.Document;
                UIDocument activeUiDoc = app.ActiveUIDocument;

                var builtinModule = engine.GetBuiltinModule();
                builtinModule.SetVariable("__revit__", app);
                builtinModule.SetVariable("__uiapp__", app);
                builtinModule.SetVariable("__doc__", activeDoc);
                builtinModule.SetVariable("__uidoc__", activeUiDoc);
                builtinModule.SetVariable("doc", activeDoc);
                builtinModule.SetVariable("uidoc", activeUiDoc);
                builtinModule.SetVariable("__file__", scriptPath);
                builtinModule.SetVariable("__commandpath__", scriptPath);
                builtinModule.SetVariable("__shiftclick__", false);
                builtinModule.SetVariable("__forcedexec__", false);
                builtinModule.SetVariable("__execid__", Guid.NewGuid().ToString("N"));
                builtinModule.SetVariable("__timestamp__", DateTime.UtcNow.ToString("o"));
                builtinModule.SetVariable("__cachedengineid__", "default");
                builtinModule.SetVariable("__cachedengine__", false);
                builtinModule.SetVariable("EXEC_PARAMS", new Dictionary<string, object>());

                // Setup Dynamo Python compatibility builtins (IN, OUT, UnwrapElement)
                var inList = new List<object> { activeUiDoc?.Selection?.GetElementIds()?.FirstOrDefault() != null ? activeDoc?.GetElement(activeUiDoc.Selection.GetElementIds().First()) : null };
                for (int i = 1; i < 20; i++) inList.Add(null);
                builtinModule.SetVariable("IN", inList);
                builtinModule.SetVariable("OUT", null);

                // Setup script scope with standard pyRevit & Revit globals
                var scope = engine.CreateScope();
                scope.SetVariable("__revit__", app);
                scope.SetVariable("__uiapp__", app);
                scope.SetVariable("__doc__", activeDoc);
                scope.SetVariable("__uidoc__", activeUiDoc);
                scope.SetVariable("doc", activeDoc);
                scope.SetVariable("uidoc", activeUiDoc);
                scope.SetVariable("__file__", scriptPath);
                scope.SetVariable("__commandpath__", scriptPath);
                scope.SetVariable("__shiftclick__", false);
                scope.SetVariable("__forcedexec__", false);
                scope.SetVariable("EXEC_PARAMS", new Dictionary<string, object>());
                scope.SetVariable("IN", inList);
                scope.SetVariable("OUT", null);

                // Execute script
                var source = engine.CreateScriptSourceFromFile(scriptPath, Encoding.UTF8);
                source.Execute(scope);

                outputWriter.Flush();
                string outputText = Encoding.UTF8.GetString(outputBuffer.ToArray()).Trim();

                // If OUT variable was populated by a Dynamo-style script, prefer it if outputText is empty
                if (string.IsNullOrWhiteSpace(outputText) && scope.TryGetVariable("OUT", out object outVal) && outVal != null)
                {
                    outputText = outVal.ToString();
                }

                string message = !string.IsNullOrWhiteSpace(outputText)
                    ? outputText
                    : $"Successfully executed {title} in active Revit session.";

                var res = RunResult.Success(message);
                if (!string.IsNullOrWhiteSpace(outputText))
                {
                    res.Outputs.Add(outputText);
                }

                return res;
            }
            catch (Exception ex)
            {
                outputWriter.Flush();
                string outputText = Encoding.UTF8.GetString(outputBuffer.ToArray()).Trim();

                string msg = ex.Message;
                if (!string.IsNullOrWhiteSpace(outputText))
                {
                    msg = outputText + "\n\n" + msg;
                }

                Log.Write("Python execution exception: " + ex);
                return RunResult.Fail($"Error executing {title}:\n{msg}");
            }
            finally
            {
                outputWriter.Dispose();
                outputBuffer.Dispose();
            }
        }

        private static void RegisterProbingPaths(IEnumerable<string> paths)
        {
            lock (RegisteredProbingPaths)
            {
                foreach (var p in paths)
                {
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                        RegisteredProbingPaths.Add(p);
                }

                if (!_resolverRegistered)
                {
                    _resolverRegistered = true;
                    AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
                    {
                        try
                        {
                            string asmName = new AssemblyName(args.Name).Name;
                            if (string.IsNullOrEmpty(asmName)) return null;

                            lock (RegisteredProbingPaths)
                            {
                                foreach (var probeDir in RegisteredProbingPaths)
                                {
                                    string candidate = Path.Combine(probeDir, asmName + ".dll");
                                    if (File.Exists(candidate))
                                    {
                                        return Assembly.LoadFrom(candidate);
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
    }
}
