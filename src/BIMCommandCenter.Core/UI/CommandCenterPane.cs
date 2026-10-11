using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Autodesk.Revit.UI;
using BIMCommandCenter.Core.Infrastructure;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace BIMCommandCenter.Core.UI
{
    /// <summary>Dockable pane that hosts the web UI in Edge WebView2.</summary>
    public class CommandCenterPane : UserControl, IDockablePaneProvider
    {
        private const string VirtualHost = "bimcc.local";
        private readonly Services _services;
        private readonly Grid _root = new Grid();
        private readonly TextBlock _status;
        private WebView2 _web;
        private bool _initStarted;

        public CommandCenterPane(Services services)
        {
            _services = services;
            Background = new SolidColorBrush(Color.FromRgb(248, 250, 252));
            _status = new TextBlock
            {
                Text = "Loading BIM Command Center…",
                Margin = new Thickness(16),
                TextWrapping = TextWrapping.Wrap,
                Foreground = Brushes.SlateGray
            };
            _root.Children.Add(_status);
            Content = _root;
            Loaded += async (s, e) => await InitializeAsync();
        }

        public void SetupDockablePane(DockablePaneProviderData data)
        {
            data.FrameworkElement = this;
            data.InitialState = new DockablePaneState
            {
                DockPosition = DockPosition.Right,
                MinimumWidth = 380
            };
            data.VisibleByDefault = false;
        }

        private async System.Threading.Tasks.Task InitializeAsync()
        {
            if (_initStarted) return;
            _initStarted = true;

            try
            {
                string coreDir = AppConfig.CoreDir;

                // We are loaded via Assembly.LoadFrom (no deps.json), so tell WebView2 where its native loader is.
                string nativeDir = Path.Combine(coreDir, "runtimes", "win-x64", "native");
                try { CoreWebView2Environment.SetLoaderDllFolderPath(Directory.Exists(nativeDir) ? nativeDir : coreDir); }
                catch (Exception ex) { Log.Write("SetLoaderDllFolderPath skipped: " + ex.Message); }

                // Default user-data folder would be next to Revit.exe (Program Files = not writable).
                string userData = Path.Combine(AppConfig.LocalDataDir, "WebView2");
                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, userData);

                _web = new WebView2();
                _root.Children.Add(_web);
                await _web.EnsureCoreWebView2Async(env);

                CoreWebView2 core = _web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = true;           // F12 for debugging; disable for production if desired
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = true;

                var bridge = new Bridge(core, _services);
                core.WebMessageReceived += bridge.OnMessage;

                // External links open in the user's browser, not inside Revit.
                core.NewWindowRequested += (s, e) =>
                {
                    e.Handled = true;
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
                };

                string url = _services.Config.FrontendUrl;
                if (string.IsNullOrWhiteSpace(url))
                {
                    string frontendDir = Path.Combine(coreDir, "frontend");
                    core.SetVirtualHostNameToFolderMapping(VirtualHost, frontendDir, CoreWebView2HostResourceAccessKind.Allow);
                    url = "https://" + VirtualHost + "/index.html";
                }
                core.Navigate(url);
                _status.Visibility = Visibility.Collapsed;
            }
            catch (WebView2RuntimeNotFoundException)
            {
                _status.Text = "Microsoft Edge WebView2 Runtime is not installed on this PC.\n\n" +
                               "Install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703 (or ask IT), then restart Revit.";
            }
            catch (Exception ex)
            {
                Log.Write("Pane init failed: " + ex);
                _status.Text = "BIM Command Center could not start its interface:\n\n" + ex.Message +
                               "\n\nLog: " + Path.Combine(AppConfig.LocalDataDir, "logs", "core.log");
            }
        }
    }
}
