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
        /// Supports Dynamo Player schema ("Inputs": [...]), Data-Shapes / custom UI nodes (UI.dropdown, UI.textbox,
        /// UI.Slider, UI.RadioButtons, UI.FilePath, UI.SelectModelElements, UI.Inputs, etc.), and standard Dynamo nodes.
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

                // 2. Index all Nodes and Connectors in the graph
                var nodeList = (dyn.TryGetValue("Nodes", out object nObj) && nObj is List<object> nList) ? nList : new List<object>();
                var connectorList = (dyn.TryGetValue("Connectors", out object cObj) && cObj is List<object> cList) ? cList : new List<object>();

                var nodeById = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                var portToNode = new Dictionary<string, (Dictionary<string, object> node, Dictionary<string, object> port)>(StringComparer.OrdinalIgnoreCase);
                var connEndToStart = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var n in nodeList)
                {
                    if (n is Dictionary<string, object> nodeDict)
                    {
                        string nId = nodeDict.Str("Id");
                        if (!string.IsNullOrEmpty(nId))
                            nodeById[nId] = nodeDict;

                        if (nodeDict.TryGetValue("Inputs", out object inPorts) && inPorts is List<object> ipList)
                        {
                            foreach (var p in ipList)
                            {
                                if (p is Dictionary<string, object> pDict)
                                {
                                    string pId = pDict.Str("Id");
                                    if (!string.IsNullOrEmpty(pId)) portToNode[pId] = (nodeDict, pDict);
                                }
                            }
                        }

                        if (nodeDict.TryGetValue("Outputs", out object outPorts) && outPorts is List<object> opList)
                        {
                            foreach (var p in opList)
                            {
                                if (p is Dictionary<string, object> pDict)
                                {
                                    string pId = pDict.Str("Id");
                                    if (!string.IsNullOrEmpty(pId)) portToNode[pId] = (nodeDict, pDict);
                                }
                            }
                        }
                    }
                }

                foreach (var c in connectorList)
                {
                    if (c is Dictionary<string, object> cDict)
                    {
                        string startId = cDict.Str("Start");
                        string endId = cDict.Str("End");
                        if (!string.IsNullOrEmpty(startId) && !string.IsNullOrEmpty(endId))
                        {
                            connEndToStart[endId] = startId;
                        }
                    }
                }

                // 3. Scan standard and Data-Shapes / custom UI nodes
                foreach (var n in nodeList)
                {
                    if (!(n is Dictionary<string, object> node)) continue;

                    string id = node.Str("Id");
                    string name = node.Str("Name");
                    string concreteType = node.Str("ConcreteType") ?? "";
                    string funcSig = node.Str("FunctionSignature") ?? "";
                    string funcSym = node.Str("FunctionSymbol") ?? "";
                    string desc = node.Str("Description");

                    bool isSetAsInput = node.Bool("IsSetAsInput") || node.Bool("IsInput");

                    // Match Data-Shapes / UI nodes
                    bool isUiDropdown = MatchesPattern(name, funcSig, funcSym, concreteType, "dropdown") ||
                                        MatchesPattern(name, funcSig, funcSym, concreteType, "ui.dropdown") ||
                                        MatchesPattern(name, funcSig, funcSym, concreteType, "radiobutton") ||
                                        MatchesPattern(name, funcSig, funcSym, concreteType, "combobox") ||
                                        MatchesPattern(name, funcSig, funcSym, concreteType, "categories") ||
                                        MatchesPattern(name, funcSig, funcSym, concreteType, "familytypes") ||
                                        MatchesPattern(name, funcSig, funcSym, concreteType, "levels") ||
                                        MatchesPattern(name, funcSig, funcSym, concreteType, "phases") ||
                                        MatchesPattern(name, funcSig, funcSym, concreteType, "views") ||
                                        MatchesPattern(name, funcSig, funcSym, concreteType, "sheets") ||
                                        MatchesPattern(name, funcSig, funcSym, concreteType, "walltypes");

                    bool isUiTextbox = MatchesPattern(name, funcSig, funcSym, concreteType, "textbox") ||
                                       MatchesPattern(name, funcSig, funcSym, concreteType, "text box") ||
                                       MatchesPattern(name, funcSig, funcSym, concreteType, "ui.textbox") ||
                                       MatchesPattern(name, funcSig, funcSym, concreteType, "stringinput") ||
                                       (name.Equals("String", StringComparison.OrdinalIgnoreCase) && isSetAsInput);

                    bool isUiSlider = MatchesPattern(name, funcSig, funcSym, concreteType, "slider") ||
                                      MatchesPattern(name, funcSig, funcSym, concreteType, "ui.slider");

                    bool isUiSelection = MatchesPattern(name, funcSig, funcSym, concreteType, "selectmodelelement") ||
                                         MatchesPattern(name, funcSig, funcSym, concreteType, "select element") ||
                                         MatchesPattern(name, funcSig, funcSym, concreteType, "selection") ||
                                         MatchesPattern(name, funcSig, funcSym, concreteType, "selectface") ||
                                         MatchesPattern(name, funcSig, funcSym, concreteType, "select face") ||
                                         MatchesPattern(name, funcSig, funcSym, concreteType, "ui.select");

                    bool isUiFile = MatchesPattern(name, funcSig, funcSym, concreteType, "filepath") ||
                                    MatchesPattern(name, funcSig, funcSym, concreteType, "file path") ||
                                    MatchesPattern(name, funcSig, funcSym, concreteType, "ui.filepath") ||
                                    MatchesPattern(name, funcSig, funcSym, concreteType, "filename");

                    bool isUiDir = MatchesPattern(name, funcSig, funcSym, concreteType, "directorypath") ||
                                   MatchesPattern(name, funcSig, funcSym, concreteType, "directory path") ||
                                   MatchesPattern(name, funcSig, funcSym, concreteType, "ui.directorypath") ||
                                   MatchesPattern(name, funcSig, funcSym, concreteType, "directory");

                    bool isUiBool = MatchesPattern(name, funcSig, funcSym, concreteType, "boolselector") ||
                                    MatchesPattern(name, funcSig, funcSym, concreteType, "ui.toggle") ||
                                    MatchesPattern(name, funcSig, funcSym, concreteType, "boolean");

                    bool isUiNumber = MatchesPattern(name, funcSig, funcSym, concreteType, "doubleinput") ||
                                      MatchesPattern(name, funcSig, funcSym, concreteType, "integerinput") ||
                                      (name.Equals("Number", StringComparison.OrdinalIgnoreCase) && isSetAsInput) ||
                                      (name.Equals("Integer", StringComparison.OrdinalIgnoreCase) && isSetAsInput);

                    bool isGenericUiInput = (name.StartsWith("UI.", StringComparison.OrdinalIgnoreCase) ||
                                             funcSig.StartsWith("Data-Shapes.UI.", StringComparison.OrdinalIgnoreCase)) &&
                                            name.IndexOf("MultipleInputForm", StringComparison.OrdinalIgnoreCase) < 0;

                    if (!isSetAsInput && !isUiDropdown && !isUiTextbox && !isUiSlider &&
                        !isUiSelection && !isUiFile && !isUiDir && !isUiBool && !isUiNumber && !isGenericUiInput)
                    {
                        continue;
                    }

                    // Skip if already captured
                    if (!string.IsNullOrEmpty(id) && details.Inputs.Any(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    // Resolve input ports of this node for connected source values
                    var nodeInPorts = (node.TryGetValue("Inputs", out object np) && np is List<object> npl)
                        ? npl.OfType<Dictionary<string, object>>().ToList()
                        : new List<Dictionary<string, object>>();

                    // 1. Resolve Display Name (Port 0 or connected String/CodeBlock)
                    string resolvedName = name;
                    if (nodeInPorts.Count > 0)
                    {
                        string p0Id = nodeInPorts[0].Str("Id");
                        string connectedLabel = GetConnectedString(p0Id, connEndToStart, portToNode);
                        if (!string.IsNullOrWhiteSpace(connectedLabel))
                        {
                            resolvedName = connectedLabel;
                        }
                    }

                    // Clean generic UI names if not renamed
                    if (string.IsNullOrWhiteSpace(resolvedName) ||
                        resolvedName.Equals("UI.TextBox Data", StringComparison.OrdinalIgnoreCase) ||
                        resolvedName.Equals("UI.textbox", StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedName = "Text Input";
                    }
                    else if (resolvedName.Equals("UI.Dropdown Data", StringComparison.OrdinalIgnoreCase) ||
                             resolvedName.Equals("UI.dropdown", StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedName = "Select Option";
                    }
                    else if (resolvedName.Equals("UI.Slider Data", StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedName = "Slider Value";
                    }
                    else if (resolvedName.Equals("UI.SelectModelElements Data", StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedName = "Select Model Elements";
                    }

                    // 2. Resolve Control Type & Values
                    string type = "string";
                    bool isSel = false;
                    double? minVal = null, maxVal = null, stepVal = null;
                    var optionsList = new List<string>();
                    string val = node.Str("InputValue") ?? node.Str("Value") ?? "";

                    if (isUiSelection)
                    {
                        type = "selection";
                        isSel = true;
                    }
                    else if (isUiDropdown)
                    {
                        type = "dropdown";
                        string selStr = node.Str("SelectedString");
                        if (!string.IsNullOrWhiteSpace(selStr)) val = selStr;

                        // Try extracting options from Port 1 (keys) or Port 2 (values)
                        if (nodeInPorts.Count > 1)
                        {
                            var keysOpts = GetConnectedStringList(nodeInPorts[1].Str("Id"), connEndToStart, portToNode);
                            if (keysOpts.Count > 0) optionsList.AddRange(keysOpts);
                        }
                        if (optionsList.Count == 0 && nodeInPorts.Count > 2)
                        {
                            var valsOpts = GetConnectedStringList(nodeInPorts[2].Str("Id"), connEndToStart, portToNode);
                            if (valsOpts.Count > 0) optionsList.AddRange(valsOpts);
                        }

                        // Provide default options for Revit Categories or common selections
                        if (optionsList.Count == 0)
                        {
                            if (MatchesPattern(name, funcSig, funcSym, concreteType, "categories") ||
                                resolvedName.IndexOf("category", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                optionsList = new List<string>
                                {
                                    "OST_Doors", "OST_Windows", "OST_Walls", "OST_Rooms", "OST_Floors",
                                    "OST_Ceilings", "OST_Columns", "OST_StructuralColumns", "OST_StructuralFraming",
                                    "OST_DuctCurves", "OST_PipeCurves", "OST_Furniture", "OST_GenericModel", "OST_Views"
                                };
                            }
                            else if (MatchesPattern(name, funcSig, funcSym, concreteType, "levels") ||
                                     resolvedName.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                optionsList = new List<string> { "Level 1", "Level 2", "Level 3", "Level 4" };
                            }
                            else if (MatchesPattern(name, funcSig, funcSym, concreteType, "phases"))
                            {
                                optionsList = new List<string> { "Existing", "New Construction" };
                            }
                        }

                        if (!string.IsNullOrEmpty(val) && !optionsList.Contains(val, StringComparer.OrdinalIgnoreCase))
                        {
                            optionsList.Insert(0, val);
                        }
                        else if (string.IsNullOrEmpty(val) && optionsList.Count > 0)
                        {
                            val = optionsList[0];
                        }
                    }
                    else if (isUiSlider)
                    {
                        type = "slider";
                        if (double.TryParse(node.Str("Min"), NumberStyles.Any, CultureInfo.InvariantCulture, out double nMin)) minVal = nMin;
                        if (double.TryParse(node.Str("Max"), NumberStyles.Any, CultureInfo.InvariantCulture, out double nMax)) maxVal = nMax;
                        if (double.TryParse(node.Str("Step"), NumberStyles.Any, CultureInfo.InvariantCulture, out double nStep)) stepVal = nStep;

                        // Port 1: min, Port 2: max, Port 3: step, Port 4: default
                        if (!minVal.HasValue && nodeInPorts.Count > 1)
                        {
                            string sMin = GetConnectedString(nodeInPorts[1].Str("Id"), connEndToStart, portToNode);
                            if (double.TryParse(sMin, NumberStyles.Any, CultureInfo.InvariantCulture, out double pm)) minVal = pm;
                        }
                        if (!maxVal.HasValue && nodeInPorts.Count > 2)
                        {
                            string sMax = GetConnectedString(nodeInPorts[2].Str("Id"), connEndToStart, portToNode);
                            if (double.TryParse(sMax, NumberStyles.Any, CultureInfo.InvariantCulture, out double pm)) maxVal = pm;
                        }
                        if (!stepVal.HasValue && nodeInPorts.Count > 3)
                        {
                            string sStep = GetConnectedString(nodeInPorts[3].Str("Id"), connEndToStart, portToNode);
                            if (double.TryParse(sStep, NumberStyles.Any, CultureInfo.InvariantCulture, out double pm)) stepVal = pm;
                        }

                        if (string.IsNullOrEmpty(val)) val = node.Str("Number") ?? (minVal?.ToString() ?? "0");
                    }
                    else if (isUiNumber)
                    {
                        type = "number";
                        if (string.IsNullOrEmpty(val)) val = node.Str("Number") ?? "0";
                    }
                    else if (isUiBool)
                    {
                        type = "boolean";
                        if (string.IsNullOrEmpty(val)) val = "false";
                    }
                    else if (isUiFile)
                    {
                        type = "filepath";
                    }
                    else if (isUiDir)
                    {
                        type = "directorypath";
                    }
                    else
                    {
                        type = "string";
                        // If textbox has Port 1 (default text), extract connected value
                        if (nodeInPorts.Count > 1)
                        {
                            string defTxt = GetConnectedString(nodeInPorts[1].Str("Id"), connEndToStart, portToNode);
                            if (!string.IsNullOrWhiteSpace(defTxt)) val = defTxt;
                        }
                    }

                    details.Inputs.Add(new DynamoGraphItem
                    {
                        Id = string.IsNullOrWhiteSpace(id) ? resolvedName : id,
                        Name = resolvedName,
                        Type = type,
                        Value = val,
                        Description = isSel ? "Select model elements in active Revit view" : (string.IsNullOrWhiteSpace(desc) ? "Parameter input" : desc),
                        IsSelection = isSel,
                        Min = minVal,
                        Max = maxVal,
                        Step = stepVal,
                        Options = optionsList.Distinct().ToList()
                    });
                }

                // 4. Extract Outputs array objects
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

                // 5. Extract Dependencies from NodeLibraryDependencies
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

        private static bool MatchesPattern(string name, string funcSig, string funcSym, string concreteType, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            return (name != null && name.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (funcSig != null && funcSig.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (funcSym != null && funcSym.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) ||
                   (concreteType != null && concreteType.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private static string GetConnectedString(string portId, Dictionary<string, string> connEndToStart, Dictionary<string, (Dictionary<string, object> node, Dictionary<string, object> port)> portToNode)
        {
            if (string.IsNullOrEmpty(portId)) return null;
            if (!connEndToStart.TryGetValue(portId, out string startPortId)) return null;
            if (!portToNode.TryGetValue(startPortId, out var pair)) return null;

            var sourceNode = pair.node;
            string concreteType = sourceNode.Str("ConcreteType") ?? "";
            string name = sourceNode.Str("Name") ?? "";

            // String node
            if (concreteType.IndexOf("StringInput", StringComparison.OrdinalIgnoreCase) >= 0 || name.Equals("String", StringComparison.OrdinalIgnoreCase))
            {
                return sourceNode.Str("InputValue") ?? sourceNode.Str("Value");
            }

            // Code Block
            if (concreteType.IndexOf("CodeBlock", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string code = sourceNode.Str("Code") ?? "";
                var m = Regex.Match(code, "\"([^\"]*)\"");
                if (m.Success) return m.Groups[1].Value;
                return code.Trim(';', ' ', '\r', '\n');
            }

            // Number node
            if (sourceNode.ContainsKey("Number")) return sourceNode.Str("Number");
            if (sourceNode.ContainsKey("InputValue")) return sourceNode.Str("InputValue");

            return null;
        }

        private static List<string> GetConnectedStringList(string portId, Dictionary<string, string> connEndToStart, Dictionary<string, (Dictionary<string, object> node, Dictionary<string, object> port)> portToNode)
        {
            var results = new List<string>();
            if (string.IsNullOrEmpty(portId)) return results;
            if (!connEndToStart.TryGetValue(portId, out string startPortId)) return results;
            if (!portToNode.TryGetValue(startPortId, out var pair)) return results;

            var sourceNode = pair.node;
            string concreteType = sourceNode.Str("ConcreteType") ?? "";

            // CodeBlock: parse all quoted strings
            if (concreteType.IndexOf("CodeBlock", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string code = sourceNode.Str("Code") ?? "";
                var matches = Regex.Matches(code, "\"([^\"]+)\"");
                foreach (Match m in matches)
                {
                    if (m.Success && !string.IsNullOrWhiteSpace(m.Groups[1].Value))
                        results.Add(m.Groups[1].Value);
                }
            }
            // List.Create: inspect each connected input
            else if (concreteType.IndexOf("CreateList", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     sourceNode.Str("Name").IndexOf("List.Create", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (sourceNode.TryGetValue("Inputs", out object inPorts) && inPorts is List<object> ipList)
                {
                    foreach (var ip in ipList.OfType<Dictionary<string, object>>())
                    {
                        string str = GetConnectedString(ip.Str("Id"), connEndToStart, portToNode);
                        if (!string.IsNullOrWhiteSpace(str)) results.Add(str);
                    }
                }
            }
            else
            {
                string single = GetConnectedString(portId, connEndToStart, portToNode);
                if (!string.IsNullOrWhiteSpace(single)) results.Add(single);
            }

            return results;
        }

        /// <summary>
        /// Injects custom parameter values into a Dynamo .dyn file and saves a temporary copy
        /// for isolated headless execution without altering the central original file.
        /// Handles Data-Shapes UI nodes (UI.dropdown, UI.textbox, UI.MultipleInputForm ++),
        /// standard Dynamo inputs, and element selection IDs.
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

                // 2. Index nodes and connectors to allow injection into connected source nodes
                var nodeList = (dyn.TryGetValue("Nodes", out object nObj) && nObj is List<object> nList) ? nList : new List<object>();
                var connectorList = (dyn.TryGetValue("Connectors", out object cObj) && cObj is List<object> cList) ? cList : new List<object>();

                var nodeById = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                var portToNode = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                var connEndToStart = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var n in nodeList.OfType<Dictionary<string, object>>())
                {
                    string nId = n.Str("Id");
                    if (!string.IsNullOrEmpty(nId)) nodeById[nId] = n;

                    if (n.TryGetValue("Inputs", out object inPorts) && inPorts is List<object> ipList)
                    {
                        foreach (var p in ipList.OfType<Dictionary<string, object>>())
                        {
                            string pId = p.Str("Id");
                            if (!string.IsNullOrEmpty(pId)) portToNode[pId] = n;
                        }
                    }
                    if (n.TryGetValue("Outputs", out object outPorts) && outPorts is List<object> opList)
                    {
                        foreach (var p in opList.OfType<Dictionary<string, object>>())
                        {
                            string pId = p.Str("Id");
                            if (!string.IsNullOrEmpty(pId)) portToNode[pId] = n;
                        }
                    }
                }

                foreach (var c in connectorList.OfType<Dictionary<string, object>>())
                {
                    string s = c.Str("Start");
                    string e = c.Str("End");
                    if (!string.IsNullOrEmpty(s) && !string.IsNullOrEmpty(e))
                        connEndToStart[e] = s;
                }

                // 3. Update Nodes array (standard Dynamo schema & Data-Shapes nodes)
                var orderedOverrides = new List<string>();

                foreach (var nodeItem in nodeList)
                {
                    if (!(nodeItem is Dictionary<string, object> node)) continue;
                    string id = node.Str("Id");
                    string name = node.Str("Name");
                    string concreteType = node.Str("ConcreteType") ?? "";
                    string funcSig = node.Str("FunctionSignature") ?? "";

                    string overrideVal = null;
                    if (!string.IsNullOrEmpty(id) && inputOverrides.TryGetValue(id, out string ov1)) overrideVal = ov1;
                    else if (!string.IsNullOrEmpty(name) && inputOverrides.TryGetValue(name, out string ov2)) overrideVal = ov2;
                    else
                    {
                        // Check if any override key matches part of name or Id
                        foreach (var kvp in inputOverrides)
                        {
                            if (!string.IsNullOrEmpty(kvp.Key) &&
                                (name.IndexOf(kvp.Key, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 kvp.Key.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0))
                            {
                                overrideVal = kvp.Value;
                                break;
                            }
                        }
                    }

                    if (overrideVal == null) continue;
                    orderedOverrides.Add(overrideVal);

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
                    // Data-Shapes UI nodes (UI.TextBox Data, UI.Dropdown Data)
                    else if (name.IndexOf("UI.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             funcSig.IndexOf("Data-Shapes", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        node["InputValue"] = overrideVal;
                        node["Value"] = overrideVal;
                        node["SelectedString"] = overrideVal;

                        // Also update connected source node if Port 1 (default text / keys) is connected
                        if (node.TryGetValue("Inputs", out object inPorts) && inPorts is List<object> ipList && ipList.Count > 1)
                        {
                            if (ipList[1] is Dictionary<string, object> p1)
                            {
                                string p1Id = p1.Str("Id");
                                if (connEndToStart.TryGetValue(p1Id, out string startPortId) &&
                                    portToNode.TryGetValue(startPortId, out var srcNode))
                                {
                                    srcNode["InputValue"] = overrideVal;
                                    srcNode["Value"] = overrideVal;
                                    if (srcNode.Str("ConcreteType").IndexOf("CodeBlock", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        srcNode["Code"] = Json.Stringify(overrideVal) + ";";
                                    }
                                }
                            }
                        }
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
                            node["Value"] = overrideVal;
                        }
                    }
                }

                // 4. If Data-Shapes UI.MultipleInputForm ++ is present, bypass its UI popup during headless run!
                // Replace MultipleInputForm's values output connection with a CodeBlock outputting the collected values list.
                var formNode = nodeList.OfType<Dictionary<string, object>>()
                    .FirstOrDefault(n => n.Str("Name").IndexOf("MultipleInputForm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                         (n.Str("FunctionSignature") ?? "").IndexOf("MultipleInputForm", StringComparison.OrdinalIgnoreCase) >= 0);

                if (formNode != null && inputOverrides.Count > 0)
                {
                    // Disable form popup by setting toggle to false
                    formNode["InputValue"] = false;
                    if (formNode.TryGetValue("Inputs", out object formInputs) && formInputs is List<object> fList)
                    {
                        foreach (var fPort in fList.OfType<Dictionary<string, object>>())
                        {
                            if (fPort.Str("Name").IndexOf("toggle", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                string tPortId = fPort.Str("Id");
                                if (connEndToStart.TryGetValue(tPortId, out string tStartId) &&
                                    portToNode.TryGetValue(tStartId, out var boolNode))
                                {
                                    boolNode["InputValue"] = false;
                                }
                            }
                        }
                    }

                    // Find MultipleInputForm's "values" output port (port at index 0)
                    if (formNode.TryGetValue("Outputs", out object formOutputs) && formOutputs is List<object> foList && foList.Count > 0)
                    {
                        var valuesPort = foList[0] as Dictionary<string, object>;
                        string valuesPortId = valuesPort?.Str("Id");

                        if (!string.IsNullOrEmpty(valuesPortId))
                        {
                            // Build a replacement CodeBlock node that outputs user values as a list: { val0, val1, ... };
                            string cbNodeId = Guid.NewGuid().ToString("N");
                            string cbPortId = Guid.NewGuid().ToString("N");

                            var valList = orderedOverrides.Count > 0 ? orderedOverrides : inputOverrides.Values.ToList();
                            string codeContent = "{" + string.Join(", ", valList.Select(v => Json.Stringify(v))) + "};";

                            var codeBlockNode = new Dictionary<string, object>
                            {
                                ["ConcreteType"] = "Dynamo.Graph.Nodes.CodeBlockNodeModel, DynamoCore",
                                ["NodeType"] = "CodeBlockNode",
                                ["Code"] = codeContent,
                                ["Id"] = cbNodeId,
                                ["Inputs"] = new List<object>(),
                                ["Outputs"] = new List<object>
                                {
                                    new Dictionary<string, object>
                                    {
                                        ["Id"] = cbPortId,
                                        ["Name"] = "",
                                        ["Type"] = "var[]..[]"
                                    }
                                }
                            };

                            nodeList.Add(codeBlockNode);

                            // Rewire any connector reading from MultipleInputForm to read from the CodeBlock instead
                            foreach (var c in connectorList.OfType<Dictionary<string, object>>())
                            {
                                if (string.Equals(c.Str("Start"), valuesPortId, StringComparison.OrdinalIgnoreCase))
                                {
                                    c["Start"] = cbPortId;
                                }
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
