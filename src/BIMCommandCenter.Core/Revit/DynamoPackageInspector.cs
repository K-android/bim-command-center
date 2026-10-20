using System;
using System.Collections.Generic;
using System.Globalization;
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
        /// Supports both Dynamo Player schema ("Inputs": [...]) and standard Dynamo node schema ("IsInput": true,
        /// "IsSetAsInput": true, and interactive Selection/Slider/Dropdown/Input nodes).
        /// </summary>
        public static DynamoGraphDetails InspectGraph(string dynPath)
        {
            var details = new DynamoGraphDetails();
            if (string.IsNullOrWhiteSpace(dynPath) || !File.Exists(dynPath)) return details;

            try
            {
                string json = File.ReadAllText(dynPath);
                var dyn = Json.AsObject(Json.Parse(json));
                if (dyn == null) return details;

                // 1. Extract Inputs array objects (Dynamo Player schema)
                if (dyn.TryGetValue("Inputs", out object inpObj) && inpObj is List<object> inpList)
                {
                    foreach (var item in inpList)
                    {
                        if (!(item is Dictionary<string, object> dict)) continue;

                        string id = dict.Str("Id");
                        string name = dict.Str("Name");
                        string type = dict.Str("Type");
                        string val = dict.Str("Value");
                        string desc = dict.Str("Description");

                        string typeLower = (type ?? "").ToLowerInvariant();
                        bool isSel = typeLower.Contains("select") ||
                                     name.IndexOf("select model element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("select element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("select face", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("selection", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (isSel) typeLower = "selection";

                        double? minVal = null, maxVal = null, stepVal = null;
                        if (double.TryParse(dict.Str("MinimumValue"), NumberStyles.Any, CultureInfo.InvariantCulture, out double min)) minVal = min;
                        if (double.TryParse(dict.Str("MaximumValue"), NumberStyles.Any, CultureInfo.InvariantCulture, out double max)) maxVal = max;
                        if (double.TryParse(dict.Str("StepValue"), NumberStyles.Any, CultureInfo.InvariantCulture, out double stp)) stepVal = stp;

                        var optionsList = new List<string>();
                        if (dict.TryGetValue("Options", out object optObj) && optObj is List<object> oList)
                        {
                            foreach (var o in oList) if (o != null) optionsList.Add(o.ToString());
                        }
                        else if (dict.TryGetValue("Items", out object itmObj) && itmObj is List<object> iList)
                        {
                            foreach (var i in iList) if (i != null) optionsList.Add(i.ToString());
                        }

                        if (!string.IsNullOrWhiteSpace(name) || !string.IsNullOrWhiteSpace(id))
                        {
                            details.Inputs.Add(new DynamoGraphItem
                            {
                                Id = string.IsNullOrWhiteSpace(id) ? name : id,
                                Name = string.IsNullOrWhiteSpace(name) ? "Parameter" : name,
                                Type = string.IsNullOrWhiteSpace(typeLower) ? "string" : typeLower,
                                Value = val ?? "",
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

                // 2. Scan standard Nodes array for nodes with "IsSetAsInput": true, "IsInput": true,
                // OR candidate selection, slider, dropdown, and basic input nodes.
                // This ensures graphs show inputs in the mini player even when the user never configured Dynamo Player!
                if (dyn.TryGetValue("Nodes", out object nodesObj) && nodesObj is List<object> nodeList)
                {
                    foreach (var n in nodeList)
                    {
                        if (!(n is Dictionary<string, object> node)) continue;

                        string id = node.Str("Id");
                        string name = node.Str("Name");
                        string concreteType = node.Str("ConcreteType") ?? "";
                        string desc = node.Str("Description");

                        bool isSetAsInput = node.Bool("IsSetAsInput") || node.Bool("IsInput");

                        bool isSelNode = concreteType.IndexOf("Selection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         concreteType.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         name.IndexOf("Select Model Element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         name.IndexOf("Select Element", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         name.IndexOf("Select Face", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         name.IndexOf("Selection", StringComparison.OrdinalIgnoreCase) >= 0;

                        bool isSliderNode = concreteType.IndexOf("slider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            name.IndexOf("slider", StringComparison.OrdinalIgnoreCase) >= 0;

                        bool isDropdownNode = concreteType.IndexOf("Categories", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              concreteType.IndexOf("FamilyTypes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              concreteType.IndexOf("Levels", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              concreteType.IndexOf("Phases", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              concreteType.IndexOf("Views", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              concreteType.IndexOf("Sheets", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              concreteType.IndexOf("WallTypes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                              name.Equals("Categories", StringComparison.OrdinalIgnoreCase) ||
                                              name.Equals("Family Types", StringComparison.OrdinalIgnoreCase) ||
                                              name.Equals("Levels", StringComparison.OrdinalIgnoreCase);

                        bool isBasicInputNode = concreteType.IndexOf("StringInput", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                concreteType.IndexOf("DoubleInput", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                concreteType.IndexOf("IntegerInput", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                concreteType.IndexOf("BoolSelector", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                concreteType.IndexOf("Filename", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                concreteType.IndexOf("Directory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                                name.Equals("String", StringComparison.OrdinalIgnoreCase) ||
                                                name.Equals("Number", StringComparison.OrdinalIgnoreCase) ||
                                                name.Equals("Integer", StringComparison.OrdinalIgnoreCase) ||
                                                name.Equals("Boolean", StringComparison.OrdinalIgnoreCase) ||
                                                name.Equals("File Path", StringComparison.OrdinalIgnoreCase) ||
                                                name.Equals("Directory Path", StringComparison.OrdinalIgnoreCase);

                        bool isSymbolNode = concreteType.IndexOf("Symbol", StringComparison.OrdinalIgnoreCase) >= 0;

                        // Include if explicitly marked as input, OR recognized as interactive / parameter input node
                        if (!isSetAsInput && !isSelNode && !isSliderNode && !isDropdownNode && !isBasicInputNode && !isSymbolNode)
                        {
                            continue;
                        }

                        // Skip if already captured in details.Inputs by Id
                        if (!string.IsNullOrEmpty(id) && details.Inputs.Any(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                        {
                            continue;
                        }

                        string type = "string";
                        bool isSel = false;
                        double? minVal = null, maxVal = null, stepVal = null;
                        var optionsList = new List<string>();
                        string val = node.Str("InputValue");

                        if (isSelNode)
                        {
                            type = "selection";
                            isSel = true;
                            if (string.IsNullOrWhiteSpace(name)) name = "Select Model Elements";
                        }
                        else if (isSliderNode)
                        {
                            type = "slider";
                            if (double.TryParse(node.Str("Min"), NumberStyles.Any, CultureInfo.InvariantCulture, out double nMin)) minVal = nMin;
                            if (double.TryParse(node.Str("Max"), NumberStyles.Any, CultureInfo.InvariantCulture, out double nMax)) maxVal = nMax;
                            if (double.TryParse(node.Str("Step"), NumberStyles.Any, CultureInfo.InvariantCulture, out double nStep)) stepVal = nStep;
                            if (string.IsNullOrEmpty(val)) val = node.Str("Number");
                        }
                        else if (concreteType.IndexOf("number", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 concreteType.IndexOf("double", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 concreteType.IndexOf("integer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.Equals("Number", StringComparison.OrdinalIgnoreCase) ||
                                 name.Equals("Integer", StringComparison.OrdinalIgnoreCase))
                        {
                            type = "number";
                            if (string.IsNullOrEmpty(val)) val = node.Str("Number");
                        }
                        else if (concreteType.IndexOf("bool", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
                        {
                            type = "boolean";
                        }
                        else if (concreteType.IndexOf("file", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 concreteType.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.Equals("File Path", StringComparison.OrdinalIgnoreCase))
                        {
                            type = "filepath";
                        }
                        else if (concreteType.IndexOf("directory", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.Equals("Directory Path", StringComparison.OrdinalIgnoreCase))
                        {
                            type = "directorypath";
                        }
                        else if (isDropdownNode)
                        {
                            type = "dropdown";
                            string selStr = node.Str("SelectedString");
                            if (!string.IsNullOrWhiteSpace(selStr)) val = selStr;

                            if (concreteType.IndexOf("Categories", StringComparison.OrdinalIgnoreCase) >= 0 || name.Equals("Categories", StringComparison.OrdinalIgnoreCase))
                            {
                                optionsList = new List<string>
                                {
                                    "OST_Doors", "OST_Windows", "OST_Walls", "OST_Rooms", "OST_Floors",
                                    "OST_Ceilings", "OST_Columns", "OST_StructuralColumns", "OST_StructuralFraming",
                                    "OST_DuctCurves", "OST_PipeCurves", "OST_Furniture", "OST_GenericModel", "OST_Views"
                                };
                                if (!string.IsNullOrEmpty(val) && !optionsList.Contains(val)) optionsList.Insert(0, val);
                            }
                        }
                        else if (isSymbolNode)
                        {
                            if (node.TryGetValue("Parameter", out object paramObj) && paramObj is Dictionary<string, object> paramDict)
                            {
                                string pName = paramDict.Str("Name");
                                if (!string.IsNullOrEmpty(pName)) name = pName;
                                val = paramDict.Str("DefaultValue");
                            }
                        }

                        if (string.IsNullOrWhiteSpace(name))
                        {
                            name = !string.IsNullOrWhiteSpace(concreteType) ? concreteType.Split(',')[0].Split('.').Last() : "Input";
                        }

                        details.Inputs.Add(new DynamoGraphItem
                        {
                            Id = string.IsNullOrWhiteSpace(id) ? name : id,
                            Name = name,
                            Type = type,
                            Value = val ?? "",
                            Description = isSel ? "Select model elements in active Revit view" : (string.IsNullOrWhiteSpace(desc) ? "Dynamo node input" : desc),
                            IsSelection = isSel,
                            Min = minVal,
                            Max = maxVal,
                            Step = stepVal,
                            Options = optionsList
                        });
                    }
                }

                // 3. Extract Outputs array objects
                if (dyn.TryGetValue("Outputs", out object outObj) && outObj is List<object> outList)
                {
                    foreach (var o in outList)
                    {
                        if (o is Dictionary<string, object> oDict)
                        {
                            string oName = oDict.Str("Name");
                            string oDesc = oDict.Str("Description");
                            if (!string.IsNullOrWhiteSpace(oName))
                            {
                                details.Outputs.Add(new DynamoGraphItem
                                {
                                    Name = oName,
                                    Description = oDesc
                                });
                            }
                        }
                    }
                }

                // 4. Extract Dependencies from NodeLibraryDependencies
                if (dyn.TryGetValue("NodeLibraryDependencies", out object depObj) && depObj is List<object> depList)
                {
                    foreach (var d in depList)
                    {
                        if (d is Dictionary<string, object> dDict)
                        {
                            string depName = dDict.Str("Name");
                            if (!string.IsNullOrWhiteSpace(depName) && !details.Dependencies.Contains(depName))
                                details.Dependencies.Add(depName);
                        }
                    }
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
                var dyn = Json.AsObject(Json.Parse(json));
                if (dyn == null) return dynPath;

                // 1. Update Inputs array (Dynamo Player schema)
                if (dyn.TryGetValue("Inputs", out object inpObj) && inpObj is List<object> inpList)
                {
                    foreach (var inpItem in inpList)
                    {
                        if (!(inpItem is Dictionary<string, object> inp)) continue;
                        string id = inp.Str("Id");
                        string name = inp.Str("Name");

                        string overrideVal = null;
                        if (!string.IsNullOrEmpty(id) && inputOverrides.TryGetValue(id, out string ov1)) overrideVal = ov1;
                        else if (!string.IsNullOrEmpty(name) && inputOverrides.TryGetValue(name, out string ov2)) overrideVal = ov2;

                        if (overrideVal != null)
                        {
                            inp["Value"] = overrideVal;
                        }
                    }
                }

                // 2. Update Nodes array (standard Dynamo schema)
                if (dyn.TryGetValue("Nodes", out object nodesObj) && nodesObj is List<object> nodeList)
                {
                    foreach (var nodeItem in nodeList)
                    {
                        if (!(nodeItem is Dictionary<string, object> node)) continue;
                        string id = node.Str("Id");
                        string name = node.Str("Name");
                        string concreteType = node.Str("ConcreteType") ?? "";

                        string overrideVal = null;
                        if (!string.IsNullOrEmpty(id) && inputOverrides.TryGetValue(id, out string ov1)) overrideVal = ov1;
                        else if (!string.IsNullOrEmpty(name) && inputOverrides.TryGetValue(name, out string ov2)) overrideVal = ov2;

                        if (overrideVal == null) continue;

                        bool isSel = concreteType.IndexOf("Selection", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     concreteType.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     name.IndexOf("Select", StringComparison.OrdinalIgnoreCase) >= 0;

                        if (isSel)
                        {
                            var uids = overrideVal.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                                                  .Select(s => s.Trim().Trim('"', '\'', '[', ']', ' '))
                                                  .Where(s => !string.IsNullOrEmpty(s))
                                                  .Cast<object>()
                                                  .ToList();
                            if (uids.Count > 0)
                            {
                                node["selectionIdentifier"] = uids;
                                node["InputValue"] = uids[0].ToString();
                            }
                        }
                        else if (concreteType.IndexOf("slider", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.IndexOf("slider", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            if (double.TryParse(overrideVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double dVal))
                            {
                                node["Number"] = dVal;
                                node["InputValue"] = dVal;
                            }
                            else
                            {
                                node["Number"] = overrideVal;
                                node["InputValue"] = overrideVal;
                            }
                        }
                        else if (concreteType.IndexOf("bool", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 name.Equals("Boolean", StringComparison.OrdinalIgnoreCase))
                        {
                            bool bVal = overrideVal.Equals("true", StringComparison.OrdinalIgnoreCase) || overrideVal == "1";
                            node["InputValue"] = bVal;
                        }
                        else if (concreteType.IndexOf("Categories", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 concreteType.IndexOf("FamilyTypes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 concreteType.IndexOf("Levels", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 concreteType.IndexOf("Phases", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            node["SelectedString"] = overrideVal;
                            node["InputValue"] = overrideVal;
                        }
                        else
                        {
                            if (double.TryParse(overrideVal, NumberStyles.Any, CultureInfo.InvariantCulture, out double dVal))
                            {
                                node["InputValue"] = dVal;
                            }
                            else
                            {
                                node["InputValue"] = overrideVal;
                            }
                        }
                    }
                }

                string newJson = Json.Stringify(dyn);
                string tempDir = Path.Combine(Path.GetTempPath(), "BIMCommandCenter", "DynamoExec");
                Directory.CreateDirectory(tempDir);
                string tempPath = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(dynPath) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".dyn");
                File.WriteAllText(tempPath, newJson);
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
