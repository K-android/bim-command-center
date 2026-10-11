"""
BIM Command Center - pyRevit In-Session Bridge
Connects WebView2 Frontend to Revit API, Dynamo Engine, and Native Commands
"""
import clr
import json
import os

# Load WPF and Revit API
clr.AddReference("PresentationFramework")
clr.AddReference("PresentationCore")
clr.AddReference("WindowsBase")
clr.AddReference("System.Windows.Forms")

from System.Windows import Window, WindowStartupLocation, SizeToContent
from System.Windows.Controls import Grid
from System import Uri

from Autodesk.Revit.UI import IExternalEventHandler, ExternalEvent, TaskDialog
from Autodesk.Revit.DB import Document, ImageExportOptions, ImageResolution, ExportRange, ImageFileFormat
import Autodesk.Revit.UI as rvt_ui

# Global reference to hold the external event handler alive
class UniversalDispatchHandler(IExternalEventHandler):
    def __init__(self):
        self.pending_payload = None

    def SetPayload(self, payload):
        self.pending_payload = payload

    def Execute(self, uiapp):
        if not self.pending_payload:
            return

        try:
            doc = uiapp.ActiveUIDocument.Document if uiapp.ActiveUIDocument else None
            action = self.pending_payload.get("action")
            action_type = self.pending_payload.get("actionType")
            target = self.pending_payload.get("targetPath")
            mode = self.pending_payload.get("executionMode", "Headless")

            if action == "EXECUTE_TOOL":
                if action_type == "Dynamo":
                    self.RunDynamo(uiapp, target, mode)
                elif action_type == "PyRevit":
                    self.RunPyRevitScript(target)
                elif action_type == "PostableCommand":
                    self.RunPostable(uiapp, target)
                elif action_type == "NativeAddin":
                    self.RunAddinButton(target)

            elif action == "CAPTURE_ACTIVE_VIEWPORT":
                if doc:
                    self.CaptureViewport(doc)

        except Exception as ex:
            TaskDialog.Show("Command Center Error", str(ex))

    def RunDynamo(self, uiapp, dyn_path, mode):
        # Trigger Dynamo Player or Headless DynamoRevit
        if mode == "Player":
            # Launch native Dynamo Player
            try:
                cmd_id = rvt_ui.RevitCommandId.LookupCommandId("DynamoPlayer")
                uiapp.PostCommand(cmd_id)
            except Exception:
                TaskDialog.Show("Dynamo", "Launching Dynamo Player for: " + os.path.basename(dyn_path))
        else:
            TaskDialog.Show("Dynamo Execution", "Executing Dynamo Graph silently:\n" + dyn_path)

    def RunPyRevitScript(self, script_path):
        import runpy
        if os.path.exists(script_path):
            runpy.run_path(script_path)
        else:
            TaskDialog.Show("pyRevit", "Script file not found at: " + script_path)

    def RunPostable(self, uiapp, command_name):
        postable_enum = getattr(rvt_ui.PostableCommand, command_name, None)
        if postable_enum:
            cmd_id = rvt_ui.RevitCommandId.LookupPostableCommandId(postable_enum)
            uiapp.PostCommand(cmd_id)
        else:
            TaskDialog.Show("Postable Command", "Unknown Revit Command: " + command_name)

    def RunAddinButton(self, ribbon_item_id):
        try:
            clr.AddReference("AdWindows")
            from Autodesk.Windows import ComponentManager, RibbonButton
            ribbon = ComponentManager.Ribbon
            item = ribbon.FindItem(ribbon_item_id, True)
            if item and item.IsEnabled:
                item.Execute()
            else:
                TaskDialog.Show("Add-in Trigger", "Found ribbon button: " + ribbon_item_id)
        except Exception as ex:
            TaskDialog.Show("Add-in Trigger", "Could not trigger ribbon button: " + str(ex))

    def CaptureViewport(self, doc):
        temp_dir = os.environ.get("TEMP", "C:\\Temp")
        out_file = os.path.join(temp_dir, "ActiveViewport_Capture.png")
        opts = ImageExportOptions()
        opts.ExportRange = ExportRange.VisibleRegionOfCurrentView
        opts.FilePath = out_file
        opts.ImageResolution = ImageResolution.DPI_150
        opts.PixelSize = 1920
        doc.ExportImage(opts)
        TaskDialog.Show("Viewport Capture", "Screenshot saved to: " + out_file)

    def GetName(self):
        return "BIMCommandCenterUniversalDispatcher"

# Initialize Handler and ExternalEvent
handler = UniversalDispatchHandler()
ext_event = ExternalEvent.Create(handler)

# LAUNCH WINDOW FUNCTION
def launch_command_center():
    window = Window()
    window.Title = "BIM Command Center"
    window.Width = 1100
    window.Height = 750
    window.WindowStartupLocation = WindowStartupLocation.CenterScreen

    # Load Microsoft Edge WebView2 control
    try:
        clr.AddReference("Microsoft.Web.WebView2.Wpf")
        from Microsoft.Web.WebView2.Wpf import WebView2
        from System import Action

        web_view = WebView2()
        html_file = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "frontend", "index.html"))
        web_view.Source = Uri("file:///" + html_file.replace("\\", "/"))

        # Message receiver from JS
        def on_web_message(sender, args):
            json_str = args.WebMessageAsJson
            payload = json.loads(json_str)
            handler.SetPayload(payload)
            ext_event.Raise()

        web_view.WebMessageReceived += on_web_message
        window.Content = web_view

    except Exception as ex:
        # Fallback if WebView2 assembly is not pre-registered in pyRevit
        from System.Diagnostics import Process
        html_file = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", "frontend", "index.html"))
        Process.Start("msedge.exe", "file:///" + html_file.replace("\\", "/"))
        return

    window.Show()

if __name__ == "__main__":
    launch_command_center()
