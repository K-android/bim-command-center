using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using BIMCommandCenter.Core.Infrastructure;

namespace BIMCommandCenter.Core.Revit
{
    public static class DynamoPlayerHelper
    {
        /// <summary>
        /// Registers a directory in DynamoSettings.xml under both DynamoPlayerFolderGroups
        /// AND TrustedFolderLocations (Dynamo 3.x in Revit 2025) across all installed Dynamo Revit
        /// versions so Dynamo Player lists and allows running graphs from the central folder.
        /// </summary>
        public static void EnsureFolderInDynamoSettings(string folderPath, string displayName = "Central Library")
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

            try
            {
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

                var searchDirs = new[]
                {
                    Path.Combine(roaming, "Dynamo", "Dynamo Revit"),
                    Path.Combine(roaming, "Dynamo", "Dynamo Core"),
                    Path.Combine(common, "Dynamo", "Dynamo Revit"),
                    Path.Combine(common, "Dynamo", "Dynamo Core")
                };

                string normalizedFolder = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

                foreach (var sDir in searchDirs)
                {
                    if (!Directory.Exists(sDir)) continue;

                    foreach (var settingsFile in Directory.GetFiles(sDir, "DynamoSettings.xml", SearchOption.AllDirectories))
                    {
                        try
                        {
                            UpdateSettingsXml(settingsFile, normalizedFolder, displayName);
                        }
                        catch (Exception ex)
                        {
                            Log.Write("DynamoPlayerHelper: Failed updating " + settingsFile + ": " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("DynamoPlayerHelper: EnsureFolderInDynamoSettings error: " + ex.Message);
            }
        }

        private static void UpdateSettingsXml(string filePath, string normalizedFolder, string displayName)
        {
            if (!File.Exists(filePath)) return;

            var doc = new XmlDocument();
            doc.Load(filePath);
            var root = doc.DocumentElement;
            if (root == null) return;

            bool isModified = false;

            // 1. Ensure folder under DynamoPlayerFolderGroups -> Revit -> Folders
            XmlNode folderGroupsNode = root.SelectSingleNode("DynamoPlayerFolderGroups");
            if (folderGroupsNode == null)
            {
                folderGroupsNode = doc.CreateElement("DynamoPlayerFolderGroups");
                root.AppendChild(folderGroupsNode);
                isModified = true;
            }

            XmlNode revitGroupNode = null;
            XmlNodeList existingGroups = folderGroupsNode.SelectNodes("DynamoPlayerFolderGroup");
            if (existingGroups != null)
            {
                foreach (XmlNode group in existingGroups)
                {
                    var entry = group.SelectSingleNode("EntryPoint");
                    if (entry != null && string.Equals(entry.InnerText, "Revit", StringComparison.OrdinalIgnoreCase))
                    {
                        revitGroupNode = group;
                        break;
                    }
                }
            }

            if (revitGroupNode == null)
            {
                revitGroupNode = doc.CreateElement("DynamoPlayerFolderGroup");
                var ep = doc.CreateElement("EntryPoint");
                ep.InnerText = "Revit";
                revitGroupNode.AppendChild(ep);

                var fNode = doc.CreateElement("Folders");
                revitGroupNode.AppendChild(fNode);
                folderGroupsNode.AppendChild(revitGroupNode);
                isModified = true;
            }

            XmlNode foldersNode = revitGroupNode.SelectSingleNode("Folders");
            if (foldersNode == null)
            {
                foldersNode = doc.CreateElement("Folders");
                revitGroupNode.AppendChild(foldersNode);
                isModified = true;
            }

            // Check if folder is already present in foldersNode
            bool folderFound = false;
            XmlNodeList existingFolders = foldersNode.SelectNodes("DynamoPlayerFolder/Path");
            if (existingFolders != null)
            {
                foreach (XmlNode pNode in existingFolders)
                {
                    if (string.Equals(pNode.InnerText?.TrimEnd('\\', '/'), normalizedFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        folderFound = true;
                        break;
                    }
                }
            }

            if (!folderFound)
            {
                XmlElement folderElem = doc.CreateElement("DynamoPlayerFolder");

                XmlElement pathElem = doc.CreateElement("Path");
                pathElem.InnerText = normalizedFolder;
                folderElem.AppendChild(pathElem);

                XmlElement nameElem = doc.CreateElement("DisplayName");
                nameElem.InnerText = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(normalizedFolder) : displayName;
                folderElem.AppendChild(nameElem);

                XmlElement idElem = doc.CreateElement("Id");
                idElem.InnerText = Guid.NewGuid().ToString();
                folderElem.AppendChild(idElem);

                XmlElement remElem = doc.CreateElement("IsRemovable");
                remElem.InnerText = "true";
                folderElem.AppendChild(remElem);

                XmlElement orderElem = doc.CreateElement("Order");
                orderElem.InnerText = "1";
                folderElem.AppendChild(orderElem);

                XmlElement validElem = doc.CreateElement("IsValid");
                validElem.InnerText = "true";
                folderElem.AppendChild(validElem);

                XmlElement defElem = doc.CreateElement("IsDefault");
                defElem.InnerText = "false";
                folderElem.AppendChild(defElem);

                foldersNode.AppendChild(folderElem);
                isModified = true;
            }

            // 2. Ensure folder in TrustedFolderLocations (Crucial for Dynamo 3.0+ in Revit 2025!)
            XmlNode trustedNode = root.SelectSingleNode("TrustedFolderLocations");
            if (trustedNode == null)
            {
                trustedNode = doc.CreateElement("TrustedFolderLocations");
                root.AppendChild(trustedNode);
                isModified = true;
            }

            bool trustedFound = false;
            XmlNodeList existingTrusted = trustedNode.SelectNodes("string");
            if (existingTrusted != null)
            {
                foreach (XmlNode tNode in existingTrusted)
                {
                    if (string.Equals(tNode.InnerText?.TrimEnd('\\', '/'), normalizedFolder, StringComparison.OrdinalIgnoreCase))
                    {
                        trustedFound = true;
                        break;
                    }
                }
            }

            if (!trustedFound)
            {
                XmlElement stringElem = doc.CreateElement("string");
                stringElem.InnerText = normalizedFolder;
                trustedNode.AppendChild(stringElem);
                isModified = true;
            }

            // Also trust parent directory if available
            string parentDir = Path.GetDirectoryName(normalizedFolder);
            if (!string.IsNullOrEmpty(parentDir) && Directory.Exists(parentDir))
            {
                string normParent = parentDir.TrimEnd('\\', '/');
                bool parentTrusted = false;
                if (existingTrusted != null)
                {
                    foreach (XmlNode tNode in existingTrusted)
                    {
                        if (string.Equals(tNode.InnerText?.TrimEnd('\\', '/'), normParent, StringComparison.OrdinalIgnoreCase))
                        {
                            parentTrusted = true;
                            break;
                        }
                    }
                }
                if (!parentTrusted)
                {
                    XmlElement parentElem = doc.CreateElement("string");
                    parentElem.InnerText = normParent;
                    trustedNode.AppendChild(parentElem);
                    isModified = true;
                }
            }

            // 3. Disable trust warnings so graphs run smoothly without blocking dialogs
            XmlNode warnNode = root.SelectSingleNode("DisableTrustWarnings");
            if (warnNode == null)
            {
                warnNode = doc.CreateElement("DisableTrustWarnings");
                warnNode.InnerText = "true";
                root.AppendChild(warnNode);
                isModified = true;
            }
            else if (!string.Equals(warnNode.InnerText, "true", StringComparison.OrdinalIgnoreCase))
            {
                warnNode.InnerText = "true";
                isModified = true;
            }

            if (isModified)
            {
                doc.Save(filePath);
                Log.Write($"DynamoPlayerHelper: Registered folder '{normalizedFolder}' & trusted locations in {filePath}");
            }
        }

        /// <summary>
        /// Finds the active HTTP port that Dynamo Player's in-process server is listening on.
        /// Searches across loaded DynamoPlayer assemblies, active TCP listeners on loopback,
        /// and fallback port ranges.
        /// </summary>
        public static int FindActivePlayerServerPort()
        {
            // 1. In-process reflection via DynamoPlayer assemblies
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string asmName = asm.FullName ?? "";
                    if (asmName.IndexOf("DynamoPlayer", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        foreach (var type in asm.GetTypes())
                        {
                            if (type.Name.IndexOf("PlayerServer", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                type.Name.IndexOf("Server", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                                {
                                    if (prop.PropertyType == typeof(int) && prop.Name.IndexOf("Port", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        int p = (int)prop.GetValue(null);
                                        if (p > 0 && IsPortListening(p)) return p;
                                    }
                                }
                                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                                {
                                    if (field.FieldType == typeof(int) && field.Name.IndexOf("port", StringComparison.OrdinalIgnoreCase) >= 0)
                                    {
                                        int p = (int)field.GetValue(null);
                                        if (p > 0 && IsPortListening(p)) return p;
                                    }
                                    if (typeof(IEnumerable).IsAssignableFrom(field.FieldType))
                                    {
                                        var list = field.GetValue(null) as IEnumerable;
                                        if (list != null)
                                        {
                                            foreach (var s in list)
                                            {
                                                if (s == null) continue;
                                                var pPort = s.GetType().GetProperty("Port")?.GetValue(s) as int?;
                                                if (pPort.HasValue && pPort.Value > 0 && IsPortListening(pPort.Value))
                                                    return pPort.Value;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("DynamoPlayerHelper: Port reflection check exception: " + ex.Message);
            }

            // 2. Scan active TCP listeners in system (standard .NET System.Net.NetworkInformation)
            try
            {
                var listeners = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();
                foreach (var ep in listeners)
                {
                    if (IPAddress.IsLoopback(ep.Address) && ep.Port >= 50000 && ep.Port <= 60000)
                    {
                        if (IsPortListening(ep.Port)) return ep.Port;
                    }
                }
            }
            catch { }

            // 3. Fallback: Quick scan of common Dynamo Player ports (55100-55135)
            for (int p = 55100; p <= 55135; p++)
            {
                if (IsPortListening(p)) return p;
            }

            return -1;
        }

        private static bool IsPortListening(int port)
        {
            try
            {
                using (var tcp = new TcpClient())
                {
                    var task = tcp.ConnectAsync("127.0.0.1", port);
                    if (Task.WhenAny(task, Task.Delay(35)).Result == task && tcp.Connected)
                    {
                        return true;
                    }
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Attempts to invoke the Dynamo Player command directly via Revit's Ribbon ComponentManager.
        /// Useful in Revit 2025 when focus is hosted in WebView2 and CanPostCommand is restrictive.
        /// </summary>
        public static bool TryInvokeRibbonDynamoPlayer()
        {
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.FullName != null && asm.FullName.IndexOf("AdWindows", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        var tCompMgr = asm.GetType("Autodesk.Windows.ComponentManager");
                        if (tCompMgr != null)
                        {
                            var mSend = tCompMgr.GetMethod("SendCommand", new[] { typeof(string) });
                            if (mSend != null)
                            {
                                mSend.Invoke(null, new object[] { "ID_PLAYLIST_DYNAMO" });
                                Log.Write("DynamoPlayerHelper: Successfully invoked ID_PLAYLIST_DYNAMO via ComponentManager.SendCommand");
                                return true;
                            }

                            var pRibbon = tCompMgr.GetProperty("Ribbon", BindingFlags.Public | BindingFlags.Static);
                            var ribbon = pRibbon?.GetValue(null);
                            if (ribbon != null)
                            {
                                var pTabs = ribbon.GetType().GetProperty("Tabs");
                                var tabs = pTabs?.GetValue(ribbon) as IEnumerable;
                                if (tabs != null)
                                {
                                    foreach (var tab in tabs)
                                    {
                                        var pPanels = tab.GetType().GetProperty("Panels");
                                        var panels = pPanels?.GetValue(tab) as IEnumerable;
                                        if (panels != null)
                                        {
                                            foreach (var panel in panels)
                                            {
                                                var pSource = panel.GetType().GetProperty("Source");
                                                var source = pSource?.GetValue(panel);
                                                if (source != null)
                                                {
                                                    var pItems = source.GetType().GetProperty("Items");
                                                    var items = pItems?.GetValue(source) as IEnumerable;
                                                    if (items != null)
                                                    {
                                                        foreach (var item in items)
                                                        {
                                                            string id = item.GetType().GetProperty("Id")?.GetValue(item) as string;
                                                            string text = item.GetType().GetProperty("Text")?.GetValue(item) as string;
                                                            if ((id != null && id.IndexOf("DynamoPlayer", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                                                (text != null && text.IndexOf("Dynamo Player", StringComparison.OrdinalIgnoreCase) >= 0))
                                                            {
                                                                var pHandler = item.GetType().GetProperty("CommandHandler");
                                                                var handler = pHandler?.GetValue(item);
                                                                if (handler != null)
                                                                {
                                                                    var mExec = handler.GetType().GetMethod("Execute", new[] { typeof(object) });
                                                                    if (mExec != null)
                                                                    {
                                                                        mExec.Invoke(handler, new object[] { item });
                                                                        Log.Write("DynamoPlayerHelper: Successfully executed Dynamo Player Ribbon Item CommandHandler");
                                                                        return true;
                                                                    }
                                                                }
                                                            }
                                                        }
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("DynamoPlayerHelper: TryInvokeRibbonDynamoPlayer error: " + ex.Message);
            }
            return false;
        }

        /// <summary>
        /// Asynchronously notifies Dynamo Player to directly open the specified graph in its Inputs/Outputs panel.
        /// Retries across multiple intervals as Dynamo Player initializes.
        /// </summary>
        public static async Task<bool> TryOpenGraphInDynamoPlayerAsync(string dynPath, int maxAttempts = 35, int delayMs = 200)
        {
            if (string.IsNullOrWhiteSpace(dynPath) || !File.Exists(dynPath)) return false;
            string fullPath = Path.GetFullPath(dynPath);

            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) })
            {
                for (int attempt = 1; attempt <= maxAttempts; attempt++)
                {
                    int port = FindActivePlayerServerPort();
                    if (port > 0)
                    {
                        try
                        {
                            string url = $"http://localhost:{port}/graph/open";
                            string json = "{\"path\":" + Json.Stringify(fullPath) + ",\"forceReopen\":true}";
                            var content = new StringContent(json, Encoding.UTF8, "application/json");

                            var response = await http.PostAsync(url, content).ConfigureAwait(false);
                            if (response.IsSuccessStatusCode)
                            {
                                Log.Write($"DynamoPlayerHelper: Graph successfully opened on port {port} -> {fullPath}");
                                return true;
                            }
                            else
                            {
                                Log.Write($"DynamoPlayerHelper: Attempt {attempt} returned status {response.StatusCode}");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Write($"DynamoPlayerHelper: PostGraphOpen attempt {attempt} on port {port} error: {ex.Message}");
                        }
                    }

                    await Task.Delay(delayMs).ConfigureAwait(false);
                }
            }

            Log.Write($"DynamoPlayerHelper: Could not open {fullPath} within {maxAttempts * delayMs}ms");
            return false;
        }
    }
}
