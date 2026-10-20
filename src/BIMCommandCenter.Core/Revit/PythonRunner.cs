using System;
using System.Collections.Generic;
using System.IO;
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
    /// </summary>
    public static class PythonRunner
    {
        public static RunResult Run(UIApplication app, string scriptPath, string title)
        {
            if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
                return RunResult.Fail("Python script file not found:\n" + scriptPath);

            var outputBuffer = new MemoryStream();
            var outputWriter = new StreamWriter(outputBuffer, Encoding.UTF8);

            try
            {
                // Create standalone IronPython engine (Zero-install, no pyRevit required on machine!)
                var engine = Python.CreateEngine();

                // Redirect stdout & stderr so script output and print statements are captured
                engine.Runtime.IO.SetOutput(outputBuffer, outputWriter);
                engine.Runtime.IO.SetErrorOutput(outputBuffer, outputWriter);

                // Configure search paths (sys.path)
                string scriptDir = Path.GetDirectoryName(scriptPath);
                var searchPaths = new List<string> { scriptDir };

                // 1. Script's own lib / bin subfolders
                string localLib = Path.Combine(scriptDir, "lib");
                if (Directory.Exists(localLib)) searchPaths.Add(localLib);
                string localBin = Path.Combine(scriptDir, "bin");
                if (Directory.Exists(localBin)) searchPaths.Add(localBin);

                // 2. Central shared lib folders if present in Central Scripts Root
                var config = AppConfig.Load();
                if (!string.IsNullOrEmpty(config.ScriptsRoot))
                {
                    string centralLib = Path.Combine(config.ScriptsRoot, "lib");
                    if (Directory.Exists(centralLib)) searchPaths.Add(centralLib);
                    string centralPyLib = Path.Combine(config.ScriptsRoot, "PyRevit", "lib");
                    if (Directory.Exists(centralPyLib)) searchPaths.Add(centralPyLib);
                }

                // 3. Bundled compatibility shim folder (provides pyrevit, DB, UI, forms, script shims)
                string corePyLib = Path.Combine(AppConfig.CoreDir, "pylib");
                if (Directory.Exists(corePyLib)) searchPaths.Add(corePyLib);

                // 4. PyRevit system libraries if pyRevit happens to be installed on this PC
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string[] pyrevitDirs = new[]
                {
                    Path.Combine(appData, "pyRevit-Master", "pyrevitlib"),
                    Path.Combine(appData, "pyRevit", "pyrevitlib"),
                    Path.Combine(appData, "pyRevit-Master", "site-packages"),
                    Path.Combine(appData, "pyRevit", "site-packages"),
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "pyRevit", "pyrevitlib")
                };
                foreach (var pDir in pyrevitDirs)
                {
                    if (Directory.Exists(pDir) && !searchPaths.Contains(pDir)) searchPaths.Add(pDir);
                }

                engine.SetSearchPaths(searchPaths);

                // Load essential Revit assemblies into the Python engine
                engine.Runtime.LoadAssembly(typeof(UIApplication).Assembly); // RevitAPIUI
                engine.Runtime.LoadAssembly(typeof(Document).Assembly);      // RevitAPI

                // Setup scope with standard pyRevit & Revit globals
                var scope = engine.CreateScope();
                scope.SetVariable("__revit__", app);
                scope.SetVariable("__uiapp__", app);
                scope.SetVariable("__doc__", app.ActiveUIDocument?.Document);
                scope.SetVariable("__uidoc__", app.ActiveUIDocument);
                scope.SetVariable("__file__", scriptPath);
                scope.SetVariable("__commandpath__", scriptPath);
                scope.SetVariable("__shiftclick__", false);
                scope.SetVariable("__forcedexec__", false);
                scope.SetVariable("EXEC_PARAMS", new string[0]);

                // Execute script
                var source = engine.CreateScriptSourceFromFile(scriptPath, Encoding.UTF8);
                source.Execute(scope);

                outputWriter.Flush();
                string outputText = Encoding.UTF8.GetString(outputBuffer.ToArray()).Trim();

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
    }
}
