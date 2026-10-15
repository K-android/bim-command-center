using System;
using System.Collections.Generic;
using System.IO;

namespace BIMCommandCenter.Core.Infrastructure
{
    /// <summary>
    /// Settings loaded from config.json. Lookup order:
    ///   1. %AppData%\BIMCommandCenter\config.json   (per-user override, optional)
    ///   2. &lt;core folder&gt;\config.json                (shipped by installer / central update)
    /// </summary>
    public class AppConfig
    {
        public string CatalogSource { get; private set; }
        public string CatalogSheet { get; private set; }
        public string ScriptsRoot { get; private set; }
        public string TelemetryFolder { get; private set; }
        public string KnowledgeGraphPath { get; private set; }
        public string IdeasEndpoint { get; private set; }
        public string IdeasFolder { get; private set; }
        public string FrontendUrl { get; private set; }
        public string AiEndpoint { get; private set; }
        public int CacheMinutes { get; private set; }
        public string LoadedFrom { get; private set; }

        public static string CoreDir { get; } = Path.GetDirectoryName(typeof(AppConfig).Assembly.Location);

        public static string LocalDataDir
        {
            get
            {
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BIMCommandCenter");
                Directory.CreateDirectory(dir);
                return dir;
            }
        }

        public static AppConfig Load()
        {
            string userOverride = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMCommandCenter", "config.json");
            string bundled = Path.Combine(CoreDir, "config.json");
            string path = File.Exists(userOverride) ? userOverride : bundled;

            Dictionary<string, object> d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(path)) d = Json.AsObject(Json.Parse(File.ReadAllText(path))) ?? d;
            }
            catch (Exception ex)
            {
                Log.Write("Config parse failed (" + path + "): " + ex.Message);
            }

            int cache;
            if (!int.TryParse(d.Str("cacheMinutes", "30"), out cache)) cache = 30;

            string configuredScripts = d.Str("scriptsRoot", "");
            string defaultScripts = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMCommandCenter", "scripts");
            string scriptsRoot = !string.IsNullOrWhiteSpace(configuredScripts) ? Expand(configuredScripts) : defaultScripts;

            try
            {
                Directory.CreateDirectory(scriptsRoot);
                Directory.CreateDirectory(Path.Combine(scriptsRoot, "Dynamo"));
                Directory.CreateDirectory(Path.Combine(scriptsRoot, "PyRevit"));
                Directory.CreateDirectory(Path.Combine(scriptsRoot, "Custom Addins"));
                Directory.CreateDirectory(Path.Combine(scriptsRoot, "Grasshopper"));
            }
            catch { }

            return new AppConfig
            {
                CatalogSource = Expand(d.Str("catalogSource", "data/sample_catalog.json")),
                CatalogSheet = d.Str("catalogSheet", ""),
                ScriptsRoot = scriptsRoot,
                TelemetryFolder = Expand(d.Str("telemetryFolder", "")),
                KnowledgeGraphPath = Expand(d.Str("knowledgeGraphPath", "")),
                IdeasEndpoint = d.Str("ideasEndpoint", ""),
                IdeasFolder = Expand(d.Str("ideasFolder", "")),
                FrontendUrl = d.Str("frontendUrl", ""),
                AiEndpoint = d.Str("aiEndpoint", ""),
                CacheMinutes = cache,
                LoadedFrom = path
            };
        }

        /// <summary>Expands %ENV% variables and resolves paths relative to the core folder.</summary>
        public static string Expand(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;
            value = Environment.ExpandEnvironmentVariables(value.Trim());
            if (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return value;
            return Path.IsPathRooted(value) ? value : Path.GetFullPath(Path.Combine(CoreDir, value));
        }
    }

    public static class Log
    {
        public static void Write(string message)
        {
            try
            {
                string dir = Path.Combine(AppConfig.LocalDataDir, "logs");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "core.log"), DateTime.Now.ToString("s") + "  " + message + Environment.NewLine);
            }
            catch { }
        }
    }
}
