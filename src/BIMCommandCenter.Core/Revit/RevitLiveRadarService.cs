using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WpfEllipse = System.Windows.Shapes.Ellipse;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfGrid = System.Windows.Controls.Grid;
using WpfVisibility = System.Windows.Visibility;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Events;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;
using BIMCommandCenter.Core.Infrastructure;

namespace BIMCommandCenter.Core.Revit
{
    /// <summary>
    /// Live Multi-LAN Coordination & Clash Radar Telemetry Service for Autodesk Revit.
    /// Hooks document and view events with zero UI thread lag, calculates millimetric
    /// spatial bounding boxes, broadcasts two-way heartbeats to bim-control-room, and
    /// renders a lightweight non-blocking WPF status toast in Revit.
    /// </summary>
    public static class RevitLiveRadarService
    {
        private const double FEET_TO_MM = 304.8;
        private static readonly object _stateLock = new object();
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };

        // Internal State
        private static bool _isInitialized = false;
        private static string _controlRoomBaseUrl = "http://127.0.0.1:5050";
        private static string _syncStatus = "WORKING";
        private static DateTime? _syncStartTime = null;
        private static DateTime _lastSyncTime = DateTime.UtcNow;
        private static DateTime _lastIdlingCheck = DateTime.MinValue;

        // Dirty Tracking
        private static readonly HashSet<string> _dirtyUniqueIds = new HashSet<string>();
        private static RadarBoundingBox _dirtyBounds = null;
        private static string _lastActiveCategory = "General";

        // View Context
        private static string _activeViewName = "3D View";
        private static string _activeViewType = "ThreeD";
        private static string _activeViewId = "";
        private static string _activeLevel = "Level 1";
        private static string _activeSheet = "";
        private static List<string> _selectedUniqueIds = new List<string>();
        private static RadarBoundingBox _selectionBounds = null;

        // External Event Handler for Teammate Highlight
        private static HighlightTeammateEditsHandler _highlightHandler;
        private static ExternalEvent _highlightEvent;

        // Non-blocking WPF HUD Toast
        private static RevitRadarToastWindow _toastWindow = null;

        /// <summary>
        /// Initializes the telemetry service with UIControlledApplication during startup
        /// </summary>
        public static void Initialize(UIControlledApplication uiApp, AppConfig config = null)
        {
            if (_isInitialized) return;

            ResolveControlRoomUrl(config);

            // Register ExternalEvent for viewport zooming/highlighting
            _highlightHandler = new HighlightTeammateEditsHandler();
            _highlightEvent = ExternalEvent.Create(_highlightHandler);

            // Subscribe to document worksharing and edit events
            uiApp.ControlledApplication.DocumentChanged += OnDocumentChanged;
            uiApp.ControlledApplication.DocumentSynchronizingWithCentral += OnDocumentSynchronizing;
            uiApp.ControlledApplication.DocumentSynchronizedWithCentral += OnDocumentSynchronized;
            uiApp.ViewActivated += OnViewActivated;
            uiApp.Idling += OnIdling;

            _isInitialized = true;
            Log.Write($"[RevitLiveRadar] Initialized. Target Control Room: {_controlRoomBaseUrl}");
        }

        /// <summary>
        /// Overload for runtime initialization from UIApplication
        /// </summary>
        public static void Initialize(UIApplication uiApp, AppConfig config = null)
        {
            if (_isInitialized) return;

            ResolveControlRoomUrl(config);

            _highlightHandler = new HighlightTeammateEditsHandler();
            _highlightEvent = ExternalEvent.Create(_highlightHandler);

            uiApp.Application.DocumentChanged += OnDocumentChanged;
            uiApp.Application.DocumentSynchronizingWithCentral += OnDocumentSynchronizing;
            uiApp.Application.DocumentSynchronizedWithCentral += OnDocumentSynchronized;
            uiApp.ViewActivated += OnViewActivated;
            uiApp.Idling += OnIdling;

            _isInitialized = true;
            Log.Write($"[RevitLiveRadar] Initialized via UIApplication. Target: {_controlRoomBaseUrl}");
        }

        private static void ResolveControlRoomUrl(AppConfig config)
        {
            try
            {
                string envUrl = Environment.GetEnvironmentVariable("BIM_CONTROL_ROOM_HOST")
                             ?? Environment.GetEnvironmentVariable("CONTROL_ROOM_URL");

                if (!string.IsNullOrWhiteSpace(envUrl))
                {
                    _controlRoomBaseUrl = envUrl.Trim().TrimEnd('/');
                    return;
                }

                if (config != null && !string.IsNullOrWhiteSpace(config.FrontendUrl))
                {
                    var uri = new Uri(config.FrontendUrl);
                    _controlRoomBaseUrl = $"{uri.Scheme}://{uri.Host}:5050";
                    return;
                }
            }
            catch { }

            _controlRoomBaseUrl = "http://127.0.0.1:5050";
        }

        // =========================================================================
        // 1. REVT EVENT HOOKS (ZERO UI THREAD LAG)
        // =========================================================================

        private static void OnDocumentChanged(object sender, DocumentChangedEventArgs e)
        {
            try
            {
                Document doc = e.GetDocument();
                if (doc == null || !doc.IsValidObject) return;

                ICollection<ElementId> added = e.GetAddedElementIds();
                ICollection<ElementId> modified = e.GetModifiedElementIds();

                if ((added == null || added.Count == 0) && (modified == null || modified.Count == 0)) return;

                lock (_stateLock)
                {
                    if (added != null)
                    {
                        foreach (var id in added)
                        {
                            Element elem = doc.GetElement(id);
                            if (elem == null || !elem.IsValidObject) continue;
                            _dirtyUniqueIds.Add(elem.UniqueId);
                            ExpandDirtyBounds(elem);
                        }
                    }

                    if (modified != null)
                    {
                        foreach (var id in modified)
                        {
                            Element elem = doc.GetElement(id);
                            if (elem == null || !elem.IsValidObject) continue;
                            _dirtyUniqueIds.Add(elem.UniqueId);
                            ExpandDirtyBounds(elem);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("[RevitLiveRadar] DocumentChanged error: " + ex.Message);
            }
        }

        private static void ExpandDirtyBounds(Element elem)
        {
            try
            {
                BoundingBoxXYZ box = elem.get_BoundingBox(null);
                if (box == null) return;

                double minX = box.Min.X * FEET_TO_MM;
                double minY = box.Min.Y * FEET_TO_MM;
                double minZ = box.Min.Z * FEET_TO_MM;
                double maxX = box.Max.X * FEET_TO_MM;
                double maxY = box.Max.Y * FEET_TO_MM;
                double maxZ = box.Max.Z * FEET_TO_MM;

                if (_dirtyBounds == null)
                {
                    _dirtyBounds = new RadarBoundingBox
                    {
                        min = new Point3D { x = minX, y = minY, z = minZ },
                        max = new Point3D { x = maxX, y = maxY, z = maxZ }
                    };
                }
                else
                {
                    _dirtyBounds.min.x = Math.Min(_dirtyBounds.min.x, minX);
                    _dirtyBounds.min.y = Math.Min(_dirtyBounds.min.y, minY);
                    _dirtyBounds.min.z = Math.Min(_dirtyBounds.min.z, minZ);
                    _dirtyBounds.max.x = Math.Max(_dirtyBounds.max.x, maxX);
                    _dirtyBounds.max.y = Math.Max(_dirtyBounds.max.y, maxY);
                    _dirtyBounds.max.z = Math.Max(_dirtyBounds.max.z, maxZ);
                }

                if (elem.Category != null && !string.IsNullOrWhiteSpace(elem.Category.Name))
                {
                    _lastActiveCategory = elem.Category.Name;
                }
            }
            catch { }
        }

        private static void OnDocumentSynchronizing(object sender, DocumentSynchronizingWithCentralEventArgs e)
        {
            try
            {
                _syncStatus = "SYNCING";
                _syncStartTime = DateTime.UtcNow;
                Log.Write("[RevitLiveRadar] Central sync started. Broadcasting SYNCING status.");
                SendHeartbeatAsync(e.Document, isImmediate: true);
            }
            catch (Exception ex)
            {
                Log.Write("[RevitLiveRadar] DocumentSynchronizing error: " + ex.Message);
            }
        }

        private static void OnDocumentSynchronized(object sender, DocumentSynchronizedWithCentralEventArgs e)
        {
            try
            {
                lock (_stateLock)
                {
                    _dirtyUniqueIds.Clear();
                    _dirtyBounds = null;
                }
                _syncStatus = "WORKING";
                _syncStartTime = null;
                _lastSyncTime = DateTime.UtcNow;
                Log.Write("[RevitLiveRadar] Central sync completed. Cleared dirty elements.");
                SendHeartbeatAsync(e.Document, isImmediate: true);
            }
            catch (Exception ex)
            {
                Log.Write("[RevitLiveRadar] DocumentSynchronized error: " + ex.Message);
            }
        }

        private static void OnViewActivated(object sender, ViewActivatedEventArgs e)
        {
            try
            {
                View view = e.CurrentActiveView;
                if (view == null || !view.IsValidObject) return;

                _activeViewName = view.Name ?? "Active View";
                _activeViewType = view.ViewType.ToString();
                _activeViewId = view.Id.ToString();

                if (view is ViewSheet sheet)
                {
                    _activeSheet = $"{sheet.SheetNumber} - {sheet.Name}";
                }
                else
                {
                    _activeSheet = "";
                }

                if (view.GenLevel != null)
                {
                    _activeLevel = view.GenLevel.Name;
                }
            }
            catch { }
        }

        private static void OnIdling(object sender, IdlingEventArgs e)
        {
            try
            {
                // Throttled: only execute every 3 seconds to keep UI responsive
                if ((DateTime.UtcNow - _lastIdlingCheck).TotalSeconds < 3.0) return;
                _lastIdlingCheck = DateTime.UtcNow;

                UIApplication uiApp = sender as UIApplication;
                if (uiApp == null) return;

                UIDocument uidoc = uiApp.ActiveUIDocument;
                if (uidoc == null || uidoc.Document == null || !uidoc.Document.IsValidObject) return;

                // Capture current selection and its millimetric 3D bounding box
                ICollection<ElementId> selIds = uidoc.Selection.GetElementIds();
                var selectedUniqueIds = new List<string>();
                RadarBoundingBox selBounds = null;

                if (selIds != null && selIds.Count > 0)
                {
                    Document doc = uidoc.Document;
                    foreach (var id in selIds)
                    {
                        Element elem = doc.GetElement(id);
                        if (elem == null || !elem.IsValidObject) continue;
                        selectedUniqueIds.Add(elem.UniqueId);

                        BoundingBoxXYZ box = elem.get_BoundingBox(null);
                        if (box != null)
                        {
                            double minX = box.Min.X * FEET_TO_MM;
                            double minY = box.Min.Y * FEET_TO_MM;
                            double minZ = box.Min.Z * FEET_TO_MM;
                            double maxX = box.Max.X * FEET_TO_MM;
                            double maxY = box.Max.Y * FEET_TO_MM;
                            double maxZ = box.Max.Z * FEET_TO_MM;

                            if (selBounds == null)
                            {
                                selBounds = new RadarBoundingBox
                                {
                                    min = new Point3D { x = minX, y = minY, z = minZ },
                                    max = new Point3D { x = maxX, y = maxY, z = maxZ }
                                };
                            }
                            else
                            {
                                selBounds.min.x = Math.Min(selBounds.min.x, minX);
                                selBounds.min.y = Math.Min(selBounds.min.y, minY);
                                selBounds.min.z = Math.Min(selBounds.min.z, minZ);
                                selBounds.max.x = Math.Max(selBounds.max.x, maxX);
                                selBounds.max.y = Math.Max(selBounds.max.y, maxY);
                                selBounds.max.z = Math.Max(selBounds.max.z, maxZ);
                            }
                        }
                    }
                }

                _selectedUniqueIds = selectedUniqueIds;
                _selectionBounds = selBounds;

                SendHeartbeatAsync(uidoc.Document, isImmediate: false);
            }
            catch (Exception ex)
            {
                Log.Write("[RevitLiveRadar] Idling loop error: " + ex.Message);
            }
        }

        // =========================================================================
        // 2. CANONICAL MODEL IDENTIFIER
        // =========================================================================

        /// <summary>
        /// Hybrid central model identifier: ACC Cloud vs LAN Server vs Standalone RVT
        /// </summary>
        public static string GetCanonicalModelId(Document doc)
        {
            if (doc == null || !doc.IsValidObject) return "LOCAL:Untitled";

            try
            {
                // 1. Autodesk Construction Cloud (ACC / BIM 360)
                if (doc.IsModelInCloud)
                {
                    ModelPath cloudPath = doc.GetCloudModelPath();
                    Guid guid = cloudPath.GetModelGUID();
                    return $"ACC:{guid}:{doc.Title}";
                }

                // 2. Local Office Network Server Worksharing
                if (doc.IsWorkshared)
                {
                    ModelPath centralPath = doc.GetWorksharingCentralModelPath();
                    string userVisible = ModelPathUtils.ConvertModelPathToUserVisiblePath(centralPath);
                    string fileName = Path.GetFileName(userVisible);
                    return $"LAN:{fileName}";
                }
            }
            catch (Exception ex)
            {
                Log.Write("[RevitLiveRadar] GetCanonicalModelId fallback: " + ex.Message);
            }

            // 3. Standalone RVT File Fallback
            return $"LOCAL:{(string.IsNullOrEmpty(doc.Title) ? "Untitled" : doc.Title)}";
        }

        // =========================================================================
        // 3. TWO-WAY HEARTBEAT POST & REACTION
        // =========================================================================

        private static void SendHeartbeatAsync(Document doc, bool isImmediate)
        {
            Task.Run(async () =>
            {
                try
                {
                    string canonicalId = GetCanonicalModelId(doc);
                    string userName = doc.Application.Username;
                    string machineName = Environment.MachineName;
                    string revitVersion = doc.Application.VersionNumber;

                    RadarBoundingBox combinedBox = null;
                    List<string> dirtyList = null;
                    List<string> selectedList = null;

                    lock (_stateLock)
                    {
                        dirtyList = new List<string>(_dirtyUniqueIds);
                        selectedList = new List<string>(_selectedUniqueIds);

                        // Combine dirty bounds and selection bounds
                        if (_dirtyBounds != null && _selectionBounds != null)
                        {
                            combinedBox = new RadarBoundingBox
                            {
                                min = new Point3D
                                {
                                    x = Math.Min(_dirtyBounds.min.x, _selectionBounds.min.x),
                                    y = Math.Min(_dirtyBounds.min.y, _selectionBounds.min.y),
                                    z = Math.Min(_dirtyBounds.min.z, _selectionBounds.min.z)
                                },
                                max = new Point3D
                                {
                                    x = Math.Max(_dirtyBounds.max.x, _selectionBounds.max.x),
                                    y = Math.Max(_dirtyBounds.max.y, _selectionBounds.max.y),
                                    z = Math.Max(_dirtyBounds.max.z, _selectionBounds.max.z)
                                }
                            };
                        }
                        else
                        {
                            combinedBox = _dirtyBounds ?? _selectionBounds;
                        }
                    }

                    var payload = new Dictionary<string, object>
                    {
                        ["userId"] = userName,
                        ["userName"] = userName,
                        ["machineName"] = machineName,
                        ["revitVersion"] = revitVersion,
                        ["canonicalModelId"] = canonicalId,
                        ["docTitle"] = doc.Title ?? "Revit Project",
                        ["storageType"] = canonicalId.StartsWith("ACC:") ? "ACC Cloud" : (canonicalId.StartsWith("LAN:") ? "LAN Server" : "Local File"),
                        ["syncStatus"] = _syncStatus,
                        ["lastSyncTimeUtc"] = _lastSyncTime.ToString("o"),
                        ["dirtyCount"] = dirtyList.Count,
                        ["dirtyUniqueIds"] = dirtyList,
                        ["selectedUniqueIds"] = selectedList,
                        ["activeViewId"] = _activeViewId,
                        ["activeViewName"] = _activeViewName,
                        ["activeViewType"] = _activeViewType,
                        ["activeLevel"] = _activeLevel,
                        ["activeSheet"] = _activeSheet,
                        ["activeCategory"] = _lastActiveCategory,
                        ["combinedBoundingBox"] = combinedBox != null ? new Dictionary<string, object>
                        {
                            ["min"] = new Dictionary<string, object> { ["x"] = combinedBox.min.x, ["y"] = combinedBox.min.y, ["z"] = combinedBox.min.z },
                            ["max"] = new Dictionary<string, object> { ["x"] = combinedBox.max.x, ["y"] = combinedBox.max.y, ["z"] = combinedBox.max.z }
                        } : null
                    };

                    string jsonBody = Json.Stringify(payload);
                    var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

                    string endpoint = $"{_controlRoomBaseUrl}/api/revit-radar/heartbeat";
                    var response = await _httpClient.PostAsync(endpoint, content).ConfigureAwait(false);

                    if (response.IsSuccessStatusCode)
                    {
                        string respText = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var parsed = Json.Parse(respText) as Dictionary<string, object>;

                        if (parsed != null)
                        {
                            var centralLock = parsed.ContainsKey("centralLock") ? parsed["centralLock"] as Dictionary<string, object> : null;
                            var alerts = parsed.ContainsKey("myAlerts") ? parsed["myAlerts"] as List<object> : null;

                            UpdateWpfToastUi(doc.Title, centralLock, alerts);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // Fail silently during network drops so Revit modeling never hangs
                    Log.Write("[RevitLiveRadar] Heartbeat silent fail (offline): " + ex.Message);
                }
            });
        }

        // =========================================================================
        // 4. NON-BLOCKING WPF STATUS TOAST & BANNER
        // =========================================================================

        private static void UpdateWpfToastUi(string docTitle, Dictionary<string, object> centralLock, List<object> alerts)
        {
            try
            {
                Application.Current?.Dispatcher?.BeginInvoke(new Action(() =>
                {
                    if (_toastWindow == null)
                    {
                        _toastWindow = new RevitRadarToastWindow();
                        _toastWindow.Show();
                    }

                    _toastWindow.UpdateState(docTitle, centralLock, alerts, _highlightEvent, _highlightHandler);
                }));
            }
            catch { }
        }

        /// <summary>
        /// Highlight & isolate teammate's conflicting elements in active viewport
        /// </summary>
        public static void HighlightTeammateElements(IEnumerable<string> uniqueIds)
        {
            if (_highlightHandler == null || _highlightEvent == null) return;
            _highlightHandler.TargetUniqueIds = new List<string>(uniqueIds);
            _highlightEvent.Raise();
        }
    }

    // =========================================================================
    // 5. EXTERNAL EVENT: HIGHLIGHT / ISOLATE TEAMMATE'S EDITS
    // =========================================================================

    public class HighlightTeammateEditsHandler : IExternalEventHandler
    {
        public List<string> TargetUniqueIds { get; set; } = new List<string>();

        public void Execute(UIApplication app)
        {
            try
            {
                UIDocument uidoc = app.ActiveUIDocument;
                if (uidoc == null || uidoc.Document == null) return;
                if (TargetUniqueIds == null || TargetUniqueIds.Count == 0) return;

                Document doc = uidoc.Document;
                var elementIds = new List<ElementId>();

                foreach (var uid in TargetUniqueIds)
                {
                    Element elem = doc.GetElement(uid);
                    if (elem != null && elem.IsValidObject)
                    {
                        elementIds.Add(elem.Id);
                    }
                }

                if (elementIds.Count > 0)
                {
                    uidoc.Selection.SetElementIds(elementIds);
                    uidoc.ShowElements(elementIds);
                    Log.Write($"[RevitLiveRadar] Selected & focused {elementIds.Count} teammate elements in viewport.");
                }
            }
            catch (Exception ex)
            {
                Log.Write("[RevitLiveRadar] HighlightTeammateEditsHandler error: " + ex.Message);
            }
        }

        public string GetName() => "RevitLiveRadarHighlightHandler";
    }

    // =========================================================================
    // 6. LIGHTWEIGHT NON-BLOCKING WPF STATUS BAR / TOAST WINDOW
    // =========================================================================

    public class RevitRadarToastWindow : Window
    {
        private static SolidColorBrush Brush(string hex) => new SolidColorBrush((WpfColor)WpfColorConverter.ConvertFromString(hex));

        private readonly Border _mainBorder;
        private readonly WpfEllipse _statusLed;
        private readonly TextBlock _txtTrafficTitle;
        private readonly TextBlock _txtTrafficDesc;
        private readonly StackPanel _alertsPanel;
        private readonly Button _btnHighlight;
        private List<string> _currentAlertUniqueIds = new List<string>();
        private ExternalEvent _externalEvent;
        private HighlightTeammateEditsHandler _externalHandler;

        public RevitRadarToastWindow()
        {
            Title = "BIM Live Coordination Radar";
            Width = 340;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            Focusable = false;
            ResizeMode = ResizeMode.NoResize;

            // Anchor to top-right of the primary screen
            Left = SystemParameters.WorkArea.Right - Width - 24;
            Top = SystemParameters.WorkArea.Top + 24;

            // Obsidian Neon Shell
            _mainBorder = new Border
            {
                Background = Brush("#080C14"),
                BorderBrush = Brush("#10b981"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(12),
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black,
                    BlurRadius = 16,
                    ShadowDepth = 6,
                    Opacity = 0.85
                }
            };

            var rootStack = new StackPanel { Orientation = Orientation.Vertical };

            // Header Bar
            var headerGrid = new WpfGrid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            _statusLed = new WpfEllipse
            {
                Width = 10,
                Height = 10,
                Fill = Brush("#10b981"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            WpfGrid.SetColumn(_statusLed, 0);

            var titleBlock = new TextBlock
            {
                Text = "REVIT RADAR // MULTI-LAN",
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#00e5ff"),
                FontFamily = new FontFamily("Consolas, monospace")
            };
            WpfGrid.SetColumn(titleBlock, 1);

            var btnClose = new Button
            {
                Content = "\u2715",
                FontSize = 11,
                Foreground = Brushes.Gray,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Padding = new Thickness(4, 0, 4, 0)
            };
            btnClose.Click += (s, e) => this.Hide();
            WpfGrid.SetColumn(btnClose, 2);

            headerGrid.Children.Add(_statusLed);
            headerGrid.Children.Add(titleBlock);
            headerGrid.Children.Add(btnClose);
            rootStack.Children.Add(headerGrid);

            // Traffic Light Status
            _txtTrafficTitle = new TextBlock
            {
                Text = "CENTRAL FREE — SAFE TO SYNC",
                FontSize = 12,
                FontWeight = FontWeights.Black,
                Foreground = Brush("#10b981"),
                FontFamily = new FontFamily("Consolas, monospace"),
                Margin = new Thickness(0, 8, 0, 2)
            };
            rootStack.Children.Add(_txtTrafficTitle);

            _txtTrafficDesc = new TextBlock
            {
                Text = "Central model is unlocked.",
                FontSize = 11,
                Foreground = Brush("#94a3b8"),
                TextWrapping = TextWrapping.Wrap
            };
            rootStack.Children.Add(_txtTrafficDesc);

            // Dynamic Alerts Stack
            _alertsPanel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 6, 0, 0),
                Visibility = WpfVisibility.Collapsed
            };
            rootStack.Children.Add(_alertsPanel);

            // Action Button: Highlight conflicting elements
            _btnHighlight = new Button
            {
                Content = "\u25CE Highlight Teammate's Edits",
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = Brush("#00e5ff"),
                Background = Brush("#0b1928"),
                BorderBrush = Brush("#00e5ff"),
                BorderThickness = new Thickness(1),
                Margin = new Thickness(0, 8, 0, 0),
                Padding = new Thickness(8, 4, 8, 4),
                Cursor = System.Windows.Input.Cursors.Hand,
                Visibility = WpfVisibility.Collapsed
            };
            _btnHighlight.Click += (s, e) =>
            {
                if (_externalHandler != null && _externalEvent != null && _currentAlertUniqueIds.Count > 0)
                {
                    _externalHandler.TargetUniqueIds = new List<string>(_currentAlertUniqueIds);
                    _externalEvent.Raise();
                }
            };
            rootStack.Children.Add(_btnHighlight);

            _mainBorder.Child = rootStack;
            Content = _mainBorder;
        }

        public void UpdateState(string docTitle, Dictionary<string, object> centralLock, List<object> alerts, ExternalEvent extEvent, HighlightTeammateEditsHandler extHandler)
        {
            _externalEvent = extEvent;
            _externalHandler = extHandler;

            bool isLocked = false;
            string lockedBy = "";
            int elapsed = 0;

            if (centralLock != null)
            {
                isLocked = Convert.ToBoolean(centralLock.ContainsKey("isLocked") ? centralLock["isLocked"] : false);
                lockedBy = Convert.ToString(centralLock.ContainsKey("lockedBy") ? centralLock["lockedBy"] : "");
                elapsed = Convert.ToInt32(centralLock.ContainsKey("elapsedSeconds") ? centralLock["elapsedSeconds"] : 0);
            }

            if (isLocked)
            {
                _mainBorder.BorderBrush = Brush("#ef4444");
                _statusLed.Fill = Brush("#ef4444");
                _txtTrafficTitle.Text = $"HOLD SYNC \u2014 {lockedBy.ToUpper()} SYNCING";
                _txtTrafficTitle.Foreground = Brush("#ef4444");
                _txtTrafficDesc.Text = $"{lockedBy} is synchronizing ({elapsed}s elapsed). Please hold your sync.";
            }
            else
            {
                _mainBorder.BorderBrush = Brush("#10b981");
                _statusLed.Fill = Brush("#10b981");
                _txtTrafficTitle.Text = "CENTRAL FREE — SAFE TO SYNC";
                _txtTrafficTitle.Foreground = Brush("#10b981");
                _txtTrafficDesc.Text = "Central model is free. Safe to synchronize.";
            }

            // Populate Alerts & Highlight button
            _alertsPanel.Children.Clear();
            _currentAlertUniqueIds.Clear();

            if (alerts != null && alerts.Count > 0)
            {
                _alertsPanel.Visibility = WpfVisibility.Visible;

                foreach (var item in alerts)
                {
                    var alert = item as Dictionary<string, object>;
                    if (alert == null) continue;

                    string msg = Convert.ToString(alert.ContainsKey("message") ? alert["message"] : "");
                    string sev = Convert.ToString(alert.ContainsKey("severity") ? alert["severity"] : "INFO");

                    var alertBox = new TextBlock
                    {
                        Text = $"\u25B6 [{sev}] {msg}",
                        FontSize = 10,
                        Foreground = sev == "CRITICAL" ? Brush("#f87171") : Brush("#fbbf24"),
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 3, 0, 0)
                    };
                    _alertsPanel.Children.Add(alertBox);

                    if (alert.ContainsKey("conflictingUniqueIds"))
                    {
                        var ids = alert["conflictingUniqueIds"] as List<object>;
                        if (ids != null)
                        {
                            foreach (var id in ids) _currentAlertUniqueIds.Add(Convert.ToString(id));
                        }
                    }
                }

                _btnHighlight.Visibility = _currentAlertUniqueIds.Count > 0 ? WpfVisibility.Visible : WpfVisibility.Collapsed;
            }
            else
            {
                _alertsPanel.Visibility = WpfVisibility.Collapsed;
                _btnHighlight.Visibility = WpfVisibility.Collapsed;
            }

            if (!IsVisible) Show();
        }
    }

    // =========================================================================
    // 7. DATA DTOs
    // =========================================================================

    public class Point3D
    {
        public double x { get; set; }
        public double y { get; set; }
        public double z { get; set; }

        public Point3D() { }
        public Point3D(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
    }

    public class RadarBoundingBox
    {
        public Point3D min { get; set; } = new Point3D();
        public Point3D max { get; set; } = new Point3D();
    }
}
