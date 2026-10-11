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
            RibbonPanel panel = app.CreateRibbonPanel(TabName, "Command Center");

            string assemblyPath = typeof(CoreApp).Assembly.Location;
            var button = new PushButtonData("BIMCC_Open", "Command\nCenter", assemblyPath, typeof(ShowPaneCommand).FullName)
            {
                ToolTip = "Open the BIM Command Center: search firm tools, run Dynamo/pyRevit/add-ins, ask the Copilot, request automations.",
                LargeImage = Icon(32),
                Image = Icon(16),
                AvailabilityClassName = typeof(AlwaysAvailable).FullName
            };
            panel.AddItem(button);
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
