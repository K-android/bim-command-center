using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMCommandCenter.Core.Catalog;
using BIMCommandCenter.Core.Infrastructure;

namespace BIMCommandCenter.Core.Revit
{
    /// <summary>
    /// Manages user-customized ribbon buttons pinned from the BIM Command Center.
    /// Persisted at %AppData%\BIMCommandCenter\ribbon_tools.json so each user has their
    /// own personalized quick-action panel on the Revit ribbon.
    /// </summary>
    public static class UserRibbonManager
    {
        private static readonly string RibbonConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "BIMCommandCenter",
            "ribbon_tools.json");

        private static List<Dictionary<string, object>> _activePinnedTools = new List<Dictionary<string, object>>();

        public static List<Dictionary<string, object>> LoadPinnedTools()
        {
            try
            {
                if (File.Exists(RibbonConfigPath))
                {
                    string json = File.ReadAllText(RibbonConfigPath);
                    var list = Json.AsList(Json.Parse(json));
                    if (list != null)
                    {
                        _activePinnedTools = list.OfType<Dictionary<string, object>>().ToList();
                        return _activePinnedTools;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("LoadPinnedTools error: " + ex.Message);
            }

            // Defaults if file does not exist yet (clean start without mock tools)
            _activePinnedTools = new List<Dictionary<string, object>>();
            return _activePinnedTools;
        }

        public static void SavePinnedTools(List<Dictionary<string, object>> tools)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RibbonConfigPath));
                File.WriteAllText(RibbonConfigPath, Json.Stringify(tools));
                _activePinnedTools = tools;
                RefreshLiveRibbon();
            }
            catch (Exception ex)
            {
                Log.Write("SavePinnedTools error: " + ex.Message);
            }
        }

        /// <summary>
        /// Updates the Revit WPF ribbon live at runtime so newly pinned/unpinned tools
        /// appear and disappear immediately without requiring Revit to restart.
        /// </summary>
        public static void RefreshLiveRibbon()
        {
            Action action = () =>
            {
                try
                {
                    Type componentManagerType = null;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        componentManagerType = asm.GetType("Autodesk.Windows.ComponentManager");
                        if (componentManagerType != null) break;
                    }

                    if (componentManagerType == null) return;

                    var ribbonProp = componentManagerType.GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static);
                    object ribbonObj = ribbonProp?.GetValue(null);
                    if (ribbonObj == null) return;

                    var tabsProp = ribbonObj.GetType().GetProperty("Tabs");
                    var tabs = tabsProp?.GetValue(ribbonObj) as System.Collections.IEnumerable;
                    if (tabs == null) return;

                    object bimTab = null;
                    foreach (var t in tabs)
                    {
                        var titleProp = t.GetType().GetProperty("Title") ?? t.GetType().GetProperty("Id");
                        string tTitle = titleProp?.GetValue(t)?.ToString();
                        if (string.Equals(tTitle, "BIM Tools", StringComparison.OrdinalIgnoreCase))
                        {
                            bimTab = t;
                            break;
                        }
                    }

                    if (bimTab == null) return;

                    var panelsProp = bimTab.GetType().GetProperty("Panels");
                    var panels = panelsProp?.GetValue(bimTab) as System.Collections.IEnumerable;
                    if (panels == null) return;

                    object quickPanelSource = null;
                    foreach (var p in panels)
                    {
                        var srcProp = p.GetType().GetProperty("Source");
                        object src = srcProp?.GetValue(p);
                        if (src != null)
                        {
                            var pTitleProp = src.GetType().GetProperty("Title");
                            string pTitle = pTitleProp?.GetValue(src)?.ToString();
                            if (string.Equals(pTitle, "My Quick Tools", StringComparison.OrdinalIgnoreCase))
                            {
                                quickPanelSource = src;
                                break;
                            }
                        }
                    }

                    if (quickPanelSource == null) return;

                    var itemsProp = quickPanelSource.GetType().GetProperty("Items");
                    var items = itemsProp?.GetValue(quickPanelSource) as System.Collections.IEnumerable;
                    if (items == null) return;

                    var pinned = _activePinnedTools ?? LoadPinnedTools();

                    for (int i = 0; i < 8; i++)
                    {
                        string targetIdPart = $"UserTool_{i}";
                        object btnObj = null;

                        foreach (var item in items)
                        {
                            var idProp = item.GetType().GetProperty("Id");
                            string itemId = idProp?.GetValue(item)?.ToString() ?? "";
                            if (itemId.Contains(targetIdPart))
                            {
                                btnObj = item;
                                break;
                            }
                        }

                        if (btnObj == null) continue;

                        var textProp = btnObj.GetType().GetProperty("Text");
                        var ttProp = btnObj.GetType().GetProperty("ToolTip");
                        var visProp = btnObj.GetType().GetProperty("IsVisible");
                        var enProp = btnObj.GetType().GetProperty("IsEnabled");

                        if (i < pinned.Count)
                        {
                            var tool = pinned[i];
                            string rawTitle = tool.Str("title") ?? ("Tool " + (i + 1));
                            string safeTitle = rawTitle.Replace(" ", "\n");
                            if (!safeTitle.Contains("\n") && safeTitle.Length > 8)
                            {
                                int mid = safeTitle.Length / 2;
                                safeTitle = safeTitle.Substring(0, mid) + "\n" + safeTitle.Substring(mid);
                            }

                            textProp?.SetValue(btnObj, safeTitle);
                            ttProp?.SetValue(btnObj, $"Run {rawTitle} directly via BIM Command Center.");
                            visProp?.SetValue(btnObj, true);
                            enProp?.SetValue(btnObj, true);
                        }
                        else
                        {
                            textProp?.SetValue(btnObj, $"Slot {i + 1}");
                            visProp?.SetValue(btnObj, false); // Hide empty slot button from ribbon
                            enProp?.SetValue(btnObj, false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("RefreshLiveRibbon error: " + ex.Message);
                }
            };

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.Invoke(action);
            }
            else
            {
                action();
            }
        }

        public static Dictionary<string, object> GetToolByIndex(int index)
        {
            if (_activePinnedTools == null || _activePinnedTools.Count == 0)
                LoadPinnedTools();

            if (index >= 0 && index < _activePinnedTools.Count)
                return _activePinnedTools[index];

            return null;
        }

        public static void ExecutePinnedTool(UIApplication app, int index)
        {
            var pinned = GetToolByIndex(index);
            if (pinned == null)
            {
                TaskDialog.Show("BIM Command Center", "This ribbon slot is not configured yet. Open BIM Command Center to pin tools.");
                return;
            }

            string toolId = pinned.Str("toolId");
            if (string.IsNullOrEmpty(toolId)) return;

            var services = CoreApp.Services;
            if (services == null) return;

            var fullTool = services.Catalog.FindTool(toolId) ?? pinned;
            Log.Write($"Ribbon direct execution of {toolId} by {Environment.UserName}");

            var result = services.Runner.Run(app, fullTool);
            if (!result.Ok)
            {
                TaskDialog.Show("Execution Result", $"{fullTool.Str("title")}: {result.Message}");
            }
        }
    }

    // Dedicated IExternalCommand slots for user-pinned ribbon buttons
    [Transaction(TransactionMode.Manual)] public class UserRibbonSlot0 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) { UserRibbonManager.ExecutePinnedTool(d.Application, 0); return Result.Succeeded; } }
    [Transaction(TransactionMode.Manual)] public class UserRibbonSlot1 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) { UserRibbonManager.ExecutePinnedTool(d.Application, 1); return Result.Succeeded; } }
    [Transaction(TransactionMode.Manual)] public class UserRibbonSlot2 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) { UserRibbonManager.ExecutePinnedTool(d.Application, 2); return Result.Succeeded; } }
    [Transaction(TransactionMode.Manual)] public class UserRibbonSlot3 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) { UserRibbonManager.ExecutePinnedTool(d.Application, 3); return Result.Succeeded; } }
    [Transaction(TransactionMode.Manual)] public class UserRibbonSlot4 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) { UserRibbonManager.ExecutePinnedTool(d.Application, 4); return Result.Succeeded; } }
    [Transaction(TransactionMode.Manual)] public class UserRibbonSlot5 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) { UserRibbonManager.ExecutePinnedTool(d.Application, 5); return Result.Succeeded; } }
    [Transaction(TransactionMode.Manual)] public class UserRibbonSlot6 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) { UserRibbonManager.ExecutePinnedTool(d.Application, 6); return Result.Succeeded; } }
    [Transaction(TransactionMode.Manual)] public class UserRibbonSlot7 : IExternalCommand { public Result Execute(ExternalCommandData d, ref string m, ElementSet e) { UserRibbonManager.ExecutePinnedTool(d.Application, 7); return Result.Succeeded; } }
}
