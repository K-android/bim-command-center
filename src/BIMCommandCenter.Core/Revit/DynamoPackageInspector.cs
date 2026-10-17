using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using BIMCommandCenter.Core.Infrastructure;

namespace BIMCommandCenter.Core.Revit
{
    public class DynamoPackageCheckResult
    {
        public List<string> Missing { get; set; } = new List<string>();
        public List<string> Installed { get; set; } = new List<string>();
        public bool HasMissing => Missing.Count > 0;
        public string Instructions { get; set; } = string.Empty;
    }

    public static class DynamoPackageInspector
    {
        /// <summary>
        /// Scans all Dynamo Revit package locations (%AppData% and %ProgramData%) and returns unique installed package names.
        /// </summary>
        public static List<string> GetInstalledPackages()
        {
            var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string[] roots = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dynamo", "Dynamo Revit"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Dynamo", "Dynamo Revit"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Dynamo", "Dynamo Core")
            };

            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;

                try
                {
                    foreach (string versionDir in Directory.GetDirectories(root))
                    {
                        string pkgDir = Path.Combine(versionDir, "packages");
                        if (Directory.Exists(pkgDir))
                        {
                            foreach (string p in Directory.GetDirectories(pkgDir))
                            {
                                string name = Path.GetFileName(p);
                                if (!string.IsNullOrWhiteSpace(name))
                                    installed.Add(name);
                            }
                        }
                    }
                }
                catch { }
            }

            return installed.OrderBy(x => x).ToList();
        }

        /// <summary>
        /// Checks a comma- or semicolon-delimited list of required package names against installed packages.
        /// </summary>
        public static DynamoPackageCheckResult CheckPackages(string packagesRequired)
        {
            var result = new DynamoPackageCheckResult();
            if (string.IsNullOrWhiteSpace(packagesRequired) || 
                packagesRequired.Trim().Equals("None", StringComparison.OrdinalIgnoreCase) ||
                packagesRequired.Trim().Equals("None reported", StringComparison.OrdinalIgnoreCase) ||
                packagesRequired.Trim().Equals("None (Native .NET)", StringComparison.OrdinalIgnoreCase) ||
                packagesRequired.Trim().Equals("None (Standard Revit API)", StringComparison.OrdinalIgnoreCase) ||
                packagesRequired.Trim().Equals("None (Self-contained)", StringComparison.OrdinalIgnoreCase) ||
                packagesRequired.Trim().Equals("Self-contained", StringComparison.OrdinalIgnoreCase) ||
                packagesRequired.Trim().Equals("Built-in", StringComparison.OrdinalIgnoreCase))
            {
                return result;
            }

            var allInstalled = new HashSet<string>(GetInstalledPackages(), StringComparer.OrdinalIgnoreCase);

            var entries = packagesRequired.Split(new[] { ',', ';', '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawEntry in entries)
            {
                string clean = Regex.Replace(rawEntry, @"\(.*?\)", "").Trim();
                if (clean.Equals("None", StringComparison.OrdinalIgnoreCase) ||
                    clean.Equals("None reported", StringComparison.OrdinalIgnoreCase) ||
                    clean.Equals("Self-contained", StringComparison.OrdinalIgnoreCase) ||
                    clean.Equals("Built-in", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Strip common prefixes/suffixes like "for Dynamo 2.x" or "v1.2"
                string coreName = Regex.Replace(clean, @"\s+for\s+Dynamo.*$", "", RegexOptions.IgnoreCase).Trim();
                if (string.IsNullOrWhiteSpace(coreName)) continue;

                bool isFound = allInstalled.Contains(clean) || 
                               allInstalled.Contains(coreName) ||
                               allInstalled.Any(i => i.IndexOf(coreName, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                     coreName.IndexOf(i, StringComparison.OrdinalIgnoreCase) >= 0);

                if (isFound)
                {
                    result.Installed.Add(rawEntry.Trim());
                }
                else
                {
                    result.Missing.Add(rawEntry.Trim());
                }
            }

            if (result.HasMissing)
            {
                result.Instructions =
                    "Missing Dynamo Package(s): " + string.Join(", ", result.Missing) + "\n\n" +
                    "How to install:\n" +
                    "1. Open Autodesk Revit.\n" +
                    "2. Navigate to the 'Manage' tab and launch 'Dynamo'.\n" +
                    "3. In the top menu, go to Packages > Search for a Package...\n" +
                    "4. Search for: " + string.Join(", ", result.Missing) + "\n" +
                    "5. Click 'Install' (or Install Specified Version).\n" +
                    "6. Return to Command Center and run this automation.";
            }

            return result;
        }

        /// <summary>
        /// Reads a .dyn JSON file and extracts configured Inputs, Outputs, and Package Dependencies.
        /// Supports both Dynamo Player schema ("Inputs": [...]) and standard Dynamo node schema ("IsInput": true).
        /// </summary>
        public static DynamoGraphDetails InspectGraph(string dynPath)
        {
            var details = new DynamoGraphDetails();
            if (string.IsNullOrWhiteSpace(dynPath) || !File.Exists(dynPath)) return details;

            try
            {
                string json = File.ReadAllText(dynPath);

                // 1. Extract Inputs array objects (Dynamo Player schema)
                var inputsMatch = Regex.Match(json, @"""Inputs""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
                if (inputsMatch.Success)
                {
                    var itemMatches = Regex.Matches(inputsMatch.Groups[1].Value, @"\{([^{}]*)\}");
                    foreach (Match m in itemMatches)
                    {
                        string block = m.Groups[1].Value;
                        string id = Regex.Match(block, @"""Id""\s*:\s*""([^""]*)""").Groups[1].Value;
                        string name = Regex.Match(block, @"""Name""\s*:\s*""([^""]*)""").Groups[1].Value;
                        string type = Regex.Match(block, @"""Type""\s*:\s*""([^""]*)""").Groups[1].Value;
                        string val = Regex.Match(block, @"""Value""\s*:\s*""([^""]*)""").Groups[1].Value;
                        string desc = Regex.Match(block, @"""Description""\s*:\s*""([^""]*)""").Groups[1].Value;

                        string typeLower = (type ?? "").ToLowerInvariant();
                        bool isSel = typeLower.Contains("select") ||
                                     name.IndexOf("select model element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("select element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("select face", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("selection", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (isSel) typeLower = "selection";

                        double? minVal = null, maxVal = null, stepVal = null;
                        if (double.TryParse(Regex.Match(block, @"""MinimumValue""\s*:\s*([-\d\.]+)").Groups[1].Value, out double min)) minVal = min;
                        if (double.TryParse(Regex.Match(block, @"""MaximumValue""\s*:\s*([-\d\.]+)").Groups[1].Value, out double max)) maxVal = max;
                        if (double.TryParse(Regex.Match(block, @"""StepValue""\s*:\s*([-\d\.]+)").Groups[1].Value, out double stp)) stepVal = stp;

                        var optionsList = new List<string>();
                        var optMatches = Regex.Matches(block, @"""(?:Options|Items)""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
                        if (optMatches.Count > 0)
                        {
                            var rawItems = Regex.Matches(optMatches[0].Groups[1].Value, @"""([^""]*)""");
                            foreach (Match rm in rawItems)
                            {
                                if (!string.IsNullOrWhiteSpace(rm.Groups[1].Value))
                                    optionsList.Add(rm.Groups[1].Value);
                            }
                        }

                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            details.Inputs.Add(new DynamoGraphItem
                            {
                                Id = string.IsNullOrWhiteSpace(id) ? name : id,
                                Name = name,
                                Type = string.IsNullOrWhiteSpace(typeLower) ? "string" : typeLower,
                                Value = val,
                                Description = desc,
                                IsSelection = isSel,
                                Min = minVal,
                                Max = maxVal,
                                Step = stepVal,
                                Options = optionsList
                            });
                        }
                    }
                }

                // 2. Scan standard Nodes array for nodes with "IsInput": true OR selection nodes
                var nodeMatches = Regex.Matches(json, @"\{[^{}]*""(?:IsInput""\s*:\s*true|ConcreteType""\s*:\s*""[^""]*Selection)[^{}]*\}", RegexOptions.Singleline);
                foreach (Match nm in nodeMatches)
                {
                    string block = nm.Value;
                    string id = Regex.Match(block, @"""Id""\s*:\s*""([^""]*)""").Groups[1].Value;
                    string name = Regex.Match(block, @"""Name""\s*:\s*""([^""]*)""").Groups[1].Value;
                    string val = Regex.Match(block, @"""InputValue""\s*:\s*""?([^"",\}]+)""?").Groups[1].Value;
                    string concreteType = Regex.Match(block, @"""ConcreteType""\s*:\s*""([^""]*)""").Groups[1].Value;

                    if (string.IsNullOrWhiteSpace(name)) continue;

                    // Skip if already captured in details.Inputs by Id
                    if (details.Inputs.Any(i => i.Id == id)) continue;

                    string type = "string";
                    bool isSel = false;
                    double? minVal = null, maxVal = null, stepVal = null;
                    var optionsList = new List<string>();

                    if (concreteType.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        concreteType.IndexOf("Selection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Select Model Element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Select Element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("Select Face", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        type = "selection";
                        isSel = true;
                    }
                    else if (concreteType.IndexOf("slider", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        type = "slider";
                        if (double.TryParse(Regex.Match(block, @"""Min""\s*:\s*([-\d\.]+)").Groups[1].Value, out double nMin)) minVal = nMin;
                        if (double.TryParse(Regex.Match(block, @"""Max""\s*:\s*([-\d\.]+)").Groups[1].Value, out double nMax)) maxVal = nMax;
                        if (double.TryParse(Regex.Match(block, @"""Step""\s*:\s*([-\d\.]+)").Groups[1].Value, out double nStep)) stepVal = nStep;
                        if (string.IsNullOrEmpty(val))
                        {
                            val = Regex.Match(block, @"""Number""\s*:\s*([-\d\.]+)").Groups[1].Value;
                        }
                    }
                    else if (concreteType.IndexOf("number", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             concreteType.IndexOf("double", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             concreteType.IndexOf("integer", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        type = "number";
                    }
                    else if (concreteType.IndexOf("bool", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        type = "boolean";
                    }
                    else if (concreteType.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             concreteType.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        type = "filepath";
                    }
                    else if (concreteType.IndexOf("directory", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        type = "directorypath";
                    }
                    else if (concreteType.IndexOf("Categories", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             concreteType.IndexOf("FamilyTypes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             concreteType.IndexOf("Levels", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             concreteType.IndexOf("Phases", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             concreteType.IndexOf("Views", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             concreteType.IndexOf("Sheets", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             concreteType.IndexOf("WallTypes", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        type = "dropdown";
                        string selStr = Regex.Match(block, @"""SelectedString""\s*:\s*""([^""]*)""").Groups[1].Value;
                        if (!string.IsNullOrWhiteSpace(selStr)) val = selStr;
                    }

                    details.Inputs.Add(new DynamoGraphItem
                    {
                        Id = id,
                        Name = name,
                        Type = type,
                        Value = val,
                        Description = isSel ? "Select model elements in active Revit view" : "Dynamo node input",
                        IsSelection = isSel,
                        Min = minVal,
                        Max = maxVal,
                        Step = stepVal,
                        Options = optionsList
                    });
                }

                // 3. Extract Outputs array objects
                var outputsMatch = Regex.Match(json, @"""Outputs""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
                if (outputsMatch.Success)
                {
                    var itemMatches = Regex.Matches(outputsMatch.Groups[1].Value, @"\{([^{}]*)\}");
                    foreach (Match m in itemMatches)
                    {
                        string block = m.Groups[1].Value;
                        string name = Regex.Match(block, @"""Name""\s*:\s*""([^""]*)""").Groups[1].Value;
                        string desc = Regex.Match(block, @"""Description""\s*:\s*""([^""]*)""").Groups[1].Value;

                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            details.Outputs.Add(new DynamoGraphItem
                            {
                                Name = name,
                                Description = desc
                            });
                        }
                    }
                }

                // 4. Extract Dependencies from NodeLibraryDependencies
                var depMatches = Regex.Matches(json, @"""Name""\s*:\s*""([^""]*)""\s*,\s*""Version""");
                foreach (Match dm in depMatches)
                {
                    string dep = dm.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(dep) && !details.Dependencies.Contains(dep))
                        details.Dependencies.Add(dep);
                }
            }
            catch (Exception ex)
            {
                Log.Write("InspectGraph failed for " + dynPath + ": " + ex.Message);
            }

            return details;
        }

        /// <summary>
        /// Injects custom parameter values into a Dynamo .dyn file and saves a temporary copy
        /// for isolated headless execution without altering the central original file.
        /// </summary>
        public static string CreateTempGraphWithInputs(string dynPath, Dictionary<string, string> inputOverrides)
        {
            if (string.IsNullOrWhiteSpace(dynPath) || !File.Exists(dynPath)) return dynPath;
            if (inputOverrides == null || inputOverrides.Count == 0) return dynPath;

            try
            {
                string json = File.ReadAllText(dynPath);

                foreach (var kvp in inputOverrides)
                {
                    string key = kvp.Key;
                    string val = kvp.Value ?? "";

                    // Check if value is a selection list (comma-separated UniqueIds, e.g. "guid1,guid2")
                    string[] uids = val.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(s => s.Trim().Trim('"', '\'', '[', ']', ' '))
                                       .Where(s => !string.IsNullOrEmpty(s))
                                       .ToArray();

                    bool looksLikeSelection = uids.Length > 0 && (key.IndexOf("select", StringComparison.OrdinalIgnoreCase) >= 0 || uids.Any(u => u.Contains("-")));

                    if (looksLikeSelection)
                    {
                        // 1. Update in Inputs array by Id or Name
                        string idInputPattern = @"(\{\s*""Id""\s*:\s*""" + Regex.Escape(key) + @""".*?""Value""\s*:\s*"")([^""]*)("".*?\})";
                        if (Regex.IsMatch(json, idInputPattern, RegexOptions.Singleline))
                        {
                            json = Regex.Replace(json, idInputPattern, "${1}" + uids[0].Replace("$", "$$") + "${3}", RegexOptions.Singleline);
                        }

                        // 2. Format selectionIdentifier for JSON: [ "uid1", "uid2" ]
                        string formattedUids = string.Join(", ", uids.Select(u => $"\"{u}\""));

                        // In Nodes array: try updating existing selectionIdentifier
                        string selPattern = @"(""Id""\s*:\s*""" + Regex.Escape(key) + @""".*?""selectionIdentifier""\s*:\s*\[)(.*?)(\])";
                        if (Regex.IsMatch(json, selPattern, RegexOptions.Singleline))
                        {
                            json = Regex.Replace(json, selPattern, "${1} " + formattedUids.Replace("$", "$$") + " ${3}", RegexOptions.Singleline);
                        }
                        else
                        {
                            // Try inserting selectionIdentifier before "Id"
                            string insertSelPattern = @"(\{.*?)(""Id""\s*:\s*""" + Regex.Escape(key) + @""")";
                            if (Regex.IsMatch(json, insertSelPattern, RegexOptions.Singleline))
                            {
                                json = Regex.Replace(json, insertSelPattern, "${1}\"selectionIdentifier\": [ " + formattedUids.Replace("$", "$$") + " ], ${2}", RegexOptions.Singleline);
                            }
                        }

                        // Also update SelectedElementId if present
                        string singleSelPattern = @"(""Id""\s*:\s*""" + Regex.Escape(key) + @""".*?""SelectedElementId""\s*:\s*"")([^""]*)("")";
                        if (Regex.IsMatch(json, singleSelPattern, RegexOptions.Singleline))
                        {
                            json = Regex.Replace(json, singleSelPattern, "${1}" + uids[0].Replace("$", "$$") + "${3}", RegexOptions.Singleline);
                        }
                    }
                    else
                    {
                        // 1. Update in Inputs array by Id
                        string idPattern = @"(\{\s*""Id""\s*:\s*""" + Regex.Escape(key) + @""".*?""Value""\s*:\s*"")([^""]*)("".*?\})";
                        if (Regex.IsMatch(json, idPattern, RegexOptions.Singleline))
                        {
                            json = Regex.Replace(json, idPattern, "${1}" + val.Replace("$", "$$") + "${3}", RegexOptions.Singleline);
                        }
                        else
                        {
                            // Match in Inputs array by Name
                            string namePattern = @"(\{\s*""Name""\s*:\s*""" + Regex.Escape(key) + @""".*?""Value""\s*:\s*"")([^""]*)("".*?\})";
                            if (Regex.IsMatch(json, namePattern, RegexOptions.Singleline))
                            {
                                json = Regex.Replace(json, namePattern, "${1}" + val.Replace("$", "$$") + "${3}", RegexOptions.Singleline);
                            }
                        }

                        // 2. Update in Nodes array by Id: InputValue
                        string nodePattern = @"(""Id""\s*:\s*""" + Regex.Escape(key) + @""".*?""InputValue""\s*:\s*)([^,\}\s]+)";
                        if (Regex.IsMatch(json, nodePattern, RegexOptions.Singleline))
                        {
                            string formattedVal = (val == "true" || val == "false" || double.TryParse(val, out _))
                                ? val
                                : "\"" + val.Replace("\"", "\\\"") + "\"";
                            json = Regex.Replace(json, nodePattern, "${1}" + formattedVal, RegexOptions.Singleline);
                        }

                        // 3. Update Number (sliders)
                        string numPattern = @"(""Id""\s*:\s*""" + Regex.Escape(key) + @""".*?""Number""\s*:\s*)([^,\}\s]+)";
                        if (Regex.IsMatch(json, numPattern, RegexOptions.Singleline) && double.TryParse(val, out _))
                        {
                            json = Regex.Replace(json, numPattern, "${1}" + val, RegexOptions.Singleline);
                        }

                        // 4. Update SelectedString (dropdowns)
                        string selStrPattern = @"(""Id""\s*:\s*""" + Regex.Escape(key) + @""".*?""SelectedString""\s*:\s*"")([^""]*)("")";
                        if (Regex.IsMatch(json, selStrPattern, RegexOptions.Singleline))
                        {
                            json = Regex.Replace(json, selStrPattern, "${1}" + val.Replace("$", "$$") + "${3}", RegexOptions.Singleline);
                        }
                    }
                }

                string tempDir = Path.Combine(Path.GetTempPath(), "BIMCommandCenter", "DynamoExec");
                Directory.CreateDirectory(tempDir);
                string tempPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(dynPath) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dyn");
                File.WriteAllText(tempPath, json);
                return tempPath;
            }
            catch (Exception ex)
            {
                Log.Write("CreateTempGraphWithInputs error: " + ex.Message);
                return dynPath;
            }
        }
    }

    public class DynamoGraphItem
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public List<string> Options { get; set; } = new List<string>();
        public bool IsSelection { get; set; } = false;
        public double? Min { get; set; }
        public double? Max { get; set; }
        public double? Step { get; set; }
    }

    public class DynamoGraphDetails
    {
        public List<DynamoGraphItem> Inputs { get; set; } = new List<DynamoGraphItem>();
        public List<DynamoGraphItem> Outputs { get; set; } = new List<DynamoGraphItem>();
        public List<string> Dependencies { get; set; } = new List<string>();
        public bool HasSelectionInputs => Inputs.Any(i => i.IsSelection);
    }
}
