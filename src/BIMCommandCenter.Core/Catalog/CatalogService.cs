using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
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
    /// Loads the tool catalog from whatever the organization uses as the source of truth:
    ///   - .xlsx  : SharePoint Excel synced via OneDrive (central shared file)
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

        public Dictionary<string, object> FindTool(string toolId)
        {
            if (_last?.Tools == null) return null;
            var found = _last.Tools.FirstOrDefault(t => string.Equals(t.Str("toolId"), toolId, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;

            return _last.Tools.FirstOrDefault(t =>
                string.Equals(t.Str("title"), toolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Str("name"), toolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(t.Str("targetPath")), toolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(t.Str("targetPath")), toolId, StringComparison.OrdinalIgnoreCase));
        }

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
                    // Only load external catalog source if configured with a real enterprise source (skip local sample/dummy JSON)
                    if (!string.IsNullOrWhiteSpace(_config.CatalogSource) &&
                        !_config.CatalogSource.EndsWith("sample_catalog.json", StringComparison.OrdinalIgnoreCase) &&
                        !_config.CatalogSource.EndsWith("dummy_catalog.json", StringComparison.OrdinalIgnoreCase))
                    {
                        raw = await LoadRawAsync(_config.CatalogSource).ConfigureAwait(false);
                    }
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
                string[] ghFiles = Directory.GetFiles(root, "*.gh", SearchOption.AllDirectories);
                string[] ghxFiles = Directory.GetFiles(root, "*.ghx", SearchOption.AllDirectories);

                foreach (string file in dynFiles.Concat(pyFiles).Concat(lnkFiles).Concat(dllFiles).Concat(ghFiles).Concat(ghxFiles))
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
                    string[] parts = relDir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

                    string category = "Central Library";
                    var engineFolderNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "Dynamo", "PyRevit", "Python", "Custom Addins", "CustomAddins", "Addins", "Add-ins", "Grasshopper", "Rhino", "Scripts"
                    };

                    if (parts.Length == 0)
                    {
                        category = "Central Library";
                    }
                    else if (parts.Length == 1)
                    {
                        // e.g. <root>/Documentation OR <root>/Dynamo
                        category = engineFolderNames.Contains(parts[0]) ? "Central Library" : parts[0];
                    }
                    else
                    {
                        // e.g. <root>/Documentation/Dynamo OR <root>/Structures/Analysis/PyRevit
                        // If deepest folder is an engine folder, strip it from category name
                        if (engineFolderNames.Contains(parts[parts.Length - 1]))
                        {
                            category = string.Join(" / ", parts.Take(parts.Length - 1));
                        }
                        else if (engineFolderNames.Contains(parts[0]))
                        {
                            category = string.Join(" / ", parts.Skip(1));
                        }
                        else
                        {
                            category = string.Join(" / ", parts);
                        }
                    }

                    // If folder structure did not specify a domain category (e.g. script is in root or in Dynamo/ directly),
                    // intelligently auto-classify based on the script name keywords (e.g. Auto DIM -> Documentation)
                    if (string.Equals(category, "Central Library", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(category))
                    {
                        category = InferCategoryFromName(baseName);
                    }

                    if (existing.Contains(file) || existing.Contains(fileName)) continue;

                    string targetPath = file;
                    string actionType = (ext == ".gh" || ext == ".ghx") ? "Grasshopper" : (ext == ".dyn" ? "Dynamo" : (ext == ".py" ? "PyRevit" : "NativeAddin"));
                    string iconName = (ext == ".gh" || ext == ".ghx") ? "Box" : (ext == ".dyn" ? "PlayCircle" : (ext == ".py" ? "Code" : "Activity"));

                    if (ext == ".lnk")
                    {
                        string resolved = ResolveShortcutTarget(file);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            targetPath = resolved;
                            string rExt = Path.GetExtension(resolved).ToLowerInvariant();
                            actionType = (rExt == ".gh" || rExt == ".ghx") ? "Grasshopper" : (rExt == ".dyn" ? "Dynamo" : (rExt == ".py" ? "PyRevit" : "NativeAddin"));
                            iconName = (rExt == ".gh" || rExt == ".ghx") ? "Box" : (rExt == ".dyn" ? "PlayCircle" : (rExt == ".py" ? "Code" : "Activity"));
                        }
                    }

                    string friendlyTitle = Regex.Replace(baseName, "[_-]+", " ").Trim();
                    try { friendlyTitle = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(friendlyTitle); } catch {}

                    string finalExt = Path.GetExtension(targetPath).ToLowerInvariant();
                    var meta = ExtractScriptMetadata(targetPath, finalExt, friendlyTitle, category);

                    var autoTool = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["toolId"] = "local-" + Regex.Replace(baseName.ToLowerInvariant(), "[^a-z0-9]+", "-"),
                        ["title"] = friendlyTitle,
                        ["name"] = friendlyTitle,
                        ["category"] = category,
                        ["description"] = meta.Description,
                        ["intendedOutcome"] = meta.Description,
                        ["keywords"] = new List<string> { baseName.ToLowerInvariant(), friendlyTitle.ToLowerInvariant(), category.ToLowerInvariant() },
                        ["tags"] = new List<string> { category.ToLowerInvariant(), actionType.ToLowerInvariant() },
                        ["actionType"] = actionType,
                        ["targetPath"] = targetPath,
                        ["executionMode"] = "Player",
                        ["status"] = "Approved",
                        ["riskLevel"] = meta.SafetyLevel == "ReadOnly" ? "Low" : "Medium",
                        ["owningTeam"] = "Central BIM Library",
                        ["author"] = meta.Author,
                        ["safetyLevel"] = meta.SafetyLevel,
                        ["affectedData"] = meta.SafetyLevel == "ReadOnly" ? "Read-only model analysis / reporting." : "Modifies active Revit model elements.",
                        ["canAITrigger"] = true,
                        ["version"] = "v1.0 (Central)",
                        ["iconName"] = iconName,
                        ["revitVersions"] = new List<string> { "2023", "2024", "2025", "2026" },
                        ["packagesRequired"] = meta.PackagesRequired,
                        ["lastTestedDate"] = "Central Shared Asset",
                        ["parameters"] = meta.Parameters
                    };

                    tools.Insert(0, autoTool); // Put user's central tools at the top!
                    existing.Add(file);
                }

                // Check for companion .desc.txt overrides on existing catalog tools
                foreach (var tool in tools)
                {
                    string tp = tool.Str("targetPath");
                    if (!string.IsNullOrEmpty(tp) && File.Exists(tp))
                    {
                        string dir = Path.GetDirectoryName(tp);
                        string bName = Path.GetFileNameWithoutExtension(tp);
                        string sidecar = Path.Combine(dir, bName + ".desc.txt");
                        if (File.Exists(sidecar))
                        {
                            try
                            {
                                string customDesc = File.ReadAllText(sidecar).Trim();
                                if (!string.IsNullOrEmpty(customDesc))
                                {
                                    tool["description"] = customDesc;
                                    tool["intendedOutcome"] = customDesc;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("ScanScriptsRoot error: " + ex.Message);
            }
        }

        private class ScriptMetadata
        {
            public string Description { get; set; }
            public string Author { get; set; } = "Central BIM Library";
            public string PackagesRequired { get; set; } = "None";
            public string SafetyLevel { get; set; } = "Modifying";
            public List<Dictionary<string, object>> Parameters { get; set; } = new List<Dictionary<string, object>>();
        }

        /// <summary>
        /// Scans scripts (Dynamo, pyRevit, Add-ins) and companion description files to extract
        /// rich documentation, dependencies, parameters, and enterprise descriptions.
        /// </summary>
        private static ScriptMetadata ExtractScriptMetadata(string targetPath, string ext, string friendlyTitle, string category)
        {
            var meta = new ScriptMetadata();

            // 1. Check for companion description file: {script}.desc.txt or {script}.txt
            try
            {
                string dir = Path.GetDirectoryName(targetPath);
                string baseName = Path.GetFileNameWithoutExtension(targetPath);
                string sidecarTxt = Path.Combine(dir, baseName + ".desc.txt");
                if (!File.Exists(sidecarTxt)) sidecarTxt = Path.Combine(dir, baseName + ".txt");

                if (File.Exists(sidecarTxt))
                {
                    string txt = File.ReadAllText(sidecarTxt).Trim();
                    if (!string.IsNullOrEmpty(txt)) meta.Description = txt;
                }
                else
                {
                    string sidecarJson = Path.Combine(dir, baseName + ".json");
                    if (File.Exists(sidecarJson))
                    {
                        var parsed = Json.AsObject(Json.Parse(File.ReadAllText(sidecarJson)));
                        if (parsed != null)
                        {
                            if (parsed.TryGetValue("description", out object d) && d != null) meta.Description = d.ToString();
                            if (parsed.TryGetValue("author", out object a) && a != null) meta.Author = a.ToString();
                            if (parsed.TryGetValue("safetyLevel", out object s) && s != null) meta.SafetyLevel = s.ToString();
                            if (parsed.TryGetValue("packagesRequired", out object p) && p != null) meta.PackagesRequired = p.ToString();
                        }
                    }
                }
            }
            catch { }

            // 2. Dynamo (.dyn) parser: Dynamo files are JSON containing Description, Author, NodeLibraryDependencies, Inputs
            if (string.IsNullOrEmpty(meta.Description) && ext.Equals(".dyn", StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
            {
                try
                {
                    string json = File.ReadAllText(targetPath);
                    var dyn = Json.AsObject(Json.Parse(json));
                    if (dyn != null)
                    {
                        if (dyn.TryGetValue("Description", out object d) && d is string ds && !string.IsNullOrWhiteSpace(ds))
                            meta.Description = ds.Trim();

                        if (dyn.TryGetValue("Author", out object a) && a is string asStr && !string.IsNullOrWhiteSpace(asStr))
                            meta.Author = asStr.Trim();

                        if (dyn.TryGetValue("NodeLibraryDependencies", out object deps) && deps is List<object> depList && depList.Count > 0)
                        {
                            var depNames = new List<string>();
                            foreach (var depObj in depList)
                            {
                                if (depObj is Dictionary<string, object> depDict && depDict.TryGetValue("Name", out object dn) && dn != null)
                                    depNames.Add(dn.ToString());
                            }
                            if (depNames.Count > 0) meta.PackagesRequired = string.Join(", ", depNames);
                        }

                        if (dyn.TryGetValue("Inputs", out object inp) && inp is List<object> inpList)
                        {
                            foreach (var item in inpList)
                            {
                                if (item is Dictionary<string, object> inpDict)
                                {
                                    string pName = inpDict.TryGetValue("Name", out object pn) ? pn?.ToString() : "Parameter";
                                    string pType = inpDict.TryGetValue("Type", out object pt) ? pt?.ToString() : "string";
                                    meta.Parameters.Add(new Dictionary<string, object>
                                    {
                                        ["name"] = pName,
                                        ["type"] = pType,
                                        ["required"] = false
                                    });
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            // 3. PyRevit (.py) parser: inspects docstrings ("""..."""), __doc__, and comment headers
            if (string.IsNullOrEmpty(meta.Description) && ext.Equals(".py", StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(targetPath);
                    var sb = new StringBuilder();
                    bool inDocstring = false;
                    string quoteType = "";

                    for (int i = 0; i < Math.Min(lines.Length, 60); i++)
                    {
                        string line = lines[i].Trim();
                        if (!inDocstring)
                        {
                            if (line.StartsWith("\"\"\"") || line.StartsWith("'''"))
                            {
                                quoteType = line.Substring(0, 3);
                                inDocstring = true;
                                string remainder = line.Substring(3);
                                if (remainder.EndsWith(quoteType) && remainder.Length >= 3)
                                {
                                    sb.Append(remainder.Substring(0, remainder.Length - 3).Trim());
                                    break;
                                }
                                else if (!string.IsNullOrEmpty(remainder))
                                {
                                    sb.AppendLine(remainder);
                                }
                            }
                            else if (line.StartsWith("__doc__", StringComparison.OrdinalIgnoreCase))
                            {
                                var m = Regex.Match(line, @"__doc__\s*=\s*['""]([^'""]+)['""]");
                                if (m.Success) { sb.Append(m.Groups[1].Value); break; }
                            }
                            else if (line.StartsWith("__author__", StringComparison.OrdinalIgnoreCase))
                            {
                                var m = Regex.Match(line, @"__author__\s*=\s*['""]([^'""]+)['""]");
                                if (m.Success) meta.Author = m.Groups[1].Value;
                            }
                            else if (line.StartsWith("# Description:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("# Desc:", StringComparison.OrdinalIgnoreCase))
                            {
                                sb.Append(line.Substring(line.IndexOf(':') + 1).Trim());
                                break;
                            }
                        }
                        else
                        {
                            if (line.EndsWith(quoteType))
                            {
                                sb.Append(line.Substring(0, line.Length - 3).Trim());
                                break;
                            }
                            sb.AppendLine(line);
                        }
                    }

                    string doc = sb.ToString().Trim();
                    if (!string.IsNullOrEmpty(doc)) meta.Description = doc;
                }
                catch { }
            }

            // 4. Binary Add-in (.dll) parser: FileVersionInfo metadata
            if (string.IsNullOrEmpty(meta.Description) && ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
            {
                try
                {
                    var vi = FileVersionInfo.GetVersionInfo(targetPath);
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription) && !vi.FileDescription.Equals(Path.GetFileName(targetPath), StringComparison.OrdinalIgnoreCase))
                        meta.Description = vi.FileDescription;
                    else if (!string.IsNullOrWhiteSpace(vi.Comments))
                        meta.Description = vi.Comments;

                    if (!string.IsNullOrWhiteSpace(vi.CompanyName)) meta.Author = vi.CompanyName;
                }
                catch { }
            }

            // 5. Intelligent synthetic enterprise description if none was declared in script
            if (string.IsNullOrWhiteSpace(meta.Description))
            {
                string lower = friendlyTitle.ToLowerInvariant();
                if (lower.Contains("audit") || lower.Contains("check") || lower.Contains("health"))
                {
                    meta.Description = $"Performs automated model audit, quality checks, and reporting for {friendlyTitle}.";
                    meta.SafetyLevel = "ReadOnly";
                }
                else if (lower.Contains("renumber") || lower.Contains("number") || lower.Contains("sequence"))
                {
                    meta.Description = $"Sequences and renumbers model parameters according to project rules for {friendlyTitle}.";
                }
                else if (lower.Contains("export") || lower.Contains("batch") || lower.Contains("print") || lower.Contains("pdf"))
                {
                    meta.Description = $"Batch processes and exports target drawings, sheets, or schedules for {friendlyTitle}.";
                    meta.SafetyLevel = "ReadOnly";
                }
                else if (lower.Contains("clash") || lower.Contains("clearance") || lower.Contains("prep"))
                {
                    meta.Description = $"Coordinates geometry, runs interference prep, and validates clearance tolerances for {friendlyTitle}.";
                }
                else if (lower.Contains("align") || lower.Contains("sheet") || lower.Contains("view"))
                {
                    meta.Description = $"Aligns views and element arrangements according to sheet standard templates for {friendlyTitle}.";
                }
                else if (lower.Contains("sync") || lower.Contains("cleanup") || lower.Contains("purge"))
                {
                    meta.Description = $"Automates model maintenance, purge routines, and worksharing synchronization for {friendlyTitle}.";
                }
                else
                {
                    meta.Description = $"Enterprise automation tool for {friendlyTitle} operations in active Revit models.";
                }
            }

            return meta;
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
        /// <summary>
        /// Intelligently categorizes a script based on filename / title keywords when not explicitly specified by a folder.
        /// </summary>
        public static string InferCategoryFromName(string fileNameOrTitle)
        {
            if (string.IsNullOrWhiteSpace(fileNameOrTitle)) return "Documentation";
            string lower = fileNameOrTitle.ToLowerInvariant();

            // Quality & QA
            if (Regex.IsMatch(lower, @"\b(audit|health|check|checker|qa|qc|warning|warnings|clash|clashes|hygiene|compliance|validate|validation|corrupt)\b", RegexOptions.IgnoreCase))
                return "Quality & QA";

            // Documentation & Annotations
            if (Regex.IsMatch(lower, @"\b(dim|dims|dimension|dimensions|tag|tags|text|sheet|sheets|view|views|schedule|schedules|door|doors|room|rooms|number|renumber|renumbering|mark|marks|export|pdf|dwg|nwc|ifc|titleblock|annotation|annotations|keynote|keynotes|revision|revisions|legend|legends)\b", RegexOptions.IgnoreCase))
                return "Documentation";

            // Model Maintenance
            if (Regex.IsMatch(lower, @"\b(purge|cad|unused|unplaced|redundant|workset|worksets|sync|compact|cleanup|clean|maintenance|link|links|import|imports)\b", RegexOptions.IgnoreCase))
                return "Model Maintenance";

            // Modeling & Geometry
            if (Regex.IsMatch(lower, @"\b(wall|walls|floor|floors|column|columns|beam|beams|slab|slabs|family|families|geometry|mass|facade|grid|grids|level|levels|railing|railings|stair|stairs|roof|roofs|join|split|ceiling|ceilings|curtain|framing|structure|structural|mep|pipe|pipes|duct|ducts|conduit|conduits|electrical|plumbing|hvac)\b", RegexOptions.IgnoreCase))
                return "Modeling";

            // Computational
            if (Regex.IsMatch(lower, @"\b(computational|parametric|algorithm|voronoi|attractor|mesh|panel|paneling|generative|math|pattern|solar|analysis)\b", RegexOptions.IgnoreCase))
                return "Computational";

            return "Documentation";
        }
    }
}
