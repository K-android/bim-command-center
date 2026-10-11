using System;
using System.IO;
using System.Windows;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Autodesk.Windows;
using Microsoft.Web.WebView2.Wpf;
using Newtonsoft.Json;

namespace BIMCommandCenter
{
    public class BridgePayload
    {
        public string Action { get; set; }
        public string ToolId { get; set; }
        public string ActionType { get; set; }
        public string TargetPath { get; set; }
        public string ExecutionMode { get; set; }
    }

    public class DispatcherHandler : IExternalEventHandler
    {
        public BridgePayload PendingPayload { get; set; }

        public void Execute(UIApplication uiapp)
        {
            if (PendingPayload == null) return;

            try
            {
                Document doc = uiapp.ActiveUIDocument?.Document;

                if (PendingPayload.Action == "EXECUTE_TOOL")
                {
                    switch (PendingPayload.ActionType)
                    {
                        case "Dynamo":
                            ExecuteDynamo(uiapp, PendingPayload.TargetPath, PendingPayload.ExecutionMode);
                            break;
                        case "NativeAddin":
                            ExecuteRibbonButton(PendingPayload.TargetPath);
                            break;
                        case "PostableCommand":
                            ExecutePostable(uiapp, PendingPayload.TargetPath);
                            break;
                    }
                }
                else if (PendingPayload.Action == "CAPTURE_ACTIVE_VIEWPORT" && doc != null)
                {
                    CaptureViewport(doc);
                }
            }
            catch (Exception ex)
            {
                TaskDialog.Show("Command Center Error", ex.Message);
            }
        }

        private void ExecuteDynamo(UIApplication uiapp, string dynPath, string mode)
        {
            if (mode == "Player")
            {
                RevitCommandId id = RevitCommandId.LookupCommandId("DynamoPlayer");
                if (id != null) uiapp.PostCommand(id);
            }
            else
            {
                TaskDialog.Show("Dynamo Headless", $"Silently executing Dynamo Graph:\n{dynPath}");
            }
        }

        private void ExecuteRibbonButton(string buttonId)
        {
            var ribbon = ComponentManager.Ribbon;
            var item = ribbon.FindItem(buttonId, true) as RibbonButton;
            if (item != null && item.IsEnabled)
            {
                item.Execute();
            }
        }

        private void ExecutePostable(UIApplication uiapp, string cmdName)
        {
            if (Enum.TryParse(cmdName, out PostableCommand cmd))
            {
                RevitCommandId id = RevitCommandId.LookupPostableCommandId(cmd);
                uiapp.PostCommand(id);
            }
        }

        private void CaptureViewport(Document doc)
        {
            string tempPath = Path.Combine(Path.GetTempPath(), "ViewportCapture.png");
            ImageExportOptions opts = new ImageExportOptions
            {
                ExportRange = ExportRange.VisibleRegionOfCurrentView,
                FilePath = tempPath,
                ImageResolution = ImageResolution.DPI_150,
                PixelSize = 1920
            };
            doc.ExportImage(opts);
        }

        public string GetName() => "BIMCommandCenterDispatcherHandler";
    }

    [Autodesk.Revit.Attributes.Transaction(Autodesk.Revit.Attributes.TransactionMode.Manual)]
    public class OpenCommandCenterCommand : IExternalCommand
    {
        public Result Execute(ExternalCommandData commandData, ref string message, ElementSet elements)
        {
            var handler = new DispatcherHandler();
            var extEvent = ExternalEvent.Create(handler);

            var window = new Window
            {
                Title = "BIM Command Center",
                Width = 1100,
                Height = 750,
                WindowStartupLocation = WindowStartupLocation.CenterScreen
            };

            var webView = new WebView2();
            string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "frontend", "index.html");
            webView.Source = new Uri(htmlPath);

            webView.WebMessageReceived += (s, e) =>
            {
                var payload = JsonConvert.DeserializeObject<BridgePayload>(e.WebMessageAsJson);
                handler.PendingPayload = payload;
                extEvent.Raise();
            };

            window.Content = webView;
            window.Show();

            return Result.Succeeded;
        }
    }
}
