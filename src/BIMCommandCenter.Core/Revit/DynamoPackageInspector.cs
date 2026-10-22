using System;
using System.Collections;
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
        /// Supports Dynamo Player schema, Data-Shapes UI components (UI.DropDown Data, UI.TextBox Data,
        /// UI.Slider Data, UI.SelectModelElements Data, UI.MultipleInputForm ++), and standard Dynamo nodes.
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

                // 2. Index View.NodeViews (crucial: Custom Nodes like Data-Shapes store visual names and IsSetAsInput here!)
                var nodeViewsById = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                if (dyn.TryGetValue("View", out object viewObj) && viewObj is Dictionary<string, object> viewDict)
                {
                    if (viewDict.TryGetValue("NodeViews", out object nvObj) && nvObj is List<object> nvList)
                    {
                        foreach (var nv in nvList.OfType<Dictionary<string, object>>())
                        {
                            string nvId = nv.Str("Id");
                            if (!string.IsNullOrEmpty(nvId)) nodeViewsById[nvId] = nv;
                        }
                    }
                }

                // 3. Index NodeLibraryDependencies (finds which node IDs belong to Data-Shapes package)
                var dataShapesNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (dyn.TryGetValue("NodeLibraryDependencies", out object depObj) && depObj is List<object> depList)
                {
                    foreach (var d in depList.OfType<Dictionary<string, object>>())
                    {
                        string depName = d.Str("Name");
                        if (!string.IsNullOrEmpty(depName))
                        {
                            if (!details.Dependencies.Contains(depName)) details.Dependencies.Add(depName);
                            if (depName.IndexOf("Data-Shapes", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                if (d.TryGetValue("Nodes", out object nIds) && nIds is List<object> idList)
                                {
                                    foreach (var item in idList)
                                        if (item != null) dataShapesNodeIds.Add(item.ToString());
                                }
                            }
                        }
                    }
                }

                // 4. Index all Nodes and Connectors in the graph
                var nodeList = (dyn.TryGetValue("Nodes", out object nObj) && nObj is List<object> nList) ? nList : new List<object>();
                var connectorList = (dyn.TryGetValue("Connectors", out object cObj) && cObj is List<object> cList) ? cList : new List<object>();

                var nodeById = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
                var portToNode = new Dictionary<string, (Dictionary<string, object> node, Dictionary<string, object> port)>(StringComparer.OrdinalIgnoreCase);
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
                            if (!string.IsNullOrEmpty(pId)) portToNode[pId] = (n, p);
                        }
                    }
                    if (n.TryGetValue("Outputs", out object outPorts) && outPorts is List<object> opList)
                    {
                        foreach (var p in opList.OfType<Dictionary<string, object>>())
                        {
                            string pId = p.Str("Id");
                            if (!string.IsNullOrEmpty(pId)) portToNode[pId] = (n, p);
                        }
                    }
                }

                foreach (var c in connectorList.OfType<Dictionary<string, object>>())
                {
                    string startId = c.Str("Start");
                    string endId = c.Str("End");
                    if (!string.IsNullOrEmpty(startId) && !string.IsNullOrEmpty(endId))
                    {
                        connEndToStart[endId] = startId;
                    }
                }

                // 5. Track feeder nodes (e.g. String or CodeBlock nodes that only feed into a Data-Shapes input port)
                // so we don't duplicate them as separate inputs in the UI.
                var feederNodeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                // Pre-scan to identify Data-Shapes UI nodes and their feeder source nodes
                foreach (var n in nodeList.OfType<Dictionary<string, object>>())
                {
                    string nid = n.Str("Id");
                    var nv = (nid != null && nodeViewsById.TryGetValue(nid, out var v)) ? v : null;
                    string nName = n.Str("Name") ?? nv?.Str("Name") ?? n.Str("FunctionSymbol") ?? "";
                    string fsym = n.Str("FunctionSymbol") ?? "";
                    string fsig = n.Str("FunctionSignature") ?? "";
                    string ctype = n.Str("ConcreteType") ?? "";

                    string comb = $"{nName} {fsym} {fsig} {ctype}".ToLowerInvariant();
                    bool isDs = (nid != null && dataShapesNodeIds.Contains(nid)) || comb.Contains("data-shapes") || comb.Contains("ui.");

                    if (isDs)
                    {
                        if (comb.Contains("multipleinputform") || comb.Contains("multiple input form"))
                        {
                            details.HasDataShapesForm = true;
                        }
                        else
                        {
                            // Mark input feeder nodes as non-top-level inputs
                            if (n.TryGetValue("Inputs", out object inps) && inps is List<object> inList)
                            {
                                foreach (var p in inList.OfType<Dictionary<string, object>>())
                                {
                                    string pId = p.Str("Id");
                                    if (!string.IsNullOrEmpty(pId) && connEndToStart.TryGetValue(pId, out string sPortId))
                                    {
                                        if (portToNode.TryGetValue(sPortId, out var srcPair))
                                        {
                                            string srcId = srcPair.node.Str("Id");
                                            if (!string.IsNullOrEmpty(srcId)) feederNodeIds.Add(srcId);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

                // 6. Scan all candidate input nodes (Data-Shapes components, Dynamo Player inputs, and native input nodes)
                foreach (var node in nodeList.OfType<Dictionary<string, object>>())
                {
                    string id = node.Str("Id");
                    var nv = (id != null && nodeViewsById.TryGetValue(id, out var v)) ? v : null;

                    string name = node.Str("Name") ?? nv?.Str("Name") ?? node.Str("FunctionSymbol") ?? "";
                    string concreteType = node.Str("ConcreteType") ?? "";
                    string funcSig = node.Str("FunctionSignature") ?? "";
                    string funcSym = node.Str("FunctionSymbol") ?? "";
                    string desc = node.Str("Description") ?? "";

                    bool isFromDataShapes = (id != null && dataShapesNodeIds.Contains(id)) ||
                                            funcSig.IndexOf("Data-Shapes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            funcSym.IndexOf("Data-Shapes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                            name.IndexOf("Data-Shapes", StringComparison.OrdinalIgnoreCase) >= 0;

                    string combined = $"{name} {funcSym} {funcSig} {concreteType}".ToLowerInvariant();

                    // If it is the MultipleInputForm container itself, record it and skip as an input field
                    if (combined.Contains("multipleinputform") || combined.Contains("multiple input form"))
                    {
                        details.HasDataShapesForm = true;
                        continue;
                    }

                    // Skip internal feeder nodes that already provide labels/options to Data-Shapes components
                    if (id != null && feederNodeIds.Contains(id) && !isFromDataShapes)
                    {
                        continue;
                    }

                    bool isSetAsInput = node.Bool("IsSetAsInput") || node.Bool("IsInput") ||
                                        (nv != null && (nv.Bool("IsSetAsInput") || nv.Bool("IsInput")));

                    bool isUiDropdown = combined.Contains("dropdown") || combined.Contains("radiobutton") ||
                                        combined.Contains("combobox") || combined.Contains("categories") ||
                                        combined.Contains("familytypes") || combined.Contains("levels") ||
                                        combined.Contains("phases") || combined.Contains("views") ||
                                        combined.Contains("sheets") || combined.Contains("walltypes") ||
                                        combined.Contains("listview");

                    bool isUiTextbox = combined.Contains("textbox") || combined.Contains("text box") ||
                                       combined.Contains("stringinput") || (name.Equals("String", StringComparison.OrdinalIgnoreCase) && isSetAsInput);

                    bool isUiSlider = combined.Contains("slider");

                    bool isUiSelection = combined.Contains("selectmodelelement") || combined.Contains("select element") ||
                                         combined.Contains("select face") || combined.Contains("selectface") ||
                                         combined.Contains("selection") || combined.Contains("selectlinked");

                    bool isUiFile = combined.Contains("filepath") || combined.Contains("file path") || combined.Contains("filename");

                    bool isUiDir = combined.Contains("directorypath") || combined.Contains("directory path") || combined.Contains("directory");

                    bool isUiBool = combined.Contains("boolselector") || combined.Contains("toggle") || combined.Contains("checkbox") ||
                                    (name.Equals("Boolean", StringComparison.OrdinalIgnoreCase) && isSetAsInput);

                    bool isUiNumber = combined.Contains("doubleinput") || combined.Contains("integerinput") ||
                                      ((name.Equals("Number", StringComparison.OrdinalIgnoreCase) || name.Equals("Integer", StringComparison.OrdinalIgnoreCase)) && isSetAsInput);

                    bool isGenericUi = isFromDataShapes || combined.Contains("ui.") || combined.Contains("data-shapes");

                    if (!isSetAsInput && !isUiDropdown && !isUiTextbox && !isUiSlider &&
                        !isUiSelection && !isUiFile && !isUiDir && !isUiBool && !isUiNumber && !isGenericUi)
                    {
                        continue;
                    }

                    // Avoid duplicate additions
                    if (!string.IsNullOrEmpty(id) && details.Inputs.Any(i => i.Id.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var nodeInPorts = (node.TryGetValue("Inputs", out object np) && np is List<object> npl)
                        ? npl.OfType<Dictionary<string, object>>().ToList()
                        : new List<Dictionary<string, object>>();

                    // 1. Resolve Human-Readable Display Name
                    string resolvedName = "";
                    var pNamePort = nodeInPorts.FirstOrDefault(p => {
                        string pn = (p.Str("Name") ?? "").ToLowerInvariant();
                        return pn.Contains("input name") || pn.Contains("inputname") ||
                               pn.Contains("label") || pn.Contains("title") || pn.Contains("prompt");
                    }) ?? (nodeInPorts.Count > 0 ? nodeInPorts[0] : null);

                    if (pNamePort != null)
                    {
                        string connectedLabel = GetConnectedString(pNamePort.Str("Id"), connEndToStart, portToNode);
                        if (!string.IsNullOrWhiteSpace(connectedLabel))
                        {
                            resolvedName = connectedLabel;
                        }
                    }

                    if (string.IsNullOrWhiteSpace(resolvedName))
                    {
                        resolvedName = name;
                    }

                    // Clean generic Data-Shapes node names if unrenamed
                    if (string.IsNullOrWhiteSpace(resolvedName) ||
                        resolvedName.Equals("UI.TextBox Data", StringComparison.OrdinalIgnoreCase) ||
                        resolvedName.Equals("TextBox Data", StringComparison.OrdinalIgnoreCase) ||
                        resolvedName.Equals("UI.textbox", StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedName = "Text Input";
                    }
                    else if (resolvedName.Equals("UI.DropDown Data", StringComparison.OrdinalIgnoreCase) ||
                             resolvedName.Equals("DropDown Data", StringComparison.OrdinalIgnoreCase) ||
                             resolvedName.Equals("UI.dropdown", StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedName = "Dropdown Selection";
                    }
                    else if (resolvedName.Equals("UI.Slider Data", StringComparison.OrdinalIgnoreCase) ||
                             resolvedName.Equals("Slider Data", StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedName = "Slider Value";
                    }
                    else if (resolvedName.Equals("UI.SelectModelElements Data", StringComparison.OrdinalIgnoreCase) ||
                             resolvedName.Equals("SelectModelElements Data", StringComparison.OrdinalIgnoreCase))
                    {
                        resolvedName = "Select Model Elements";
                    }

                    // 2. Resolve Control Type & Default Values
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

                        var pKeysPort = nodeInPorts.FirstOrDefault(p => {
                            string pn = (p.Str("Name") ?? "").ToLowerInvariant();
                            return pn.Contains("keys") || pn.Contains("items") || pn.Contains("options");
                        });
                        if (pKeysPort != null)
                        {
                            var kList = GetConnectedStringList(pKeysPort.Str("Id"), connEndToStart, portToNode);
                            if (kList.Count > 0) optionsList.AddRange(kList);
                        }

                        if (optionsList.Count == 0)
                        {
                            var pValsPort = nodeInPorts.FirstOrDefault(p => {
                                string pn = (p.Str("Name") ?? "").ToLowerInvariant();
                                return pn.Contains("values");
                            });
                            if (pValsPort != null)
                            {
                                var vList = GetConnectedStringList(pValsPort.Str("Id"), connEndToStart, portToNode);
                                if (vList.Count > 0) optionsList.AddRange(vList);
                            }
                        }

                        if (optionsList.Count == 0)
                        {
                            if (combined.Contains("categor") || resolvedName.IndexOf("category", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                optionsList = new List<string>
                                {
                                    "OST_Doors", "OST_Windows", "OST_Walls", "OST_Rooms", "OST_Floors",
                                    "OST_Ceilings", "OST_Columns", "OST_StructuralColumns", "OST_StructuralFraming",
                                    "OST_DuctCurves", "OST_PipeCurves", "OST_Furniture", "OST_GenericModel", "OST_Views"
                                };
                            }
                            else if (combined.Contains("level") || resolvedName.IndexOf("level", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                optionsList = new List<string> { "Level 1", "Level 2", "Level 3", "Level 4" };
                            }
                            else if (combined.Contains("phase"))
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

                        var pMin = nodeInPorts.FirstOrDefault(p => (p.Str("Name") ?? "").ToLowerInvariant().Contains("min"));
                        if (pMin != null && !minVal.HasValue)
                        {
                            string sMin = GetConnectedString(pMin.Str("Id"), connEndToStart, portToNode);
                            if (double.TryParse(sMin, NumberStyles.Any, CultureInfo.InvariantCulture, out double pm)) minVal = pm;
                        }
                        var pMax = nodeInPorts.FirstOrDefault(p => (p.Str("Name") ?? "").ToLowerInvariant().Contains("max"));
                        if (pMax != null && !maxVal.HasValue)
                        {
                            string sMax = GetConnectedString(pMax.Str("Id"), connEndToStart, portToNode);
                            if (double.TryParse(sMax, NumberStyles.Any, CultureInfo.InvariantCulture, out double pm)) maxVal = pm;
                        }
                        var pStep = nodeInPorts.FirstOrDefault(p => (p.Str("Name") ?? "").ToLowerInvariant().Contains("step"));
                        if (pStep != null && !stepVal.HasValue)
                        {
                            string sStep = GetConnectedString(pStep.Str("Id"), connEndToStart, portToNode);
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
                        var pDef = nodeInPorts.FirstOrDefault(p => {
                            string pn = (p.Str("Name") ?? "").ToLowerInvariant();
                            return pn.Contains("default") || pn.Contains("text") || pn.Contains("value");
                        });
                        if (pDef != null)
                        {
                            string defTxt = GetConnectedString(pDef.Str("Id"), connEndToStart, portToNode);
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

                // 7. Extract Outputs array objects
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
            }
            catch (Exception ex)
            {
                Log.Write("InspectGraph failed for " + dynPath + ": " + ex.Message);
            }

            return details;
        }

        private static string GetConnectedString(string portId, Dictionary<string, string> connEndToStart, Dictionary<string, (Dictionary<string, object> node, Dictionary<string, object> port)> portToNode)
        {
            if (string.IsNullOrEmpty(portId)) return null;
            if (!connEndToStart.TryGetValue(portId, out string startPortId)) return null;
            if (!portToNode.TryGetValue(startPortId, out var pair)) return null;

            var sourceNode = pair.node;
            string concreteType = sourceNode.Str("ConcreteType") ?? "";
            string name = sourceNode.Str("Name") ?? "";

            if (concreteType.IndexOf("StringInput", StringComparison.OrdinalIgnoreCase) >= 0 || name.Equals("String", StringComparison.OrdinalIgnoreCase))
            {
                return sourceNode.Str("InputValue") ?? sourceNode.Str("Value");
            }

            if (concreteType.IndexOf("CodeBlock", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string code = sourceNode.Str("Code") ?? "";
                var m = Regex.Match(code, "\"([^\"]*)\"");
                if (m.Success) return m.Groups[1].Value;
                return code.Trim(';', ' ', '\r', '\n');
            }

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
        /// for isolated execution without altering the central original file.
        /// When values are provided, Data-Shapes UI.MultipleInputForm ++ popup is cleanly bypassed
        /// by injecting a CodeBlock providing the values directly to downstream nodes.
        /// When native UI is requested (__run_native_ui__), toggle is set to true so the Data-Shapes dialog opens.
        /// </summary>
        public static string CreateTempGraphWithInputs(string dynPath, Dictionary<string, string> inputOverrides)
        {
            if (string.IsNullOrWhiteSpace(dynPath) || !File.Exists(dynPath)) return dynPath;
            if (inputOverrides == null) inputOverrides = new Dictionary<string, string>();

            try
            {
                string json = File.ReadAllText(dynPath);
                var dyn = Json.AsObject(Json.Parse(json));
                if (dyn == null) return dynPath;

                bool runNativeUi = inputOverrides.ContainsKey("__run_native_ui__");

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

                // 2. Index nodes and connectors
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

                // 3. Locate Data-Shapes UI.MultipleInputForm ++ node if present
                var formNode = nodeList.OfType<Dictionary<string, object>>()
                    .FirstOrDefault(n => {
                        string comb = $"{n.Str("Name")} {n.Str("FunctionSymbol")} {n.Str("FunctionSignature")} {n.Str("ConcreteType")}".ToLowerInvariant();
                        return comb.Contains("multipleinputform");
                    });

                // Mode A: User wants the native Data-Shapes Windows Form dialog to appear!
                if (runNativeUi && formNode != null)
                {
                    formNode["InputValue"] = true;
                    if (formNode.TryGetValue("Inputs", out object formInputs) && formInputs is List<object> fList)
                    {
                        foreach (var fPort in fList.OfType<Dictionary<string, object>>())
                        {
                            string fpName = (fPort.Str("Name") ?? "").ToLowerInvariant();
                            if (fpName.Contains("toggle") || fpName.Contains("run"))
                            {
                                string tPortId = fPort.Str("Id");
                                if (connEndToStart.TryGetValue(tPortId, out string tStartId) &&
                                    portToNode.TryGetValue(tStartId, out var boolNode))
                                {
                                    boolNode["InputValue"] = true;
                                }
                            }
                        }
                    }
                }
                // Mode B: User is providing inputs from Command Center -> inject & bypass popup!
                else
                {
                    var orderedOverrides = new List<string>();

                    foreach (var nodeItem in nodeList)
                    {
                        if (!(nodeItem is Dictionary<string, object> node)) continue;
                        string id = node.Str("Id");
                        string name = node.Str("Name");
                        string concreteType = node.Str("ConcreteType") ?? "";
                        string funcSig = node.Str("FunctionSignature") ?? "";
                        string funcSym = node.Str("FunctionSymbol") ?? "";

                        string overrideVal = null;
                        if (!string.IsNullOrEmpty(id) && inputOverrides.TryGetValue(id, out string ov1)) overrideVal = ov1;
                        else if (!string.IsNullOrEmpty(name) && inputOverrides.TryGetValue(name, out string ov2)) overrideVal = ov2;
                        else
                        {
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
                        else if (name.IndexOf("UI.", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 funcSig.IndexOf("Data-Shapes", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 funcSym.IndexOf("Data-Shapes", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            node["InputValue"] = overrideVal;
                            node["Value"] = overrideVal;
                            node["SelectedString"] = overrideVal;

                            if (node.TryGetValue("Inputs", out object inPorts) && inPorts is List<object> ipList)
                            {
                                foreach (var p in ipList.OfType<Dictionary<string, object>>())
                                {
                                    string pn = (p.Str("Name") ?? "").ToLowerInvariant();
                                    if (pn.Contains("default") || pn.Contains("text") || pn.Contains("keys"))
                                    {
                                        string pId = p.Str("Id");
                                        if (connEndToStart.TryGetValue(pId, out string startPortId) &&
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

                    // If MultipleInputForm is present and we have user inputs to feed:
                    if (formNode != null && inputOverrides.Count > 0)
                    {
                        // Disable form popup by setting toggle to false
                        formNode["InputValue"] = false;
                        if (formNode.TryGetValue("Inputs", out object formInputs) && formInputs is List<object> fList)
                        {
                            foreach (var fPort in fList.OfType<Dictionary<string, object>>())
                            {
                                string fpName = (fPort.Str("Name") ?? "").ToLowerInvariant();
                                if (fpName.Contains("toggle") || fpName.Contains("run"))
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

                        // Replace MultipleInputForm's values output connection with a CodeBlock outputting the user values
                        if (formNode.TryGetValue("Outputs", out object formOutputs) && formOutputs is List<object> foList && foList.Count > 0)
                        {
                            var valuesPort = foList[0] as Dictionary<string, object>;
                            string valuesPortId = valuesPort?.Str("Id");

                            if (!string.IsNullOrEmpty(valuesPortId))
                            {
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
        public bool HasDataShapesForm { get; set; } = false;
    }
}
