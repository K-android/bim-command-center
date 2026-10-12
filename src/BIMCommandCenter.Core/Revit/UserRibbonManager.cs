using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

            // Defaults if file does not exist yet
            _activePinnedTools = new List<Dictionary<string, object>>
            {
                new Dictionary<string, object>
                {
                    ["toolId"] = "bim-health-check",
                    ["title"] = "BIM Health\nCheck",
                    ["category"] = "Audit"
                },
                new Dictionary<string, object>
                {
                    ["toolId"] = "doc-renumber-doors",
                    ["title"] = "Renumber\nDoors",
                    ["category"] = "Documentation"
                }
            };
            SavePinnedTools(_activePinnedTools);
            return _activePinnedTools;
        }

        public static void SavePinnedTools(List<Dictionary<string, object>> tools)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RibbonConfigPath));
                File.WriteAllText(RibbonConfigPath, Json.Stringify(tools));
                _activePinnedTools = tools;
            }
            catch (Exception ex)
            {
                Log.Write("SavePinnedTools error: " + ex.Message);
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
