using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

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
        /// </summary>
        public static DynamoGraphDetails InspectGraph(string dynPath)
        {
            var details = new DynamoGraphDetails();
            if (string.IsNullOrWhiteSpace(dynPath) || !File.Exists(dynPath)) return details;

            try
            {
                string json = File.ReadAllText(dynPath);

                // Extract Inputs array objects
                var inputsMatch = Regex.Match(json, @"""Inputs""\s*:\s*\[(.*?)\]", RegexOptions.Singleline);
                if (inputsMatch.Success)
                {
                    var itemMatches = Regex.Matches(inputsMatch.Groups[1].Value, @"\{([^{}]*)\}");
                    foreach (Match m in itemMatches)
                    {
                        string block = m.Groups[1].Value;
                        string name = Regex.Match(block, @"""Name""\s*:\s*""([^""]*)""").Groups[1].Value;
                        string type = Regex.Match(block, @"""Type""\s*:\s*""([^""]*)""").Groups[1].Value;
                        string val = Regex.Match(block, @"""Value""\s*:\s*""([^""]*)""").Groups[1].Value;
                        string desc = Regex.Match(block, @"""Description""\s*:\s*""([^""]*)""").Groups[1].Value;

                        if (!string.IsNullOrWhiteSpace(name))
                        {
                            details.Inputs.Add(new DynamoGraphItem
                            {
                                Name = name,
                                Type = string.IsNullOrWhiteSpace(type) ? "var" : type,
                                Value = val,
                                Description = desc
                            });
                        }
                    }
                }

                // Extract Outputs array objects
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

                // Extract Dependencies from NodeLibraryDependencies
                var depMatches = Regex.Matches(json, @"""Name""\s*:\s*""([^""]*)""\s*,\s*""Version""");
                foreach (Match dm in depMatches)
                {
                    string dep = dm.Groups[1].Value;
                    if (!string.IsNullOrWhiteSpace(dep) && !details.Dependencies.Contains(dep))
                        details.Dependencies.Add(dep);
                }
            }
            catch { }

            return details;
        }
    }

    public class DynamoGraphItem
    {
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public class DynamoGraphDetails
    {
        public List<DynamoGraphItem> Inputs { get; set; } = new List<DynamoGraphItem>();
        public List<DynamoGraphItem> Outputs { get; set; } = new List<DynamoGraphItem>();
        public List<string> Dependencies { get; set; } = new List<string>();
    }
}
