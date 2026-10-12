using System;
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

            Services = new Services
            {
                Config = config,
                Dispatcher = dispatcher,
                Catalog = new CatalogService(config, year),
                Runner = new ToolRunner(config),
                Ideas = new IdeaService(config),
                RevitYear = year
            };

            application.RegisterDockablePane(PaneId, "BIM Command Center", new CommandCenterPane(Services));
            CreateRibbon(application);
            Log.Write($"Core started in Revit {year}. Config: {config.LoadedFrom}. Catalog: {config.CatalogSource}");
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

            // 2. Rhino <-> Revit Exchange Button (Beside Command Center)
            var rhinoButton = new PushButtonData("BIMCC_RhinoExchange", "Rhino ↔ Revit\nExchange", assemblyPath, typeof(RhinoExchangeCommand).FullName)
            {
                ToolTip = "Rhino ↔ Revit Live Exchange: launch Rhino.Inside.Revit, Grasshopper direct shape sync, and computational geometry workflows.",
                LargeImage = RhinoIcon(32),
                Image = RhinoIcon(16),
                AvailabilityClassName = typeof(AlwaysAvailable).FullName
            };
            panel.AddItem(rhinoButton);

            // 3. Dynamic User Custom Ribbon Panel ("My Quick Tools")
            CreateUserRibbonPanel(app, assemblyPath);
        }

        private static void CreateUserRibbonPanel(UIControlledApplication app, string assemblyPath)
        {
            try
            {
                var pinnedTools = UserRibbonManager.LoadPinnedTools();
                if (pinnedTools == null || pinnedTools.Count == 0) return;

                RibbonPanel userPanel = app.CreateRibbonPanel(TabName, "My Quick Tools");
                int maxSlots = Math.Min(pinnedTools.Count, 8);

                for (int i = 0; i < maxSlots; i++)
                {
                    var tool = pinnedTools[i];
                    string toolId = tool.Str("toolId");
                    string title = tool.Str("title") ?? ("Tool " + (i + 1));
                    string safeTitle = title.Replace(" ", "\n");
                    if (!safeTitle.Contains("\n") && safeTitle.Length > 8)
                    {
                        int mid = safeTitle.Length / 2;
                        safeTitle = safeTitle.Substring(0, mid) + "\n" + safeTitle.Substring(mid);
                    }

                    string cmdName = $"BIMCommandCenter.Core.Revit.UserRibbonSlot{i}";
                    var slotBtn = new PushButtonData($"BIMCC_UserTool_{i}_{toolId}", safeTitle, assemblyPath, cmdName)
                    {
                        ToolTip = $"Execute pinned tool '{title}' directly inside Revit.",
                        LargeImage = QuickToolIcon(32, i),
                        Image = QuickToolIcon(16, i),
                        AvailabilityClassName = typeof(AlwaysAvailable).FullName
                    };
                    userPanel.AddItem(slotBtn);
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

                var td = new TaskDialog("Rhino ↔ Revit Exchange")
                {
                    MainInstruction = "Rhino ↔ Revit Computational Exchange",
                    MainContent = (doc != null ? $"Active Model: {doc.Title}\n\n" : "No active Revit project open.\n\n") +
                                  (hasRhino8 ? "✓ Rhino 8 detected on this workstation.\n" : (hasRhino7 ? "✓ Rhino 7 detected on this workstation.\n" : "• Rhino desktop standalone not found in default path.\n")) +
                                  "Connect live geometry streams, execute Grasshopper definitions, or open the BIM Command Center.",
                    CommonButtons = TaskDialogCommonButtons.Close
                };

                td.AddCommandLink(TaskDialogCommandLinkId.CommandLink1, "Open Command Center (Computational Tools)", "Filter and browse computational scripts and exchange tools.");
                if (hasRhino8 || hasRhino7)
                {
                    td.AddCommandLink(TaskDialogCommandLinkId.CommandLink2, "Launch Rhino Desktop Application", "Start standalone Rhino session with Grasshopper.");
                }

                TaskDialogResult res = td.Show();
                if (res == TaskDialogResult.CommandLink1)
                {
                    DockablePane pane = uiApp.GetDockablePane(CoreApp.PaneId);
                    if (!pane.IsShown()) pane.Show();
                }
                else if (res == TaskDialogResult.CommandLink2)
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
