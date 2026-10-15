using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
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
        /// Registers a directory in DynamoSettings.xml under DynamoPlayerFolderGroups
        /// across all installed Dynamo Revit versions so Dynamo Player lists the folder.
        /// </summary>
        public static void EnsureFolderInDynamoSettings(string folderPath, string displayName = "Central Library")
        {
            if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath)) return;

            try
            {
                string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string dynamoRevitDir = Path.Combine(roaming, "Dynamo", "Dynamo Revit");
                if (!Directory.Exists(dynamoRevitDir)) return;

                string normalizedFolder = Path.GetFullPath(folderPath).TrimEnd('\\', '/');

                foreach (var settingsFile in Directory.GetFiles(dynamoRevitDir, "DynamoSettings.xml", SearchOption.AllDirectories))
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
            catch (Exception ex)
            {
                Log.Write("DynamoPlayerHelper: EnsureFolderInDynamoSettings error: " + ex.Message);
            }
        }

        private static void UpdateSettingsXml(string filePath, string normalizedFolder, string displayName)
        {
            string content = File.ReadAllText(filePath);
            if (content.IndexOf(normalizedFolder, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Already registered
                return;
            }

            var doc = new XmlDocument();
            doc.Load(filePath);
            var root = doc.DocumentElement;
            if (root == null) return;

            XmlNode folderGroupsNode = root.SelectSingleNode("DynamoPlayerFolderGroups");
            if (folderGroupsNode == null)
            {
                folderGroupsNode = doc.CreateElement("DynamoPlayerFolderGroups");
                root.AppendChild(folderGroupsNode);
            }

            // Find or create Revit group
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
            }

            XmlNode foldersNode = revitGroupNode.SelectSingleNode("Folders");
            if (foldersNode == null)
            {
                foldersNode = doc.CreateElement("Folders");
                revitGroupNode.AppendChild(foldersNode);
            }

            // Create new DynamoPlayerFolder node
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

            doc.Save(filePath);
            Log.Write($"DynamoPlayerHelper: Registered folder '{normalizedFolder}' in {filePath}");
        }

        /// <summary>
        /// Finds the active HTTP port that Dynamo Player's in-process server is listening on.
        /// </summary>
        public static int FindActivePlayerServerPort()
        {
            // 1. In-process reflection via DynamoPlayer.Server.PlayerServer
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (asm.FullName != null && asm.FullName.StartsWith("DynamoPlayer.Server", StringComparison.OrdinalIgnoreCase))
                    {
                        var tServer = asm.GetType("DynamoPlayer.PlayerServer");
                        if (tServer != null)
                        {
                            var fAll = tServer.GetField("_allPlayerServers", BindingFlags.NonPublic | BindingFlags.Static);
                            if (fAll != null)
                            {
                                var list = fAll.GetValue(null) as IEnumerable;
                                if (list != null)
                                {
                                    foreach (var s in list)
                                    {
                                        if (s == null) continue;
                                        var pPort = tServer.GetProperty("Port")?.GetValue(s) as int?;
                                        if (pPort.HasValue && pPort.Value > 0)
                                        {
                                            return pPort.Value;
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

            // 2. Fallback: Quick scan of common Dynamo Player ports (55100-55115)
            for (int p = 55100; p <= 55115; p++)
            {
                try
                {
                    using (var tcp = new TcpClient())
                    {
                        var task = tcp.ConnectAsync("127.0.0.1", p);
                        if (Task.WhenAny(task, Task.Delay(40)).Result == task && tcp.Connected)
                        {
                            return p;
                        }
                    }
                }
                catch { }
            }

            return -1;
        }

        /// <summary>
        /// Asynchronously notifies Dynamo Player to directly open the specified graph in its Inputs/Outputs panel.
        /// Retries across multiple intervals as Dynamo Player initializes.
        /// </summary>
        public static async Task<bool> TryOpenGraphInDynamoPlayerAsync(string dynPath, int maxAttempts = 30, int delayMs = 200)
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
