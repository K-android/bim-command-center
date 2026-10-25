using System;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMCommandCenter.Core.Infrastructure;

namespace BIMCommandCenter.Core.Revit
{
    public class CustomFamilyLoadOptions : IFamilyLoadOptions
    {
        public bool OnFamilyFound(bool familyInUse, out bool overwriteParameterValues)
        {
            overwriteParameterValues = true;
            return true;
        }

        public bool OnSharedFamilyFound(Family sharedFamily, bool familyInUse, out FamilySource source, out bool overwriteParameterValues)
        {
            source = FamilySource.Family;
            overwriteParameterValues = true;
            return true;
        }
    }

    public static class FamilyLoadRunner
    {
        public static RunResult Execute(UIApplication app, string familyPath, bool placeAfterLoad)
        {
            try
            {
                if (app == null || app.ActiveUIDocument == null)
                    return RunResult.Fail("No active Revit project document is open. Please open a project first.");

                Document doc = app.ActiveUIDocument.Document;
                if (doc == null)
                    return RunResult.Fail("Active Revit document is unavailable.");

                if (doc.IsFamilyDocument)
                    return RunResult.Fail("Active document is a Family (.rfa). Please switch to a Project document (.rvt).");

                if (!File.Exists(familyPath))
                    return RunResult.Fail("Family file not found on disk: " + familyPath);

                string famName = Path.GetFileNameWithoutExtension(familyPath);
                string ext = Path.GetExtension(familyPath).ToLowerInvariant();

                if (ext != ".rfa")
                {
                    return RunResult.Fail("File format '" + ext + "' is not a native Revit Family (.rfa). For DWG or 3DM blocks, import them via Revit's Insert tab.");
                }

                // Check if already in project
                Family family = new FilteredElementCollector(doc)
                    .OfClass(typeof(Family))
                    .Cast<Family>()
                    .FirstOrDefault(f => f.Name.Equals(famName, StringComparison.OrdinalIgnoreCase));

                bool wasAlreadyLoaded = family != null;

                using (var tx = new Transaction(doc, "Load Family: " + famName))
                {
                    tx.Start();
                    try
                    {
                        bool loaded = doc.LoadFamily(familyPath, new CustomFamilyLoadOptions(), out Family loadedFam);
                        if (loadedFam != null) family = loadedFam;
                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.RollBack();
                        if (family == null)
                        {
                            return RunResult.Fail("Failed to load family '" + famName + "': " + ex.Message);
                        }
                    }
                }

                // Re-query if null
                if (family == null)
                {
                    family = new FilteredElementCollector(doc)
                        .OfClass(typeof(Family))
                        .Cast<Family>()
                        .FirstOrDefault(f => f.Name.Equals(famName, StringComparison.OrdinalIgnoreCase));
                }

                if (family == null)
                {
                    return RunResult.Fail("Revit did not return the loaded family element for '" + famName + "'.");
                }

                if (placeAfterLoad)
                {
                    var symbolIds = family.GetFamilySymbolIds();
                    var symbolId = symbolIds.FirstOrDefault();
                    if (symbolId != null && symbolId != ElementId.InvalidElementId)
                    {
                        var symbol = doc.GetElement(symbolId) as FamilySymbol;
                        if (symbol != null)
                        {
                            if (!symbol.IsActive)
                            {
                                using (var tx = new Transaction(doc, "Activate Family Symbol"))
                                {
                                    tx.Start();
                                    symbol.Activate();
                                    tx.Commit();
                                }
                            }

                            try
                            {
                                app.ActiveUIDocument.PromptForFamilyInstancePlacement(symbol);
                                return RunResult.Success($"'{famName}' loaded. Ready for placement in active view.");
                            }
                            catch (Autodesk.Revit.Exceptions.OperationCanceledException)
                            {
                                return RunResult.Success($"'{famName}' loaded into project.");
                            }
                        }
                    }
                }

                return RunResult.Success(wasAlreadyLoaded
                    ? $"'{famName}' reloaded and updated in project."
                    : $"'{famName}' successfully loaded into active project.");
            }
            catch (Exception ex)
            {
                Log.Write("FamilyLoadRunner error: " + ex);
                return RunResult.Fail("Error loading family: " + ex.Message);
            }
        }
    }
}
