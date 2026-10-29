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
using BIMCommandCenter.Core.Revit;
using BIMCommandCenter.Core.Security;

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
        private readonly GovernanceService _governance;
        private List<Dictionary<string, object>> _allTools = new List<Dictionary<string, object>>();
        private CatalogResult _last;

        public CatalogService(AppConfig config, string revitYear, GovernanceService governance = null)
        {
            _config = config;
            _revitYear = revitYear;
            _governance = governance;
        }

        private string CachePath => Path.Combine(AppConfig.LocalDataDir, "catalog_cache.json");

        public CatalogResult Current => _last;

        private static readonly Dictionary<string, object> IntrinsicBimHealthCheck = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["toolId"] = "bim-health-check",
            ["title"] = "BIM Health Check",
            ["category"] = "Quality & QA",
            ["description"] = "Comprehensive health audit of model elements, warnings, purgeable items, views, links, and CAD imports.",
            ["intendedOutcome"] = "Audits model integrity, health score, parameters, warnings, views, and links.",
            ["keywords"] = new List<object> { "health", "check", "audit", "hygiene", "warnings", "purge", "model health", "model check", "quality", "qa", "score" },
            ["iconName"] = "activity",
            ["actionType"] = "NativeAddin",
            ["targetPath"] = "tools/BIMHealthCheck.dll",
            ["executionMode"] = "Native",
            ["revitVersions"] = new List<object> { "2023", "2024", "2025", "2026" },
            ["packagesRequired"] = "None",
            ["status"] = "Approved",
            ["canAITrigger"] = true,
            ["safetyLevel"] = "ReadOnly",
            ["affectedData"] = "Read-only analysis (generates comprehensive audit report).",
            ["version"] = "1.0",
            ["author"] = "BIM Systems"
        };

        private void EnsureIntrinsicTools(List<Dictionary<string, object>> tools)
        {
            if (!tools.Any(t => string.Equals(t.Str("toolId"), "bim-health-check", StringComparison.OrdinalIgnoreCase)))
            {
                tools.Add(new Dictionary<string, object>(IntrinsicBimHealthCheck));
            }
        }

        public Dictionary<string, object> FindTool(string toolId)
        {
            if (string.Equals(toolId, "bim-health-check", StringComparison.OrdinalIgnoreCase))
            {
                var foundHealth = _last?.Tools?.FirstOrDefault(t => string.Equals(t.Str("toolId"), "bim-health-check", StringComparison.OrdinalIgnoreCase))
                    ?? _allTools?.FirstOrDefault(t => string.Equals(t.Str("toolId"), "bim-health-check", StringComparison.OrdinalIgnoreCase));
                if (foundHealth != null) return foundHealth;
                return IntrinsicBimHealthCheck;
            }

            var pool = (_last?.Tools != null && _last.Tools.Count > 0) ? _last.Tools : _allTools;
            if (pool == null) return null;
            var found = pool.FirstOrDefault(t => string.Equals(t.Str("toolId"), toolId, StringComparison.OrdinalIgnoreCase));
            if (found != null) return found;

            // Also check _allTools in case it was filtered out by governance but still needs authorization checking
            if (_allTools != null && _allTools != pool)
            {
                var fullFound = _allTools.FirstOrDefault(t => string.Equals(t.Str("toolId"), toolId, StringComparison.OrdinalIgnoreCase));
                if (fullFound != null) return fullFound;
            }

            return pool.FirstOrDefault(t =>
                string.Equals(t.Str("title"), toolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(t.Str("name"), toolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileNameWithoutExtension(t.Str("targetPath")), toolId, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Path.GetFileName(t.Str("targetPath")), toolId, StringComparison.OrdinalIgnoreCase));
        }

        public async Task<CatalogResult> GetAsync(bool forceRefresh)
        {
            if (!forceRefresh && _last != null && !_last.FromCache &&
                DateTime.Now - _last.LoadedAt < TimeSpan.FromMinutes(Math.Max(1, _config.CacheMinutes)))
            {
                return new CatalogResult
                {
                    Tools = Filter(_allTools),
                    Source = _last.Source,
                    FromCache = _last.FromCache,
                    Warning = _last.Warning,
                    LoadedAt = _last.LoadedAt
                };
            }

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
                EnsureIntrinsicTools(tools);

                if (_governance != null)
                {
                    _governance.LoadGovernance(forceRefresh);
                    foreach (var tool in tools)
                    {
                        _governance.ApplyGovernanceRule(tool);
                    }
                }

                _allTools = tools;

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

                // 1. Scan and register .addin manifest packages (multi-command / multi-DLL add-ins)
                string[] addinFiles = Directory.GetFiles(root, "*.addin", SearchOption.AllDirectories);
                foreach (string addinFile in addinFiles)
                {
                    if (addinFile.IndexOf(".disabled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        File.Exists(addinFile + ".disabled") ||
                        File.Exists(Path.Combine(Path.GetDirectoryName(addinFile), Path.GetFileNameWithoutExtension(addinFile) + ".disabled")))
                    {
                        continue;
                    }

                    try
                    {
                        string content = File.ReadAllText(addinFile);
                        string addinDir = Path.GetDirectoryName(addinFile);
                        string relDir = addinDir.Length > root.Length ? addinDir.Substring(root.Length).TrimStart('\\', '/') : "";
                        string[] parts = relDir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
                        string category = DetermineCategory(parts, Path.GetFileNameWithoutExtension(addinFile));

                        var cmdMatches = Regex.Matches(content, @"<AddIn\s+Type=""Command""[^>]*>([\s\S]*?)</AddIn>", RegexOptions.IgnoreCase);
                        foreach (Match m in cmdMatches)
                        {
                            string block = m.Groups[1].Value;
                            string assemblyRel = Regex.Match(block, @"<Assembly>([\s\S]*?)</Assembly>", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                            string fullClass = Regex.Match(block, @"<FullClassName>([\s\S]*?)</FullClassName>", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                            string cmdName = Regex.Match(block, @"<Name>([\s\S]*?)</Name>", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                            if (string.IsNullOrEmpty(cmdName))
                                cmdName = Regex.Match(block, @"<Text>([\s\S]*?)</Text>", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                            string desc = Regex.Match(block, @"<Description>([\s\S]*?)</Description>", RegexOptions.IgnoreCase).Groups[1].Value.Trim();
                            string vendor = Regex.Match(block, @"<VendorDescription>([\s\S]*?)</VendorDescription>", RegexOptions.IgnoreCase).Groups[1].Value.Trim();

                            if (string.IsNullOrEmpty(assemblyRel)) continue;

                            string targetDll = Path.IsPathRooted(assemblyRel) ? assemblyRel : Path.GetFullPath(Path.Combine(addinDir, assemblyRel));
                            if (!File.Exists(targetDll))
                            {
                                string siblingDll = Path.Combine(addinDir, Path.GetFileName(assemblyRel));
                                if (File.Exists(siblingDll)) targetDll = siblingDll;
                            }

                            if (!File.Exists(targetDll)) continue;

                            string toolTitle = !string.IsNullOrWhiteSpace(cmdName) ? cmdName : Regex.Replace(Path.GetFileNameWithoutExtension(targetDll), "[_-]+", " ");
                            try { toolTitle = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(toolTitle); } catch {}

                            string idKey = Path.GetFileNameWithoutExtension(addinFile) + "-" + (!string.IsNullOrEmpty(cmdName) ? cmdName : fullClass);
                            string toolId = "local-addin-" + Regex.Replace(idKey.ToLowerInvariant(), "[^a-z0-9]+", "-");

                            var addinTool = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                            {
                                ["toolId"] = toolId,
                                ["title"] = toolTitle,
                                ["name"] = toolTitle,
                                ["category"] = category,
                                ["description"] = !string.IsNullOrWhiteSpace(desc) ? desc : $"Revit C# add-in command from {Path.GetFileName(addinFile)}",
                                ["intendedOutcome"] = !string.IsNullOrWhiteSpace(desc) ? desc : $"Revit C# add-in command from {Path.GetFileName(addinFile)}",
                                ["keywords"] = new List<string> { Path.GetFileNameWithoutExtension(targetDll).ToLowerInvariant(), toolTitle.ToLowerInvariant(), category.ToLowerInvariant() },
                                ["tags"] = new List<string> { category.ToLowerInvariant(), "addin", "nativeaddin" },
                                ["actionType"] = "NativeAddin",
                                ["targetPath"] = targetDll,
                                ["commandClass"] = fullClass,
                                ["executionMode"] = "Player",
                                ["status"] = "Approved",
                                ["riskLevel"] = "Medium",
                                ["owningTeam"] = !string.IsNullOrWhiteSpace(vendor) ? vendor : "Central BIM Library",
                                ["author"] = !string.IsNullOrWhiteSpace(vendor) ? vendor : "Central BIM Library",
                                ["safetyLevel"] = "ReadWrite",
                                ["affectedData"] = "Modifies active Revit model elements.",
                                ["canAITrigger"] = true,
                                ["version"] = "v1.0 (Central)",
                                ["iconName"] = "Activity",
                                ["revitVersions"] = new List<string> { "2023", "2024", "2025", "2026" },
                                ["packagesRequired"] = "",
                                ["lastTestedDate"] = "Central Shared Asset",
                                ["parameters"] = new List<Dictionary<string, object>>()
                            };

                            tools.Insert(0, addinTool);
                            existing.Add(targetDll);
                            existing.Add(Path.GetFileName(targetDll));
                        }

                        existing.Add(addinFile);
                    }
                    catch (Exception ex)
                    {
                        Log.Write($"Failed parsing addin manifest {addinFile}: {ex.Message}");
                    }
                }

                string[] dynFiles = Directory.GetFiles(root, "*.dyn", SearchOption.AllDirectories);
                string[] pyFiles = Directory.GetFiles(root, "*.py", SearchOption.AllDirectories);
                string[] lnkFiles = Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories);
                string[] dllFiles = Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories);
                string[] ghFiles = Directory.GetFiles(root, "*.gh", SearchOption.AllDirectories);
                string[] ghxFiles = Directory.GetFiles(root, "*.ghx", SearchOption.AllDirectories);

                foreach (string file in dynFiles.Concat(pyFiles).Concat(lnkFiles).Concat(dllFiles).Concat(ghFiles).Concat(ghxFiles))
                {
                    // Ignore any disabled files or flag sidecars
                    if (file.IndexOf(".disabled", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        File.Exists(file + ".disabled") ||
                        File.Exists(Path.Combine(Path.GetDirectoryName(file), Path.GetFileNameWithoutExtension(file) + ".disabled")))
                    {
                        continue;
                    }

                    string fileName = Path.GetFileName(file);
                    bool isGenericScript = fileName.Equals("script.py", StringComparison.OrdinalIgnoreCase) ||
                                           fileName.Equals("main.py", StringComparison.OrdinalIgnoreCase) ||
                                           fileName.Equals("run.py", StringComparison.OrdinalIgnoreCase);

                    if (existing.Contains(file)) continue;
                    if (!isGenericScript && existing.Contains(fileName)) continue;

                    string baseName = Path.GetFileNameWithoutExtension(file);
                    string ext = Path.GetExtension(file).ToLowerInvariant();

                    string dir = Path.GetDirectoryName(file);
                    string relDir = dir.Length > root.Length ? dir.Substring(root.Length).TrimStart('\\', '/') : "";
                    string[] parts = relDir.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);

                    if (ext == ".dll")
                    {
                        // 1. Skip DLLs in dependency subfolders
                        if (parts.Any(p => p.Equals("lib", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("libs", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("packages", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("deps", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("dependencies", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("runtimes", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("x86", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        // 2. Skip common library / framework dependencies
                        if (fileName.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) || 
                            fileName.StartsWith("System", StringComparison.OrdinalIgnoreCase) || 
                            fileName.StartsWith("Revit", StringComparison.OrdinalIgnoreCase) || 
                            fileName.StartsWith("Autodesk", StringComparison.OrdinalIgnoreCase) || 
                            fileName.StartsWith("Newtonsoft", StringComparison.OrdinalIgnoreCase) || 
                            fileName.StartsWith("RestSharp", StringComparison.OrdinalIgnoreCase) || 
                            fileName.StartsWith("MaterialDesign", StringComparison.OrdinalIgnoreCase) || 
                            fileName.StartsWith("MahApps", StringComparison.OrdinalIgnoreCase) || 
                            fileName.StartsWith("BIMCommandCenter", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // 3. If a companion .addin manifest exists in the same folder, only include the entry assembly
                        string[] addinManifests = Directory.GetFiles(dir, "*.addin");
                        if (addinManifests.Length > 0)
                        {
                            bool isManifestEntry = false;
                            foreach (var mf in addinManifests)
                            {
                                try
                                {
                                    string mfContent = File.ReadAllText(mf);
                                    if (mfContent.IndexOf(fileName, StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        isManifestEntry = true;
                                        break;
                                    }
                                }
                                catch { }
                            }

                            if (!isManifestEntry)
                            {
                                // Sibling dependency DLL in a manifest-driven addin package
                                continue;
                            }
                        }
                    }

                    if (ext == ".py")
                    {
                        // 1. Skip Python helper modules in dependency / library subfolders (e.g. lib/, packages/)
                        if (parts.Any(p => p.Equals("lib", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("libs", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("packages", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("deps", StringComparison.OrdinalIgnoreCase) ||
                                           p.Equals("dependencies", StringComparison.OrdinalIgnoreCase) ||
                                           p.StartsWith("_", StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        // 2. Skip __init__.py files
                        if (fileName.Equals("__init__.py", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        // 3. If standard pyRevit pushbutton (script.py), derive baseName from the folder name (unless it's just the root PyRevit folder)
                        if (fileName.Equals("script.py", StringComparison.OrdinalIgnoreCase))
                        {
                            string parentFolder = Path.GetFileName(dir);
                            if (!string.IsNullOrEmpty(parentFolder) &&
                                !parentFolder.Equals("pyrevit", StringComparison.OrdinalIgnoreCase) &&
                                !parentFolder.Equals("python", StringComparison.OrdinalIgnoreCase) &&
                                !parentFolder.Equals("scripts", StringComparison.OrdinalIgnoreCase))
                            {
                                string cleanParent = Regex.Replace(parentFolder, @"\.(pushbutton|smartbutton|urlbutton|splitbutton|pulldown|linkbutton)$", "", RegexOptions.IgnoreCase);
                                if (!string.IsNullOrWhiteSpace(cleanParent))
                                {
                                    baseName = cleanParent;
                                }
                            }
                        }
                    }

                    string category = DetermineCategory(parts, baseName);

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

                            if (baseName.Equals("script", StringComparison.OrdinalIgnoreCase))
                            {
                                string resolvedBase = Path.GetFileNameWithoutExtension(resolved);
                                if (!string.IsNullOrWhiteSpace(resolvedBase) && !resolvedBase.Equals("script", StringComparison.OrdinalIgnoreCase))
                                {
                                    baseName = resolvedBase;
                                }
                            }
                        }
                    }

                    string friendlyTitle = Regex.Replace(baseName, "[_-]+", " ").Trim();
                    try { friendlyTitle = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(friendlyTitle); } catch {}

                    string finalExt = Path.GetExtension(targetPath).ToLowerInvariant();
                    if (ext == ".py" || finalExt == ".py" || actionType == "PyRevit" || fileName.Equals("script.py", StringComparison.OrdinalIgnoreCase) || baseName.Equals("script", StringComparison.OrdinalIgnoreCase))
                    {
                        string pyTitle = ResolvePyRevitTitle(targetPath, friendlyTitle);
                        if (!string.IsNullOrWhiteSpace(pyTitle) && !pyTitle.Equals("script", StringComparison.OrdinalIgnoreCase))
                        {
                            friendlyTitle = pyTitle;
                        }
                    }

                    var meta = ExtractScriptMetadata(targetPath, finalExt, friendlyTitle, category);
                    if (!string.IsNullOrWhiteSpace(meta.TitleOverride) && !meta.TitleOverride.Equals("script", StringComparison.OrdinalIgnoreCase))
                    {
                        friendlyTitle = meta.TitleOverride;
                    }

                    string safeId = Regex.Replace(friendlyTitle.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
                    if (string.IsNullOrEmpty(safeId) || safeId == "script")
                    {
                        safeId = "tool-" + Math.Abs(file.GetHashCode()).ToString("x");
                    }

                    var autoTool = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["toolId"] = "local-" + safeId,
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
                    if (!isGenericScript) existing.Add(fileName);
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
            public string TitleOverride { get; set; }
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

            // 1. Check for companion description file: {script}.desc.txt or {script}.txt, or pyRevit bundle.yaml
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

                // Check for companion pyRevit bundle.yaml metadata
                string bundleYaml = Path.Combine(dir, "bundle.yaml");
                if (!File.Exists(bundleYaml)) bundleYaml = Path.Combine(dir, "bundle.yml");
                if (File.Exists(bundleYaml))
                {
                    string yamlContent = File.ReadAllText(bundleYaml);
                    var locMatch = Regex.Match(yamlContent, @"(?:^|\n)\s*title:\s*\r?\n\s+[a-zA-Z_-]+:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
                    if (locMatch.Success && !string.IsNullOrWhiteSpace(locMatch.Groups[1].Value))
                    {
                        meta.TitleOverride = CleanPyRevitTitle(locMatch.Groups[1].Value);
                    }
                    else
                    {
                        var titleMatch = Regex.Match(yamlContent, @"(?:^|\n)\s*_?title:\s*[""']?([^""'\r\n]+)[""']?", RegexOptions.IgnoreCase);
                        if (titleMatch.Success && !string.IsNullOrWhiteSpace(titleMatch.Groups[1].Value))
                        {
                            meta.TitleOverride = CleanPyRevitTitle(titleMatch.Groups[1].Value);
                        }
                    }

                    var tooltipMatch = Regex.Match(yamlContent, @"(?:^|\n)\s*tooltip:\s*[""']?([^""'\r\n]+)[""']?", RegexOptions.IgnoreCase);
                    if (tooltipMatch.Success && !string.IsNullOrWhiteSpace(tooltipMatch.Groups[1].Value))
                    {
                        meta.Description = CleanPyRevitTitle(tooltipMatch.Groups[1].Value);
                    }
                    var authorMatch = Regex.Match(yamlContent, @"(?:^|\n)\s*author:\s*[""']?([^""'\r\n]+)[""']?", RegexOptions.IgnoreCase);
                    if (authorMatch.Success && !string.IsNullOrWhiteSpace(authorMatch.Groups[1].Value))
                    {
                        meta.Author = authorMatch.Groups[1].Value.Trim('\'', '"', ' ');
                    }
                }
            }
            catch { }

            // 2. Dynamo (.dyn) parser: Dynamo files are JSON containing Description, Author, NodeLibraryDependencies, Inputs
            if (ext.Equals(".dyn", StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
            {
                try
                {
                    var details = DynamoPackageInspector.InspectGraph(targetPath);
                    if (details.Dependencies.Count > 0 && (string.IsNullOrEmpty(meta.PackagesRequired) || meta.PackagesRequired == "None"))
                    {
                        meta.PackagesRequired = string.Join(", ", details.Dependencies);
                    }

                    // Populate parameters from details.Inputs (both Dynamo Player schema and all detected interactive nodes)
                    foreach (var inp in details.Inputs)
                    {
                        meta.Parameters.Add(new Dictionary<string, object>
                        {
                            ["id"] = inp.Id,
                            ["name"] = inp.Name,
                            ["type"] = inp.Type,
                            ["value"] = inp.Value,
                            ["description"] = inp.Description,
                            ["isSelection"] = inp.IsSelection,
                            ["required"] = false
                        });
                    }

                    // Also extract Description and Author from the JSON if not already set
                    if (string.IsNullOrEmpty(meta.Description) || string.IsNullOrEmpty(meta.Author))
                    {
                        string json = File.ReadAllText(targetPath);
                        var dyn = Json.AsObject(Json.Parse(json));
                        if (dyn != null)
                        {
                            if (string.IsNullOrEmpty(meta.Description) && dyn.TryGetValue("Description", out object d) && d is string ds && !string.IsNullOrWhiteSpace(ds))
                                meta.Description = ds.Trim();

                            if (string.IsNullOrEmpty(meta.Author) && dyn.TryGetValue("Author", out object a) && a is string asStr && !string.IsNullOrWhiteSpace(asStr))
                                meta.Author = asStr.Trim();
                        }
                    }
                }
                catch { }
            }

            // 3. PyRevit (.py) parser: inspects __title__, docstrings ("""..."""), __doc__, and comment headers
            if (ext.Equals(".py", StringComparison.OrdinalIgnoreCase) && File.Exists(targetPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(targetPath);
                    var sb = new StringBuilder();
                    bool inDocstring = false;
                    string quoteType = "";

                    for (int i = 0; i < Math.Min(lines.Length, 80); i++)
                    {
                        string line = lines[i].Trim();

                        // Check __title__ = "..."
                        if (string.IsNullOrEmpty(meta.TitleOverride) && line.StartsWith("__title__", StringComparison.OrdinalIgnoreCase))
                        {
                            var tm = Regex.Match(lines[i], "__title__\\s*=\\s*(?:[bB]?r?|r?[bB]?)(?:\"\"\"([\\s\\S]*?)\"\"\"|'''([\\s\\S]*?)'''|\"([^\"\\r\\n]*)\"|'([^'\\r\\n]*)')", RegexOptions.IgnoreCase);
                            if (tm.Success)
                            {
                                string raw = tm.Groups[1].Success && !string.IsNullOrEmpty(tm.Groups[1].Value) ? tm.Groups[1].Value :
                                            (tm.Groups[2].Success && !string.IsNullOrEmpty(tm.Groups[2].Value) ? tm.Groups[2].Value :
                                            (tm.Groups[3].Success && !string.IsNullOrEmpty(tm.Groups[3].Value) ? tm.Groups[3].Value : tm.Groups[4].Value));
                                string clean = CleanPyRevitTitle(raw);
                                if (!string.IsNullOrWhiteSpace(clean) && !clean.Equals("script", StringComparison.OrdinalIgnoreCase))
                                {
                                    meta.TitleOverride = clean;
                                }
                            }
                        }

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
                            else if (line.StartsWith("# Title:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("# Name:", StringComparison.OrdinalIgnoreCase))
                            {
                                if (string.IsNullOrEmpty(meta.TitleOverride))
                                {
                                    string t = CleanPyRevitTitle(line.Substring(line.IndexOf(':') + 1));
                                    if (!string.IsNullOrWhiteSpace(t) && !t.Equals("script", StringComparison.OrdinalIgnoreCase))
                                        meta.TitleOverride = t;
                                }
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
                    if (string.IsNullOrEmpty(meta.Description) && !string.IsNullOrEmpty(doc)) meta.Description = doc;

                    // Fallback title resolution if not explicitly set yet
                    if (string.IsNullOrEmpty(meta.TitleOverride) || meta.TitleOverride.Equals("script", StringComparison.OrdinalIgnoreCase))
                    {
                        string res = ResolvePyRevitTitle(targetPath, friendlyTitle);
                        if (!string.IsNullOrWhiteSpace(res) && !res.Equals("script", StringComparison.OrdinalIgnoreCase))
                        {
                            meta.TitleOverride = res;
                        }
                    }
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

        /// <summary>
        /// Resolves a human-friendly title for pyRevit pushbuttons and Python scripts.
        /// Inspects in order:
        ///   1. Python source code for __title__ = "..." or __title__ = """...""" (replacing \n with space)
        ///   2. pyRevit bundle.yaml (title: ..., _title: ..., or indented localized title: en_us)
        ///   3. Enclosing folder hierarchy (stripping .pushbutton, .smartbutton, etc. and splitting CamelCase)
        ///   4. Script docstring / comment header (# Title: ...)
        ///   5. Fallback to defaultTitle or cleaned file basename
        /// </summary>
        public static string ResolvePyRevitTitle(string scriptPath, string defaultTitle = null)
        {
            if (string.IsNullOrWhiteSpace(scriptPath)) return defaultTitle ?? "PyRevit Tool";

            try
            {
                string filePath = scriptPath;
                if (Directory.Exists(filePath))
                {
                    string cand = Path.Combine(filePath, "script.py");
                    if (File.Exists(cand)) filePath = cand;
                }

                string dir = Path.GetDirectoryName(filePath);

                // 1. Check Python script content for __title__ = "..."
                if (File.Exists(filePath))
                {
                    try
                    {
                        using (var reader = new StreamReader(filePath, Encoding.UTF8))
                        {
                            char[] buffer = new char[8192];
                            int read = reader.Read(buffer, 0, buffer.Length);
                            if (read > 0)
                            {
                                string content = new string(buffer, 0, read);
                                var m = Regex.Match(content, "__title__\\s*=\\s*(?:[bB]?r?|r?[bB]?)(?:\"\"\"([\\s\\S]*?)\"\"\"|'''([\\s\\S]*?)'''|\"([^\"\\r\\n]*)\"|'([^'\\r\\n]*)')", RegexOptions.IgnoreCase);
                                if (m.Success)
                                {
                                    string raw = m.Groups[1].Success && !string.IsNullOrEmpty(m.Groups[1].Value) ? m.Groups[1].Value :
                                                (m.Groups[2].Success && !string.IsNullOrEmpty(m.Groups[2].Value) ? m.Groups[2].Value :
                                                (m.Groups[3].Success && !string.IsNullOrEmpty(m.Groups[3].Value) ? m.Groups[3].Value : m.Groups[4].Value));

                                    if (!string.IsNullOrWhiteSpace(raw))
                                    {
                                        string clean = CleanPyRevitTitle(raw);
                                        if (!string.IsNullOrWhiteSpace(clean) && !clean.Equals("script", StringComparison.OrdinalIgnoreCase))
                                        {
                                            return clean;
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }

                // 2. Check bundle.yaml / bundle.yml in directory
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    string bundleYaml = Path.Combine(dir, "bundle.yaml");
                    if (!File.Exists(bundleYaml)) bundleYaml = Path.Combine(dir, "bundle.yml");

                    if (File.Exists(bundleYaml))
                    {
                        try
                        {
                            string yaml = File.ReadAllText(bundleYaml);
                            var mLoc = Regex.Match(yaml, @"(?:^|\n)\s*title:\s*\r?\n\s+[a-zA-Z_-]+:\s*([^\r\n]+)", RegexOptions.IgnoreCase);
                            if (mLoc.Success && !string.IsNullOrWhiteSpace(mLoc.Groups[1].Value))
                            {
                                string clean = CleanPyRevitTitle(mLoc.Groups[1].Value);
                                if (!string.IsNullOrWhiteSpace(clean) && !clean.Equals("script", StringComparison.OrdinalIgnoreCase))
                                    return clean;
                            }

                            var mDirect = Regex.Match(yaml, @"(?:^|\n)\s*_?title:\s*[""']?([^""'\r\n]+)[""']?", RegexOptions.IgnoreCase);
                            if (mDirect.Success && !string.IsNullOrWhiteSpace(mDirect.Groups[1].Value))
                            {
                                string clean = CleanPyRevitTitle(mDirect.Groups[1].Value);
                                if (!string.IsNullOrWhiteSpace(clean) && !clean.Equals("script", StringComparison.OrdinalIgnoreCase))
                                    return clean;
                            }
                        }
                        catch { }
                    }
                }

                // 3. Inspect directory hierarchy (e.g. DoorRenumber.pushbutton, 01_BatchExport.smartbutton)
                if (!string.IsNullOrEmpty(dir))
                {
                    DirectoryInfo cur = new DirectoryInfo(dir);
                    int depth = 0;
                    while (cur != null && depth < 3)
                    {
                        string folderName = cur.Name;
                        if (Regex.IsMatch(folderName, @"\.(pushbutton|smartbutton|urlbutton|splitbutton|pulldown|linkbutton)$", RegexOptions.IgnoreCase))
                        {
                            string baseFolder = Regex.Replace(folderName, @"\.(pushbutton|smartbutton|urlbutton|splitbutton|pulldown|linkbutton)$", "", RegexOptions.IgnoreCase);
                            string clean = FormatTitleFromFolderName(baseFolder);
                            if (!string.IsNullOrWhiteSpace(clean) && !clean.Equals("script", StringComparison.OrdinalIgnoreCase))
                                return clean;
                        }

                        if (!cur.Name.Equals("scripts", StringComparison.OrdinalIgnoreCase) &&
                            !cur.Name.Equals("pyrevit", StringComparison.OrdinalIgnoreCase) &&
                            !cur.Name.Equals("python", StringComparison.OrdinalIgnoreCase) &&
                            !cur.Name.Equals("tools", StringComparison.OrdinalIgnoreCase) &&
                            !cur.Name.Equals("bin", StringComparison.OrdinalIgnoreCase) &&
                            !cur.Name.Equals("lib", StringComparison.OrdinalIgnoreCase) &&
                            !cur.Name.EndsWith(".extension", StringComparison.OrdinalIgnoreCase) &&
                            !cur.Name.EndsWith(".tab", StringComparison.OrdinalIgnoreCase) &&
                            !cur.Name.EndsWith(".panel", StringComparison.OrdinalIgnoreCase))
                        {
                            string clean = FormatTitleFromFolderName(cur.Name);
                            if (!string.IsNullOrWhiteSpace(clean) && !clean.Equals("script", StringComparison.OrdinalIgnoreCase))
                                return clean;
                        }

                        cur = cur.Parent;
                        depth++;
                    }
                }

                // 4. Check comments/docstring for # Title: ...
                if (File.Exists(filePath))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(filePath);
                        for (int i = 0; i < Math.Min(lines.Length, 30); i++)
                        {
                            string line = lines[i].Trim();
                            if (line.StartsWith("# Title:", StringComparison.OrdinalIgnoreCase) ||
                                line.StartsWith("# Name:", StringComparison.OrdinalIgnoreCase))
                            {
                                string val = line.Substring(line.IndexOf(':') + 1).Trim();
                                string clean = CleanPyRevitTitle(val);
                                if (!string.IsNullOrWhiteSpace(clean) && !clean.Equals("script", StringComparison.OrdinalIgnoreCase))
                                    return clean;
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            if (!string.IsNullOrWhiteSpace(defaultTitle) &&
                !defaultTitle.Equals("script", StringComparison.OrdinalIgnoreCase) &&
                !defaultTitle.Equals("script.py", StringComparison.OrdinalIgnoreCase))
            {
                return defaultTitle;
            }

            string fileBase = Path.GetFileNameWithoutExtension(scriptPath);
            if (!string.IsNullOrWhiteSpace(fileBase) && !fileBase.Equals("script", StringComparison.OrdinalIgnoreCase))
            {
                return FormatTitleFromFolderName(fileBase);
            }

            return "PyRevit Tool";
        }

        private static string CleanPyRevitTitle(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";
            string clean = raw.Replace("\\n", " ").Replace("\\r", " ").Replace("\\t", " ")
                              .Replace("\r\n", " ").Replace("\n", " ").Replace("\r", " ").Replace("\t", " ");
            clean = Regex.Replace(clean, @"\s+", " ").Trim('"', '\'', ' ', '\t');
            try
            {
                clean = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(clean);
            }
            catch { }
            return clean;
        }

        private static string FormatTitleFromFolderName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "";
            string clean = Regex.Replace(name, @"^[0-9]+[_\s-]+", "");
            clean = Regex.Replace(clean, @"([a-z0-9])([A-Z])", "$1 $2");
            clean = Regex.Replace(clean, @"[_-]+", " ").Trim();
            try
            {
                clean = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(clean);
            }
            catch { }
            return clean;
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

        /// <summary>Hide deprecated tools and tools not built for this Revit version, and apply governance filtering.</summary>
        private List<Dictionary<string, object>> Filter(List<Dictionary<string, object>> tools) =>
            tools.Where(t =>
            {
                if (string.Equals(t.Str("status"), "Deprecated", StringComparison.OrdinalIgnoreCase)) return false;
                List<string> versions = t.StrList("revitVersions");
                if (versions.Count > 0 && !versions.Any(v => v.Contains(_revitYear))) return false;

                if (_governance != null)
                {
                    if (!_governance.IsToolVisible(_governance.CurrentUser, t)) return false;
                }

                return true;
            }).ToList();
        private static string DetermineCategory(string[] parts, string baseName)
        {
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
                category = engineFolderNames.Contains(parts[0]) ? "Central Library" : parts[0];
            }
            else
            {
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

            if (string.Equals(category, "Central Library", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(category))
            {
                category = InferCategoryFromName(baseName);
            }

            return category;
        }

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
