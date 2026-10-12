using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BIMCommandCenter.Core.Infrastructure;

namespace BIMCommandCenter.Core.Catalog
{
    public class CatalogResult
    {
        public List<Dictionary<string, object>> Tools { get; set; } = new List<Dictionary<string, object>>();
        public string Source { get; set; }
        public bool FromCache { get; set; }
        public string Warning { get; set; }
        public DateTime LoadedAt { get; set; }
    }

    /// <summary>
    /// Loads the tool catalog from whatever the team uses as the source of truth:
    ///   - .xlsx  : SharePoint Excel synced via OneDrive (teammate's file)
    ///   - .json  : local / network JSON
    ///   - https  : Power Automate HTTP flow or any endpoint returning JSON (array, {value:[]}, {tools:[]}, Graph list items)
    /// Results are normalised to the schema in the spec and cached locally so the UI still works offline.
    /// </summary>
    public class CatalogService
    {
        private static readonly string[] SchemaKeys =
        {
            "toolId", "title", "category", "description", "keywords", "iconName", "actionType", "targetPath",
            "executionMode", "revitVersions", "packagesRequired", "status", "canAITrigger", "version", "author"
        };

        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

        private readonly AppConfig _config;
        private readonly string _revitYear;
        private CatalogResult _last;

        public CatalogService(AppConfig config, string revitYear)
        {
            _config = config;
            _revitYear = revitYear;
        }

        private string CachePath => Path.Combine(AppConfig.LocalDataDir, "catalog_cache.json");

        public CatalogResult Current => _last;

        public Dictionary<string, object> FindTool(string toolId) =>
            _last?.Tools.FirstOrDefault(t => string.Equals(t.Str("toolId"), toolId, StringComparison.OrdinalIgnoreCase));

        public async Task<CatalogResult> GetAsync(bool forceRefresh)
        {
            if (!forceRefresh && _last != null && !_last.FromCache &&
                DateTime.Now - _last.LoadedAt < TimeSpan.FromMinutes(Math.Max(1, _config.CacheMinutes)))
                return _last;

            try
            {
                List<Dictionary<string, object>> raw = new List<Dictionary<string, object>>();
                try
                {
                    raw = await LoadRawAsync(_config.CatalogSource).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Write("Catalog source load failed: " + ex.Message);
                }

                List<Dictionary<string, object>> tools = raw.Select(Normalize).Where(IsUsable).ToList();
                ScanScriptsRoot(tools);

                try { File.WriteAllText(CachePath, Json.Stringify(tools)); } catch { }

                _last = new CatalogResult { Tools = Filter(tools), Source = _config.CatalogSource, LoadedAt = DateTime.Now };
            }
            catch (Exception ex)
            {
                Log.Write("Catalog load failed: " + ex);
                _last = LoadFromCache("Could not reach catalog source (" + ex.Message + "). Showing last cached copy.");
            }
            return _last;
        }

        private void ScanScriptsRoot(List<Dictionary<string, object>> tools)
        {
            if (string.IsNullOrWhiteSpace(_config.ScriptsRoot)) return;
            string root = Environment.ExpandEnvironmentVariables(_config.ScriptsRoot);
            if (!Directory.Exists(root)) return;

            try
            {
                var existing = new HashSet<string>(tools.Select(t => t.Str("targetPath")).Where(p => !string.IsNullOrEmpty(p)), StringComparer.OrdinalIgnoreCase);

                string[] dynFiles = Directory.GetFiles(root, "*.dyn", SearchOption.AllDirectories);
                string[] pyFiles = Directory.GetFiles(root, "*.py", SearchOption.AllDirectories);
                string[] lnkFiles = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories);
                string[] dllFiles = Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories);

                foreach (string file in dynFiles.Concat(pyFiles).Concat(lnkFiles).Concat(dllFiles))
                {
                    string fileName = Path.GetFileName(file);
                    string baseName = Path.GetFileNameWithoutExtension(file);
                    string ext = Path.GetExtension(file).ToLowerInvariant();

                    if (ext == ".dll" && (fileName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || 
                                          fileName.StartsWith("System", StringComparison.OrdinalIgnoreCase) || 
                                          fileName.StartsWith("Revit", StringComparison.OrdinalIgnoreCase) || 
                                          fileName.StartsWith("BIMCommandCenter", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    string dir = Path.GetDirectoryName(file);
                    string relDir = dir.Length > root.Length ? dir.Substring(root.Length).TrimStart('\\', '/') : "";
                    string category = string.IsNullOrWhiteSpace(relDir) ? "Central Library" : relDir.Replace("\\", " / ");

                    if (existing.Contains(file) || existing.Contains(fileName)) continue;

                    string targetPath = file;
                    string actionType = ext == ".dyn" ? "Dynamo" : (ext == ".py" ? "PyRevit" : "NativeAddin");
                    string iconName = ext == ".dyn" ? "PlayCircle" : (ext == ".py" ? "Code" : "Activity");

                    if (ext == ".lnk")
                    {
                        string resolved = ResolveShortcutTarget(file);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            targetPath = resolved;
                            string rExt = Path.GetExtension(resolved).ToLowerInvariant();
                            actionType = rExt == ".dyn" ? "Dynamo" : (rExt == ".py" ? "PyRevit" : "NativeAddin");
                            iconName = rExt == ".dyn" ? "PlayCircle" : (rExt == ".py" ? "Code" : "Activity");
                        }
                    }

                    string friendlyTitle = Regex.Replace(baseName, "[_-]+", " ").Trim();
                    try { friendlyTitle = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(friendlyTitle); } catch {}

                    var autoTool = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["toolId"] = "local-" + Regex.Replace(baseName.ToLowerInvariant(), "[^a-z0-9]+", "-"),
                        ["title"] = friendlyTitle,
                        ["name"] = friendlyTitle,
                        ["category"] = category,
                        ["description"] = "Tool or shortcut located in your central folder: " + (string.IsNullOrEmpty(relDir) ? fileName : Path.Combine(relDir, fileName)),
                        ["intendedOutcome"] = "Custom automation tool loaded from central library (" + category + ").",
                        ["keywords"] = new List<string> { baseName.ToLowerInvariant(), friendlyTitle.ToLowerInvariant(), category.ToLowerInvariant() },
                        ["tags"] = new List<string> { category.ToLowerInvariant(), actionType.ToLowerInvariant() },
                        ["actionType"] = actionType,
                        ["targetPath"] = targetPath,
                        ["executionMode"] = "Player",
                        ["status"] = "Approved",
                        ["riskLevel"] = "Medium",
                        ["owningTeam"] = "Firm Library (" + category + ")",
                        ["author"] = Environment.UserName,
                        ["safetyLevel"] = "Modifying",
                        ["affectedData"] = "Modifies active Revit model elements.",
                        ["canAITrigger"] = true,
                        ["version"] = "v1.0 (Local)",
                        ["iconName"] = iconName,
                        ["revitVersions"] = new List<string> { "2023", "2024", "2025", "2026" },
                        ["packagesRequired"] = "None reported",
                        ["lastTestedDate"] = "Auto-discovered local tool",
                        ["parameters"] = new List<Dictionary<string, object>>()
                    };

                    tools.Insert(0, autoTool); // Put user's central tools at the top!
                    existing.Add(file);
                }
            }
            catch (Exception ex)
            {
                Log.Write("ScanScriptsRoot error: " + ex.Message);
            }
        }

        private static string ResolveShortcutTarget(string shortcutPath)
        {
            try
            {
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType != null)
                {
                    object shell = Activator.CreateInstance(shellType);
                    MethodInfo createShortcut = shellType.GetMethod("CreateShortcut");
                    if (createShortcut != null)
                    {
                        object shortcut = createShortcut.Invoke(shell, new object[] { shortcutPath });
                        if (shortcut != null)
                        {
                            PropertyInfo targetProp = shortcut.GetType().GetProperty("TargetPath");
                            if (targetProp != null)
                            {
                                string target = targetProp.GetValue(shortcut, null) as string;
                                if (!string.IsNullOrEmpty(target)) return target;
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private CatalogResult LoadFromCache(string warning)
        {
            var result = new CatalogResult { Source = CachePath, FromCache = true, Warning = warning, LoadedAt = DateTime.Now };
            try
            {
                if (File.Exists(CachePath) && Json.Parse(File.ReadAllText(CachePath)) is List<object> list)
                    result.Tools = Filter(list.OfType<Dictionary<string, object>>().ToList());
                else
                    result.Warning = "Catalog source unavailable and no cached copy exists yet.";
            }
            catch (Exception ex)
            {
                result.Warning = "Catalog cache unreadable: " + ex.Message;
            }
            return result;
        }

        private async Task<List<Dictionary<string, object>>> LoadRawAsync(string source)
        {
            if (string.IsNullOrWhiteSpace(source)) throw new InvalidOperationException("catalogSource is not configured.");

            if (source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                string body = await Http.GetStringAsync(source).ConfigureAwait(false);
                return ExtractList(Json.Parse(body));
            }

            if (!File.Exists(source)) throw new FileNotFoundException("Catalog file not found", source);

            if (source.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
            {
                return XlsxReader.ReadRows(source, _config.CatalogSheet)
                    .Select(r => r.ToDictionary(kv => kv.Key, kv => (object)kv.Value, StringComparer.OrdinalIgnoreCase))
                    .ToList();
            }

            return ExtractList(Json.Parse(File.ReadAllText(source)));
        }

        private static List<Dictionary<string, object>> ExtractList(object parsed)
        {
            if (parsed is Dictionary<string, object> obj)
            {
                foreach (string key in new[] { "value", "tools", "items", "rows" })
                    if (obj.TryGetValue(key, out object inner) && inner is List<object>)
                    {
                        parsed = inner;
                        break;
                    }
            }

            var list = parsed as List<object> ?? throw new InvalidDataException("Catalog JSON must be an array of tools.");
            return list.OfType<Dictionary<string, object>>()
                // Microsoft Graph list items wrap columns in "fields"
                .Select(d => d.TryGetValue("fields", out object f) && f is Dictionary<string, object> fd ? fd : d)
                .ToList();
        }

        /// <summary>Maps loosely-named columns ("Tool ID", "Target Path", "Revit Versions") onto schema keys.</summary>
        private static Dictionary<string, object> Normalize(Dictionary<string, object> source)
        {
            var lookup = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in source) lookup[Regex.Replace(kv.Key, "[^A-Za-z0-9]", "")] = kv.Value;

            var tool = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in SchemaKeys)
                tool[key] = lookup.TryGetValue(key, out object v) ? v : null;

            // Common SharePoint column aliases
            if (tool["title"] == null && lookup.TryGetValue("Name", out object name)) tool["title"] = name;
            if (tool["description"] == null && lookup.TryGetValue("Desc", out object desc)) tool["description"] = desc;
            if (tool["targetPath"] == null && lookup.TryGetValue("Path", out object path)) tool["targetPath"] = path;
            if (tool["category"] == null) tool["category"] = "General";

            tool["keywords"] = tool.StrList("keywords");
            tool["revitVersions"] = tool.StrList("revitVersions");
            tool["canAITrigger"] = tool.Bool("canAITrigger");
            tool["actionType"] = NormalizeActionType(tool.Str("actionType", "Dynamo"));
            tool["executionMode"] = NormalizeMode(tool.Str("executionMode", "Interactive"));
            tool["status"] = tool.Str("status", "Active");

            if (string.IsNullOrWhiteSpace(tool.Str("toolId")))
                tool["toolId"] = Regex.Replace((tool.Str("title") ?? "tool").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');

            foreach (string key in SchemaKeys)
                if (tool[key] == null) tool[key] = "";
            return tool;
        }

        private static string NormalizeActionType(string value)
        {
            string v = Regex.Replace(value ?? "", "[^A-Za-z]", "").ToLowerInvariant();
            if (v.StartsWith("dyn")) return "Dynamo";
            if (v.StartsWith("py")) return "PyRevit";
            if (v.Contains("postable") || v.Contains("revitnative") || v == "native") return "PostableCommand";
            if (v.Contains("addin") || v.Contains("ribbon") || v.Contains("plugin")) return "NativeAddin";
            if (v.Contains("url") || v.Contains("link") || v.Contains("web")) return "Url";
            return value;
        }

        private static string NormalizeMode(string value)
        {
            string v = (value ?? "").Trim().ToLowerInvariant();
            return v.StartsWith("head") || v.Contains("silent") || v.Contains("1-click") ? "Headless" : "Interactive";
        }

        private static bool IsUsable(Dictionary<string, object> t) =>
            !string.IsNullOrWhiteSpace(t.Str("title")) && !string.IsNullOrWhiteSpace(t.Str("targetPath"));

        /// <summary>Hide deprecated tools and tools not built for this Revit version.</summary>
        private List<Dictionary<string, object>> Filter(List<Dictionary<string, object>> tools) =>
            tools.Where(t =>
            {
                if (string.Equals(t.Str("status"), "Deprecated", StringComparison.OrdinalIgnoreCase)) return false;
                List<string> versions = t.StrList("revitVersions");
                return versions.Count == 0 || versions.Any(v => v.Contains(_revitYear));
            }).ToList();
    }
}
