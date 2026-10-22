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
using BIMCommandCenter.Core.Security;
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

                case "getAuthSession":
                {
                    var cur = _s.Governance?.CurrentUser;
                    bool configured = _s.Governance?.Data?.Users != null && _s.Governance.Data.Users.Count > 0;
                    return new Dictionary<string, object>
                    {
                        ["configured"] = configured,
                        ["loggedIn"] = cur != null,
                        ["user"] = cur?.ToDictionary(),
                        ["suggestedUsername"] = Environment.UserName,
                        ["governancePath"] = _s.Governance?.LoadedPath
                    };
                }

                case "login":
                {
                    string username = payload.Str("username");
                    string pin = payload.Str("pin");
                    bool rememberMe = payload.Bool("rememberMe");

                    if (_s.Governance == null)
                        throw new InvalidOperationException("Governance service is not available.");

                    if (!_s.Governance.Login(username, pin, rememberMe, out string err))
                    {
                        throw new InvalidOperationException(err ?? "Login failed.");
                    }

                    // Refresh catalog to apply user role and discipline filters
                    await _s.Catalog.GetAsync(true);

                    return new Dictionary<string, object>
                    {
                        ["loggedIn"] = true,
                        ["user"] = _s.Governance.CurrentUser.ToDictionary()
                    };
                }

                case "logout":
                {
                    _s.Governance?.Logout();
                    await _s.Catalog.GetAsync(true);
                    return new Dictionary<string, object>
                    {
                        ["loggedIn"] = false,
                        ["suggestedUsername"] = Environment.UserName
                    };
                }

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

                case "checkDynamoPackages":
                {
                    string req = payload.Str("packagesRequired");
                    var check = DynamoPackageInspector.CheckPackages(req);
                    return new Dictionary<string, object>
                    {
                        ["missing"] = check.Missing,
                        ["installed"] = check.Installed,
                        ["hasMissing"] = check.HasMissing,
                        ["instructions"] = check.Instructions,
                        ["allInstalled"] = DynamoPackageInspector.GetInstalledPackages()
                    };
                }

                case "executeTool":
                {
                    string toolId = payload.Str("toolId");
                    if (_s.Catalog.Current == null) await _s.Catalog.GetAsync(false);
                    Dictionary<string, object> tool = _s.Catalog.FindTool(toolId);

                    if (tool == null)
                    {
                        // Refresh catalog immediately to discover any recently dropped scripts
                        await _s.Catalog.GetAsync(true);
                        tool = _s.Catalog.FindTool(toolId);
                    }

                    if (tool == null && string.Equals(toolId, "bim-health-check", StringComparison.OrdinalIgnoreCase))
                    {
                        tool = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                        {
                            ["toolId"] = "bim-health-check",
                            ["title"] = "BIM Health Check",
                            ["actionType"] = "NativeAddin",
                            ["targetPath"] = "tools/BIMHealthCheck.dll",
                            ["executionMode"] = "Native",
                            ["safetyLevel"] = "ReadOnly",
                            ["intendedOutcome"] = "Audits model integrity, health score, parameters, warnings, views, and links."
                        };
                    }

                    if (tool == null)
                    {
                        // Direct file resolution fallback if toolId points to or matches a script on disk
                        string resolved = _s.Runner.ResolvePath(toolId);
                        if (!string.IsNullOrEmpty(resolved) && File.Exists(resolved))
                        {
                            string ext = Path.GetExtension(resolved).ToLowerInvariant();
                            string actionType = (ext == ".gh" || ext == ".ghx") ? "Grasshopper" : (ext == ".dyn" ? "Dynamo" : (ext == ".py" ? "PyRevit" : "NativeAddin"));
                            tool = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["toolId"] = toolId,
                                ["title"] = Path.GetFileNameWithoutExtension(resolved),
                                ["actionType"] = actionType,
                                ["targetPath"] = resolved,
                                ["executionMode"] = "Player",
                                ["safetyLevel"] = "Modifying",
                                ["packagesRequired"] = "None"
                            };
                        }
                    }

                    if (tool == null)
                        throw new InvalidOperationException("Tool \"" + toolId + "\" is not in the catalog.");

                    // Enforce role and discipline authorization from governance
                    string targetId = tool.Str("toolId") ?? toolId;
                    if (_s.Governance != null)
                    {
                        if (!_s.Governance.IsAuthorized(_s.Governance.CurrentUser, targetId, out string denyReason))
                        {
                            throw new UnauthorizedAccessException(denyReason ?? "You are not authorized to run this tool.");
                        }
                    }

                    Log.Write("Execute " + toolId + " by " + (_s.Governance?.CurrentUser?.Username ?? Environment.UserName));

                    Dictionary<string, string> inputs = null;
                    if (payload.ContainsKey("inputs") && payload["inputs"] is Dictionary<string, object> rawInputs)
                    {
                        inputs = rawInputs.ToDictionary(k => k.Key, v => v.Value?.ToString() ?? "");
                    }

                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    RunResult result = await _s.Dispatcher.InvokeAsync(app => _s.Runner.Run(app, tool, inputs));
                    sw.Stop();
                    result.DurationSeconds = Math.Round(sw.Elapsed.TotalSeconds, 2);

                    // Record to audit trail
                    RecordRunAudit(tool, result, payload);

                    return result.ToDictionary();
                }

                case "launchDynamoPlayer":
                {
                    string toolId = payload.Str("toolId");
                    string targetPath = payload.Str("targetPath");
                    if (_s.Catalog.Current == null) await _s.Catalog.GetAsync(false);
                    var tool = _s.Catalog.FindTool(toolId);
                    if (tool != null && string.IsNullOrEmpty(targetPath))
                    {
                        targetPath = tool.Str("targetPath");
                    }

                    // Enforce authorization for Dynamo Player launch
                    string targetId = tool?.Str("toolId") ?? toolId;
                    if (_s.Governance != null)
                    {
                        if (!_s.Governance.IsAuthorized(_s.Governance.CurrentUser, targetId, out string denyReason))
                        {
                            throw new UnauthorizedAccessException(denyReason ?? "You are not authorized to run this tool.");
                        }
                    }

                    string resolved = _s.Runner.ResolvePath(targetPath);
                    RunResult result = await _s.Dispatcher.InvokeAsync(app => _s.Runner.LaunchDynamoPlayer(app, resolved, tool));
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

                case "getDynamoGraphDetails":
                {
                    string toolId = payload.Str("toolId");
                    string targetPath = payload.Str("targetPath");
                    if (_s.Catalog.Current == null) await _s.Catalog.GetAsync(false);
                    var tool = _s.Catalog.FindTool(toolId);
                    if (tool != null && string.IsNullOrEmpty(targetPath))
                    {
                        targetPath = tool.Str("targetPath");
                    }
                    string resolved = _s.Runner.ResolvePath(targetPath);
                    var details = DynamoPackageInspector.InspectGraph(resolved);
                    return new Dictionary<string, object>
                    {
                        ["path"] = resolved,
                        ["hasSelectionInputs"] = details.HasSelectionInputs,
                        ["hasDataShapesForm"] = details.HasDataShapesForm,
                        ["inputs"] = details.Inputs.Select(i => new Dictionary<string, object>
                        {
                            ["id"] = i.Id,
                            ["name"] = i.Name,
                            ["type"] = i.Type,
                            ["value"] = i.Value,
                            ["description"] = i.Description,
                            ["options"] = i.Options,
                            ["isSelection"] = i.IsSelection,
                            ["min"] = i.Min,
                            ["max"] = i.Max,
                            ["step"] = i.Step
                        }).ToList(),
                        ["outputs"] = details.Outputs.Select(o => new Dictionary<string, object>
                        {
                            ["name"] = o.Name,
                            ["description"] = o.Description
                        }).ToList(),
                        ["dependencies"] = details.Dependencies
                    };
                }

                case "getRevitSelection":
                {
                    var selResult = await _s.Dispatcher.InvokeAsync(app =>
                    {
                        var uidoc = app.ActiveUIDocument;
                        if (uidoc == null || uidoc.Document == null)
                        {
                            return new Dictionary<string, object>
                            {
                                ["count"] = 0,
                                ["summary"] = "No active Revit project",
                                ["elements"] = new List<object>(),
                                ["uniqueIds"] = new List<string>()
                            };
                        }

                        var doc = uidoc.Document;
                        var selIds = uidoc.Selection.GetElementIds();
                        var elements = new List<Dictionary<string, object>>();
                        var catCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                        foreach (var id in selIds)
                        {
                            var elem = doc.GetElement(id);
                            if (elem == null) continue;
                            string cat = elem.Category?.Name ?? "Element";
                            if (!catCounts.ContainsKey(cat)) catCounts[cat] = 0;
                            catCounts[cat]++;

                            elements.Add(new Dictionary<string, object>
                            {
                                ["id"] = id.ToString(),
                                ["uniqueId"] = elem.UniqueId,
                                ["name"] = elem.Name ?? "",
                                ["category"] = cat
                            });
                        }

                        string summary = elements.Count == 0
                            ? "No elements selected in Revit"
                            : string.Join(", ", catCounts.Select(kv => $"{kv.Value} {kv.Key}")) + $" ({elements.Count} total)";

                        return new Dictionary<string, object>
                        {
                            ["count"] = elements.Count,
                            ["summary"] = summary,
                            ["elements"] = elements,
                            ["uniqueIds"] = elements.Select(e => e["uniqueId"]).ToList()
                        };
                    });
                    return selResult;
                }

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
                    _s.Catalog = new CatalogService(_s.Config, _s.RevitYear ?? "2026", _s.Governance);

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
                var curUser = _s.Governance?.CurrentUser;
                string username = !string.IsNullOrWhiteSpace(curUser?.Username) ? curUser.Username : Environment.UserName;
                string displayName = !string.IsNullOrWhiteSpace(curUser?.DisplayName) ? curUser.DisplayName : username;
                string role = !string.IsNullOrWhiteSpace(curUser?.Role) ? curUser.Role : "Unassigned";
                string discipline = !string.IsNullOrWhiteSpace(curUser?.Discipline) ? curUser.Discipline : "All";

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
                    ["user"] = username,
                    ["userDisplayName"] = displayName,
                    ["role"] = role,
                    ["discipline"] = discipline,
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

                // 2. Central Shared Enterprise Telemetry (per-user file to prevent multi-user network locks)
                string centralFolder = !string.IsNullOrWhiteSpace(_s.Config.TelemetryFolder)
                    ? _s.Config.TelemetryFolder
                    : (!string.IsNullOrWhiteSpace(_s.Config.ScriptsRoot) ? System.IO.Path.Combine(_s.Config.ScriptsRoot, "telemetry") : null);

                if (!string.IsNullOrWhiteSpace(centralFolder))
                {
                    try
                    {
                        System.IO.Directory.CreateDirectory(centralFolder);
                        string safeUser = System.Text.RegularExpressions.Regex.Replace(username, @"[^a-zA-Z0-9_\-\.]", "_");
                        string perUserFile = System.IO.Path.Combine(centralFolder, $"runs_{safeUser}.jsonl");
                        string jsonLine = Json.Stringify(entry).Replace("\r", "").Replace("\n", "") + Environment.NewLine;
                        System.IO.File.AppendAllText(perUserFile, jsonLine);

                        // Also attempt appending to unified firm_runs.jsonl
                        try
                        {
                            string firmFile = System.IO.Path.Combine(centralFolder, "firm_runs.jsonl");
                            System.IO.File.AppendAllText(firmFile, jsonLine);
                        }
                        catch { }
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

                // Try reading central corporate runs (aggregating all runs_*.jsonl and firm_runs.jsonl)
                if (!string.IsNullOrWhiteSpace(centralFolder) && System.IO.Directory.Exists(centralFolder))
                {
                    var files = System.IO.Directory.GetFiles(centralFolder, "runs_*.jsonl")
                        .Concat(System.IO.Directory.GetFiles(centralFolder, "firm_runs.jsonl"))
                        .Distinct(StringComparer.OrdinalIgnoreCase);

                    var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var file in files)
                    {
                        try
                        {
                            var lines = System.IO.File.ReadAllLines(file);
                            foreach (var line in lines)
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                try
                                {
                                    var parsed = Json.AsObject(Json.Parse(line));
                                    if (parsed != null)
                                    {
                                        string execId = parsed.Str("id");
                                        if (string.IsNullOrEmpty(execId) || seenIds.Add(execId))
                                        {
                                            runList.Add(parsed);
                                        }
                                    }
                                }
                                catch { }
                            }
                        }
                        catch { }
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
        public GovernanceService Governance { get; set; }
        public CatalogService Catalog { get; set; }
        public ToolRunner Runner { get; set; }
        public IdeaService Ideas { get; set; }
        public string RevitYear { get; set; }
    }
}
