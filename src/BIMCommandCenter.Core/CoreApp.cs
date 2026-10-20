using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Autodesk.Revit.Attributes;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using BIMCommandCenter.Core.Catalog;
using BIMCommandCenter.Core.Ideas;
using BIMCommandCenter.Core.Infrastructure;
using BIMCommandCenter.Core.Revit;
using BIMCommandCenter.Core.Security;
using BIMCommandCenter.Core.UI;

namespace BIMCommandCenter.Core
{
    /// <summary>Created by the Loader. Builds the ribbon, registers the dockable pane and shared services.</summary>
    public class CoreApp : IExternalApplication
    {
        public static readonly DockablePaneId PaneId = new DockablePaneId(new Guid("6F1B7C0E-3C1D-4E8B-9A57-2B3C4D5E6F70"));
        private const string TabName = "BIM Tools";

        public static Services Services { get; private set; }

        public Result OnStartup(UIControlledApplication application)
        {
            AppConfig config = AppConfig.Load();
            string year = application.ControlledApplication.VersionNumber;

            var dispatcher = new RevitDispatcher();
            dispatcher.Initialize();

            var governance = new GovernanceService(config);
            governance.InitializeSession();

            Services = new Services
            {
                Config = config,
                Dispatcher = dispatcher,
                Governance = governance,
                Catalog = new CatalogService(config, year, governance),
                Runner = new ToolRunner(config),
                Ideas = new IdeaService(config),
                RevitYear = year
            };

            application.RegisterDockablePane(PaneId, "BIM Command Center", new CommandCenterPane(Services));
            CreateRibbon(application);
            Log.Write($"Core started in Revit {year}. Config: {config.LoadedFrom}. Catalog: {config.CatalogSource}. Governance: {governance.LoadedPath ?? "none"}");
            return Result.Succeeded;
        }

        public Result OnShutdown(UIControlledApplication application) => Result.Succeeded;

        private static void CreateRibbon(UIControlledApplication app)
        {
            try { app.CreateRibbonTab(TabName); } catch { /* tab already exists (shared with other firm tools) */ }
            
            // 1. Control Plane Panel
            RibbonPanel panel = app.CreateRibbonPanel(TabName, "Command Center");
            string assemblyPath = typeof(CoreApp).Assembly.Location;

            var button = new PushButtonData("BIMCC_Open", "Command\nCenter", assemblyPath, typeof(ShowPaneCommand).FullName)
            {
                ToolTip = "Open the BIM Command Center: search corporate tools, run Dynamo/pyRevit/add-ins, ask the Copilot, request automations.",
                LargeImage = Icon(32),
                Image = Icon(16),
                AvailabilityClassName = typeof(AlwaysAvailable).FullName
            };
            panel.AddItem(button);

            // 2. Dynamic User Custom Ribbon Panel ("My Quick Tools")
            CreateUserRibbonPanel(app, assemblyPath);
        }

        private static void CreateUserRibbonPanel(UIControlledApplication app, string assemblyPath)
        {
            try
            {
                var pinnedTools = UserRibbonManager.LoadPinnedTools() ?? new List<Dictionary<string, object>>();

                RibbonPanel userPanel = app.CreateRibbonPanel(TabName, "My Quick Tools");

                for (int i = 0; i < 8; i++)
                {
                    bool hasTool = i < pinnedTools.Count;
                    var tool = hasTool ? pinnedTools[i] : null;
                    string title = hasTool ? (tool.Str("title") ?? ("Tool " + (i + 1))) : $"Slot {i + 1}";
                    string safeTitle = title.Replace(" ", "\n");
                    if (!safeTitle.Contains("\n") && safeTitle.Length > 8)
                    {
                        int mid = safeTitle.Length / 2;
                        safeTitle = safeTitle.Substring(0, mid) + "\n" + safeTitle.Substring(mid);
                    }

                    string cmdName = $"BIMCommandCenter.Core.Revit.UserRibbonSlot{i}";
                    var slotBtn = new PushButtonData($"BIMCC_UserTool_{i}", safeTitle, assemblyPath, cmdName)
                    {
                        ToolTip = hasTool ? $"Execute pinned tool '{title}' directly inside Revit." : "Pin your favorite automations from BIM Command Center.",
                        LargeImage = QuickToolIcon(32, i),
                        Image = QuickToolIcon(16, i),
                        AvailabilityClassName = typeof(AlwaysAvailable).FullName
                    };
                    var item = userPanel.AddItem(slotBtn) as PushButton;
                    if (item != null)
                    {
                        item.Visible = hasTool;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("CreateUserRibbonPanel error: " + ex.Message);
            }
        }

        /// <summary>Draws the ribbon icon in code so no image files need to ship.</summary>
        private static BitmapSource Icon(int size)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var blue = new SolidColorBrush(System.Windows.Media.Color.FromRgb(37, 99, 235));
                dc.DrawRoundedRectangle(blue, null, new Rect(0, 0, size, size), size * 0.2, size * 0.2);
                double cell = size * 0.28, gap = size * 0.1, start = size * 0.17;
                for (int r = 0; r < 2; r++)
                    for (int c = 0; c < 2; c++)
                    {
                        Brush fill = (r == 1 && c == 1) ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 191, 36)) : Brushes.White;
                        dc.DrawRoundedRectangle(fill, null, new Rect(start + c * (cell + gap), start + r * (cell + gap), cell, cell), size * 0.05, size * 0.05);
                    }
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        private static BitmapSource RhinoIcon(int size)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var bg = new SolidColorBrush(System.Windows.Media.Color.FromRgb(15, 118, 110)); // Teal
                dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, size, size), size * 0.22, size * 0.22);

                var pen = new System.Windows.Media.Pen(Brushes.White, size * 0.09);
                double y1 = size * 0.38;
                double y2 = size * 0.62;
                double xLeft = size * 0.22;
                double xRight = size * 0.78;

                // Top arrow pointing right
                dc.DrawLine(pen, new System.Windows.Point(xLeft, y1), new System.Windows.Point(xRight, y1));
                dc.DrawLine(pen, new System.Windows.Point(xRight - size * 0.18, y1 - size * 0.12), new System.Windows.Point(xRight, y1));
                dc.DrawLine(pen, new System.Windows.Point(xRight - size * 0.18, y1 + size * 0.12), new System.Windows.Point(xRight, y1));

                // Bottom arrow pointing left
                dc.DrawLine(pen, new System.Windows.Point(xRight, y2), new System.Windows.Point(xLeft, y2));
                dc.DrawLine(pen, new System.Windows.Point(xLeft + size * 0.18, y2 - size * 0.12), new System.Windows.Point(xLeft, y2));
                dc.DrawLine(pen, new System.Windows.Point(xLeft + size * 0.18, y2 + size * 0.12), new System.Windows.Point(xLeft, y2));
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }

        private static BitmapSource QuickToolIcon(int size, int slot)
        {
            var visual = new DrawingVisual();
            using (DrawingContext dc = visual.RenderOpen())
            {
                var colors = new[]
                {
                    System.Windows.Media.Color.FromRgb(14, 165, 233), // Sky
                    System.Windows.Media.Color.FromRgb(16, 185, 129), // Emerald
                    System.Windows.Media.Color.FromRgb(168, 85, 247), // Purple
                    System.Windows.Media.Color.FromRgb(245, 158, 11), // Amber
                    System.Windows.Media.Color.FromRgb(239, 68, 68),  // Rose
                    System.Windows.Media.Color.FromRgb(99, 102, 241), // Indigo
                    System.Windows.Media.Color.FromRgb(20, 184, 166), // Teal
                    System.Windows.Media.Color.FromRgb(236, 72, 153)  // Pink
                };
                var fill = new SolidColorBrush(colors[slot % colors.Length]);
                dc.DrawRoundedRectangle(fill, null, new Rect(0, 0, size, size), size * 0.22, size * 0.22);

                var pen = new System.Windows.Media.Pen(Brushes.White, size * 0.08);
                dc.DrawEllipse(null, pen, new System.Windows.Point(size * 0.5, size * 0.5), size * 0.28, size * 0.28);
            }
            var bmp = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(visual);
            bmp.Freeze();
            return bmp;
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class RhinoExchangeCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                UIApplication uiApp = commandData.Application;
                Document doc = uiApp.ActiveUIDocument?.Document;

                // Check for Rhino.Inside registered commands
                RevitCommandId rhinoInsideId = RevitCommandId.LookupCommandId("CustomCtrl_%CustomCtrl_%Rhino.Inside%Rhino.Inside.Revit%CommandStartRhino");
                if (rhinoInsideId == null)
                    rhinoInsideId = RevitCommandId.LookupCommandId("CustomCtrl_%Rhino.Inside.Revit%CommandStartRhino");

                if (rhinoInsideId != null && rhinoInsideId.CanHaveBinding)
                {
                    uiApp.PostCommand(rhinoInsideId);
                    return Result.Succeeded;
                }

                string rhino8Exe = @"C:\Program Files\Rhino 8\System\Rhino.exe";
                string rhino7Exe = @"C:\Program Files\Rhino 7\System\Rhino.exe";
                bool hasRhino8 = System.IO.File.Exists(rhino8Exe);
                bool hasRhino7 = System.IO.File.Exists(rhino7Exe);

                var td = new TaskDialog("Rhino ↔ Revit Interoperability")
                {
                    MainInstruction = "Rhino.Inside.Revit Not Detected",
                    MainContent = "Rhino.Inside.Revit connects Rhino & Grasshopper directly into Revit's live memory space for bi-directional geometry streaming.\n\n" +
                                  "To use live interoperability, install Rhino.Inside.Revit on this workstation (free for Rhino 7 & 8 license holders).\n\n" +
                                  (hasRhino8 ? "✓ Standalone Rhino 8 is installed.\n" : (hasRhino7 ? "✓ Standalone Rhino 7 is installed.\n" : "• Standalone Rhino 7/8 was not detected in default directories.\n")),
                    CommonButtons = TaskDialogCommonButtons.Close
                };

                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Download Rhino.Inside.Revit (Official Installer)", "Open https://www.rhino3d.com/inside/revit/ in default web browser.");
                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Open Command Center (Rhino Computational Tools)", "Browse Grasshopper scripts (.gh) and computational design catalog.");
                if (hasRhino8 || hasRhino7)
                {
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink3, "Launch Standalone Rhino Desktop", "Open external Rhino 3D session.");
                }

                TaskDialogResult res = td.Show();
                if (res == TaskDialogResult.CommandLink1)
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://www.rhino3d.com/inside/revit/8/") { UseShellExecute = true });
                }
                else if (res == TaskDialogResult.CommandLink2)
                {
                    DockablePane pane = uiApp.GetDockablePane(CoreApp.PaneId);
                    if (!pane.IsShown()) pane.Show();
                }
                else if (res == TaskDialogResult.CommandLink3)
                {
                    string target = hasRhino8 ? rhino8Exe : rhino7Exe;
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
                }

                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    [Transaction(TransactionMode.Manual)]
    public class ShowPaneCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            try
            {
                DockablePane pane = commandData.Application.GetDockablePane(CoreApp.PaneId);
                if (pane.IsShown()) pane.Hide(); else pane.Show();
                return Result.Succeeded;
            }
            catch (Exception ex)
            {
                message = ex.Message;
                return Result.Failed;
            }
        }
    }

    /// <summary>Button works even with no project open (the UI explains what needs a document).</summary>
    public class AlwaysAvailable : IExternalCommandAvailability
    {
        public bool IsCommandAvailable(UIApplication app, CategorySet selectedCategories) => true;
    }
}
