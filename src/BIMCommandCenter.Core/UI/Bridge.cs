using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using BIMCommandCenter.Core.Catalog;
using BIMCommandCenter.Core.Ideas;
using BIMCommandCenter.Core.Infrastructure;
using BIMCommandCenter.Core.Revit;
using Microsoft.Web.WebView2.Core;

namespace BIMCommandCenter.Core.UI
{
    /// <summary>
    /// Request/response protocol between the web UI and Revit.
    ///   JS  -> C#  : { id, action, payload }
    ///   C#  -> JS  : { replyTo: id, ok: true, data } | { replyTo: id, ok: false, error }
    /// The UI never sends file paths to execute: it sends a toolId, and C# looks the tool up
    /// in the catalog it loaded itself. That keeps the web page from running arbitrary files.
    /// </summary>
    public class Bridge
    {
        private readonly CoreWebView2 _web;
        private readonly Services _s;

        public Bridge(CoreWebView2 web, Services services)
        {
            _web = web;
            _s = services;
        }

        public async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string id = null;
            try
            {
                string raw;
                try { raw = e.TryGetWebMessageAsString(); }
                catch { raw = e.WebMessageAsJson; }

                Dictionary<string, object> msg = Json.AsObject(Json.Parse(raw)) ?? throw new ArgumentException("Bad message");
                id = msg.Str("id");
                string action = msg.Str("action");
                Dictionary<string, object> payload =
                    (msg.TryGetValue("payload", out object p) ? Json.AsObject(p) : null)
                    ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                object data = await HandleAsync(action, payload);
                Reply(id, true, data, null);
            }
            catch (Exception ex)
            {
                Log.Write("Bridge error: " + ex);
                Reply(id, false, null, ex.InnerException?.Message ?? ex.Message);
            }
        }

        private async Task<object> HandleAsync(string action, Dictionary<string, object> payload)
        {
            switch (action)
            {
                case "getContext":
                    Dictionary<string, object> ctx = await _s.Dispatcher.InvokeAsync(RevitContext.Describe);
                    ctx["configSource"] = _s.Config.LoadedFrom;
                    ctx["catalogSource"] = _s.Config.CatalogSource;
                    ctx["aiEndpointConfigured"] = !string.IsNullOrWhiteSpace(_s.Config.AiEndpoint);
                    return ctx;

                case "getCatalog":
                    CatalogResult catalog = await _s.Catalog.GetAsync(payload.Bool("refresh"));
                    return new Dictionary<string, object>
                    {
                        ["tools"] = catalog.Tools,
                        ["source"] = catalog.Source,
                        ["fromCache"] = catalog.FromCache,
                        ["warning"] = catalog.Warning ?? "",
                        ["loadedAt"] = catalog.LoadedAt.ToString("s")
                    };

                case "executeTool":
                {
                    string toolId = payload.Str("toolId");
                    if (_s.Catalog.Current == null) await _s.Catalog.GetAsync(false);
                    Dictionary<string, object> tool = _s.Catalog.FindTool(toolId)
                        ?? throw new InvalidOperationException("Tool \"" + toolId + "\" is not in the catalog.");
                    Log.Write("Execute " + toolId + " by " + Environment.UserName);

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    RunResult result = await _s.Dispatcher.InvokeAsync(app => _s.Runner.Run(app, tool));
                    sw.Stop();
                    result.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);

                    // Record to audit trail
                    RecordRunAudit(tool, result, payload);

                    return result.ToDictionary();
                }

                case "getRunHistory":
                {
                    return GetRunHistory();
                }

                case "reportIssue":
                {
                    return RecordIssue(payload);
                }

                case "captureViewport":
                    return await _s.Dispatcher.InvokeAsync(RevitContext.CaptureActiveView);

                case "submitIdea":
                {
                    Dictionary<string, object> context = await _s.Dispatcher.InvokeAsync(RevitContext.Describe);
                    return await _s.Ideas.SubmitAsync(payload, context);
                }

                case "openUrl":
                {
                    string url = payload.Str("url");
                    if (url == null || !(url.StartsWith("https://") || url.StartsWith("http://")))
                        throw new ArgumentException("Only http(s) links can be opened.");
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return true;
                }

                case "openFolder":
                {
                    string path = payload.Str("path");
                    if (string.IsNullOrWhiteSpace(path))
                        path = _s.Config.ScriptsRoot;
                    if (string.IsNullOrWhiteSpace(path))
                        path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BIM_Scripts");

                    path = Environment.ExpandEnvironmentVariables(path);
                    System.IO.Directory.CreateDirectory(path);
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                    return new Dictionary<string, object> { ["path"] = path };
                }

                case "getConfig":
                {
                    return new Dictionary<string, object>
                    {
                        ["catalogSource"] = _s.Config.CatalogSource,
                        ["catalogSheet"] = _s.Config.CatalogSheet,
                        ["scriptsRoot"] = _s.Config.ScriptsRoot,
                        ["telemetryFolder"] = _s.Config.TelemetryFolder,
                        ["knowledgeGraphPath"] = _s.Config.KnowledgeGraphPath,
                        ["ideasEndpoint"] = _s.Config.IdeasEndpoint,
                        ["ideasFolder"] = _s.Config.IdeasFolder,
                        ["configPath"] = _s.Config.LoadedFrom
                    };
                }

                case "getFirmUsage":
                {
                    return GetFirmUsageStats();
                }

                case "getKnowledgeGraph":
                {
                    return await GenerateKnowledgeGraphAsync();
                }

                case "saveConfig":
                {
                    string userConfigPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMCommandCenter", "config.json");
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(userConfigPath));

                    var current = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    if (System.IO.File.Exists(userConfigPath))
                    {
                        try { current = Json.AsObject(Json.Parse(System.IO.File.ReadAllText(userConfigPath))) ?? current; } catch {}
                    }

                    foreach (var kv in payload)
                    {
                        current[kv.Key] = kv.Value;
                    }

                    System.IO.File.WriteAllText(userConfigPath, Json.Stringify(current));
                    _s.Config = AppConfig.Load();
                    _s.Catalog = new CatalogService(_s.Config, _s.RevitYear ?? "2026");
                    _s.Runner = new ToolRunner(_s.Config);

                    return new Dictionary<string, object> { ["saved"] = true, ["configPath"] = userConfigPath };
                }

                case "saveToolDescription":
                {
                    string targetPath = payload.Str("targetPath");
                    string desc = (payload.Str("description") ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(targetPath))
                        throw new ArgumentException("targetPath is required to save description.");

                    string resolved = targetPath;
                    string dir = System.IO.Path.GetDirectoryName(resolved);
                    if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);

                    string baseName = System.IO.Path.GetFileNameWithoutExtension(resolved);
                    string sidecarPath = System.IO.Path.Combine(dir, baseName + ".desc.txt");

                    System.IO.File.WriteAllText(sidecarPath, desc);

                    // Re-instantiate catalog so in-memory cache reflects changes immediately
                    _s.Catalog = new CatalogService(_s.Config, _s.RevitYear ?? "2026");

                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["saved"] = true,
                        ["path"] = sidecarPath,
                        ["description"] = desc
                    };
                }

                case "getRibbonTools":
                {
                    var pinned = UserRibbonManager.LoadPinnedTools();
                    return new Dictionary<string, object>
                    {
                        ["tools"] = pinned,
                        ["count"] = pinned.Count
                    };
                }

                case "updateRibbonTools":
                {
                    var list = payload.TryGetValue("tools", out object tList) ? Json.AsList(tList) : null;
                    if (list == null && payload.TryGetValue("toolIds", out object idList))
                    {
                        var ids = Json.AsList(idList)?.Select(x => x?.ToString()).ToList() ?? new List<string>();
                        var currentCat = await _s.Catalog.GetAsync(false);
                        list = ids.Select(id => (object)(currentCat.Tools.FirstOrDefault(t => t.Str("toolId") == id) ?? new Dictionary<string, object> { ["toolId"] = id, ["title"] = id })).ToList();
                    }

                    var toolDicts = (list ?? new List<object>()).OfType<Dictionary<string, object>>().ToList();
                    UserRibbonManager.SavePinnedTools(toolDicts);

                    return new Dictionary<string, object>
                    {
                        ["saved"] = true,
                        ["count"] = toolDicts.Count,
                        ["message"] = "Revit Ribbon updated. Pinned tools are saved to 'My Quick Tools' panel."
                    };
                }

                case "registerTool":
                {
                    string srcFile = payload.Str("filePath");
                    string title = payload.Str("title");
                    string cat = payload.Str("category", "Dynamo");
                    string desc = (payload.Str("description") ?? "").Trim();
                    bool makeShortcut = payload.Bool("createShortcut", true);

                    if (string.IsNullOrWhiteSpace(srcFile) || !File.Exists(srcFile))
                        throw new FileNotFoundException("Source tool file not found: " + srcFile);

                    string root = Environment.ExpandEnvironmentVariables(_s.Config.ScriptsRoot);
                    if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                    {
                        root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMCommandCenter", "scripts");
                    }

                    string targetFolder = Path.Combine(root, cat);
                    Directory.CreateDirectory(targetFolder);

                    string srcExt = Path.GetExtension(srcFile);
                    string cleanName = string.IsNullOrWhiteSpace(title) 
                        ? Path.GetFileNameWithoutExtension(srcFile)
                        : System.Text.RegularExpressions.Regex.Replace(title, @"[\\/:*?""<>|]", "_");

                    string destFile;
                    if (makeShortcut)
                    {
                        destFile = Path.Combine(targetFolder, cleanName + ".lnk");
                        try
                        {
                            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                            if (shellType != null)
                            {
                                object shell = Activator.CreateInstance(shellType);
                                MethodInfo createShortcut = shellType.GetMethod("CreateShortcut");
                                if (createShortcut != null)
                                {
                                    object shortcut = createShortcut.Invoke(shell, new object[] { destFile });
                                    shortcut.GetType().GetProperty("TargetPath")?.SetValue(shortcut, srcFile, null);
                                    shortcut.GetType().GetProperty("Description")?.SetValue(shortcut, desc, null);
                                    shortcut.GetType().GetMethod("Save")?.Invoke(shortcut, null);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Write("CreateShortcut error: " + ex.Message);
                            destFile = Path.Combine(targetFolder, cleanName + srcExt);
                            File.Copy(srcFile, destFile, true);
                        }
                    }
                    else
                    {
                        destFile = Path.Combine(targetFolder, cleanName + srcExt);
                        File.Copy(srcFile, destFile, true);
                    }

                    // Save companion description
                    string descFile = Path.Combine(targetFolder, cleanName + ".desc.txt");
                    if (!string.IsNullOrEmpty(desc))
                    {
                        File.WriteAllText(descFile, desc);
                    }

                    // Re-instantiate catalog
                    _s.Catalog = new CatalogService(_s.Config, _s.RevitYear ?? "2026");

                    return new Dictionary<string, object>
                    {
                        ["ok"] = true,
                        ["registered"] = true,
                        ["path"] = destFile,
                        ["title"] = cleanName
                    };
                }

                default:
                    throw new NotSupportedException("Unknown action: " + action);
            }
        }

        private static string AuditHistoryPath =>
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMCommandCenter", "audit", "run_history.json");

        private void RecordRunAudit(Dictionary<string, object> tool, RunResult result, Dictionary<string, object> payload)
        {
            try
            {
                var entry = new Dictionary<string, object>
                {
                    ["id"] = result.ExecutionId,
                    ["toolId"] = tool.Str("toolId"),
                    ["toolTitle"] = tool.Str("title"),
                    ["category"] = tool.Str("category"),
                    ["actionType"] = tool.Str("actionType"),
                    ["status"] = result.Status,
                    ["ok"] = result.Ok,
                    ["message"] = result.Message,
                    ["changedItems"] = result.ChangedItems,
                    ["outputs"] = result.Outputs,
                    ["warnings"] = result.Warnings,
                    ["errors"] = result.Errors,
                    ["user"] = Environment.UserName,
                    ["durationSeconds"] = result.DurationSeconds,
                    ["timestamp"] = result.ExecutedAt.ToString("o")
                };

                // 1. Local AppData Audit Store
                string path = AuditHistoryPath;
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));

                var history = new List<object>();
                if (System.IO.File.Exists(path))
                {
                    try { history = Json.AsList(Json.Parse(System.IO.File.ReadAllText(path))) ?? history; } catch {}
                }

                history.Insert(0, entry);
                if (history.Count > 250) history = history.Take(250).ToList();
                System.IO.File.WriteAllText(path, Json.Stringify(history));

                // 2. Central Shared Enterprise Telemetry (if configured via settings or scripts folder default)
                string centralFolder = !string.IsNullOrWhiteSpace(_s.Config.TelemetryFolder)
                    ? _s.Config.TelemetryFolder
                    : (!string.IsNullOrWhiteSpace(_s.Config.ScriptsRoot) ? System.IO.Path.Combine(_s.Config.ScriptsRoot, "telemetry") : null);

                if (!string.IsNullOrWhiteSpace(centralFolder))
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(centralFolder);
                        string centralFile = System.IO.Path.Combine(centralFolder, "firm_runs.jsonl");
                        string jsonLine = Json.Stringify(entry).Replace("\r", "").Replace("\n", "") + Environment.NewLine;
                        System.IO.File.AppendAllText(centralFile, jsonLine);
                    }
                    catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Write("RecordRunAudit error: " + ex.Message);
            }
        }

        private Dictionary<string, object> GetFirmUsageStats()
        {
            try
            {
                string centralFolder = !string.IsNullOrWhiteSpace(_s.Config.TelemetryFolder)
                    ? _s.Config.TelemetryFolder
                    : (!string.IsNullOrWhiteSpace(_s.Config.ScriptsRoot) ? System.IO.Path.Combine(_s.Config.ScriptsRoot, "telemetry") : null);

                var runList = new List<Dictionary<string, object>>();

                // Try reading central corporate runs
                if (!string.IsNullOrWhiteSpace(centralFolder))
                {
                    string centralFile = System.IO.Path.Combine(centralFolder, "firm_runs.jsonl");
                    if (System.IO.File.Exists(centralFile))
                    {
                        var lines = System.IO.File.ReadAllLines(centralFile);
                        foreach (var line in lines)
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;
                            try
                            {
                                var parsed = Json.AsObject(Json.Parse(line));
                                if (parsed != null) runList.Add(parsed);
                            }
                            catch { }
                        }
                    }
                }

                // Fallback to local history if central has no records yet
                if (runList.Count == 0)
                {
                    var local = GetRunHistory();
                    runList = local.OfType<Dictionary<string, object>>().ToList();
                }

                // Aggregate statistics
                var toolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var toolNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var toolUsers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                var userCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                int totalRuns = runList.Count;
                int successRuns = 0;

                foreach (var r in runList)
                {
                    string tid = r.Str("toolId") ?? "unknown";
                    string tTitle = r.Str("toolTitle") ?? tid;
                    string user = r.Str("user") ?? "unknown";
                    bool ok = r.Bool("ok");

                    if (ok) successRuns++;

                    toolCounts[tid] = toolCounts.TryGetValue(tid, out int tc) ? tc + 1 : 1;
                    toolNames[tid] = tTitle;
                    
                    if (!toolUsers.ContainsKey(tid)) toolUsers[tid] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    toolUsers[tid].Add(user);

                    userCounts[user] = userCounts.TryGetValue(user, out int uc) ? uc + 1 : 1;
                }

                var leaderboard = toolCounts
                    .OrderByDescending(kv => kv.Value)
                    .Take(15)
                    .Select(kv => new Dictionary<string, object>
                    {
                        ["toolId"] = kv.Key,
                        ["title"] = toolNames.TryGetValue(kv.Key, out var n) ? n : kv.Key,
                        ["runCount"] = kv.Value,
                        ["uniqueUsers"] = toolUsers.TryGetValue(kv.Key, out var us) ? us.Count : 0
                    })
                    .ToList();

                return new Dictionary<string, object>
                {
                    ["totalRuns"] = totalRuns,
                    ["successRuns"] = successRuns,
                    ["uniqueUsersTotal"] = userCounts.Keys.Count,
                    ["uniqueToolsUsed"] = toolCounts.Keys.Count,
                    ["leaderboard"] = leaderboard,
                    ["centralSourceActive"] = !string.IsNullOrWhiteSpace(centralFolder) && System.IO.Directory.Exists(centralFolder)
                };
            }
            catch (Exception ex)
            {
                Log.Write("GetFirmUsageStats error: " + ex.Message);
                return new Dictionary<string, object> { ["error"] = ex.Message };
            }
        }

        private async Task<Dictionary<string, object>> GenerateKnowledgeGraphAsync()
        {
            var catalog = await _s.Catalog.GetAsync(false);
            var nodes = new List<Dictionary<string, object>>();
            var links = new List<Dictionary<string, object>>();
            var nodeIds = new HashSet<string>();

            // Root Domain Nodes
            var categories = catalog.Tools.Select(t => t.Str("category")).Where(c => !string.IsNullOrEmpty(c)).Distinct().ToList();
            foreach (var cat in categories)
            {
                string catId = "cat:" + cat;
                if (nodeIds.Add(catId))
                {
                    nodes.Add(new Dictionary<string, object>
                    {
                        ["id"] = catId,
                        ["name"] = cat,
                        ["group"] = "Category",
                        ["val"] = 18,
                        ["color"] = "#2563eb"
                    });
                }
            }

            // Tool Nodes & Links to Category
            foreach (var t in catalog.Tools)
            {
                string tid = t.Str("toolId");
                string cat = t.Str("category");
                string plat = t.Str("actionType");
                string color = plat == "Grasshopper" ? "#0d9488" : (plat == "Dynamo" ? "#3b82f6" : (plat == "PyRevit" ? "#f59e0b" : "#8b5cf6"));

                if (nodeIds.Add(tid))
                {
                    nodes.Add(new Dictionary<string, object>
                    {
                        ["id"] = tid,
                        ["name"] = t.Str("title"),
                        ["group"] = plat,
                        ["val"] = 12,
                        ["color"] = color,
                        ["desc"] = t.Str("description"),
                        ["author"] = t.Str("author")
                    });
                }

                if (!string.IsNullOrEmpty(cat))
                {
                    links.Add(new Dictionary<string, object>
                    {
                        ["source"] = "cat:" + cat,
                        ["target"] = tid,
                        ["relationship"] = "CONTAINS"
                    });
                }
            }

            // Export to custom path if set
            if (!string.IsNullOrWhiteSpace(_s.Config.KnowledgeGraphPath))
            {
                try
                {
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_s.Config.KnowledgeGraphPath));
                    var exportData = new Dictionary<string, object> { ["nodes"] = nodes, ["links"] = links };
                    System.IO.File.WriteAllText(_s.Config.KnowledgeGraphPath, Json.Stringify(exportData));
                }
                catch { }
            }

            return new Dictionary<string, object>
            {
                ["nodes"] = nodes,
                ["links"] = links,
                ["nodeCount"] = nodes.Count,
                ["linkCount"] = links.Count
            };
        }

        private static List<object> GetRunHistory()
        {
            try
            {
                string path = AuditHistoryPath;
                if (System.IO.File.Exists(path))
                {
                    return Json.AsList(Json.Parse(System.IO.File.ReadAllText(path))) ?? new List<object>();
                }
            }
            catch (Exception ex)
            {
                Log.Write("GetRunHistory error: " + ex.Message);
            }
            return new List<object>();
        }

        private static Dictionary<string, object> RecordIssue(Dictionary<string, object> payload)
        {
            try
            {
                string issuePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMCommandCenter", "audit", "issues.json");
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(issuePath));

                var issues = new List<object>();
                if (System.IO.File.Exists(issuePath))
                {
                    try { issues = Json.AsList(Json.Parse(System.IO.File.ReadAllText(issuePath))) ?? issues; } catch {}
                }

                payload["id"] = Guid.NewGuid().ToString("N");
                payload["user"] = Environment.UserName;
                payload["timestamp"] = DateTime.Now.ToString("o");

                issues.Insert(0, payload);
                System.IO.File.WriteAllText(issuePath, Json.Stringify(issues));
                return new Dictionary<string, object> { ["ok"] = true, ["issueId"] = payload["id"] };
            }
            catch (Exception ex)
            {
                return new Dictionary<string, object> { ["ok"] = false, ["error"] = ex.Message };
            }
        }

        private void Reply(string id, bool ok, object data, string error)
        {
            if (id == null) return;
            var message = new Dictionary<string, object> { ["replyTo"] = id, ["ok"] = ok };
            if (ok) message["data"] = data; else message["error"] = error;
            try { _web.PostWebMessageAsJson(Json.Stringify(message)); }
            catch (Exception ex) { Log.Write("Reply failed: " + ex.Message); }
        }
    }

    /// <summary>Shared services created once at Revit startup.</summary>
    public class Services
    {
        public AppConfig Config { get; set; }
        public RevitDispatcher Dispatcher { get; set; }
        public CatalogService Catalog { get; set; }
        public ToolRunner Runner { get; set; }
        public IdeaService Ideas { get; set; }
        public string RevitYear { get; set; }
    }
}
