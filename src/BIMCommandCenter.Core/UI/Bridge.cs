using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Autodesk.Revit.UI;
using BIMCommandCenter.Core.Catalog;
using BIMCommandCenter.Core.Ideas;
using BIMCommandCenter.Core.Infrastructure;
using BIMCommandCenter.Core.Revit;
using Microsoft.Web.WebView2.Core;

namespace BIMCommandCenter.Core.UI
{
    /// <summary>
    /// Request/response protocol between the web UI and Revit.
    ///   JS  -> C#  : { id, action, payload }
    ///   C#  -> JS  : { replyTo: id, ok: true, data } | { replyTo: id, ok: false, error }
    /// The UI never sends file paths to execute: it sends a toolId, and C# looks the tool up
    /// in the catalog it loaded itself. That keeps the web page from running arbitrary files.
    /// </summary>
    public class Bridge
    {
        private readonly CoreWebView2 _web;
        private readonly Services _s;

        public Bridge(CoreWebView2 web, Services services)
        {
            _web = web;
            _s = services;
        }

        public async void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string id = null;
            try
            {
                string raw;
                try { raw = e.TryGetWebMessageAsString(); }
                catch { raw = e.WebMessageAsJson; }

                Dictionary<string, object> msg = Json.AsObject(Json.Parse(raw)) ?? throw new ArgumentException("Bad message");
                id = msg.Str("id");
                string action = msg.Str("action");
                Dictionary<string, object> payload =
                    (msg.TryGetValue("payload", out object p) ? Json.AsObject(p) : null)
                    ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

                object data = await HandleAsync(action, payload);
                Reply(id, true, data, null);
            }
            catch (Exception ex)
            {
                Log.Write("Bridge error: " + ex);
                Reply(id, false, null, ex.InnerException?.Message ?? ex.Message);
            }
        }

        private async Task<object> HandleAsync(string action, Dictionary<string, object> payload)
        {
            switch (action)
            {
                case "getContext":
                    Dictionary<string, object> ctx = await _s.Dispatcher.InvokeAsync(RevitContext.Describe);
                    ctx["configSource"] = _s.Config.LoadedFrom;
                    ctx["catalogSource"] = _s.Config.CatalogSource;
                    ctx["aiEndpointConfigured"] = !string.IsNullOrWhiteSpace(_s.Config.AiEndpoint);
                    return ctx;

                case "getCatalog":
                    CatalogResult catalog = await _s.Catalog.GetAsync(payload.Bool("refresh"));
                    return new Dictionary<string, object>
                    {
                        ["tools"] = catalog.Tools,
                        ["source"] = catalog.Source,
                        ["fromCache"] = catalog.FromCache,
                        ["warning"] = catalog.Warning ?? "",
                        ["loadedAt"] = catalog.LoadedAt.ToString("s")
                    };

                case "executeTool":
                {
                    string toolId = payload.Str("toolId");
                    if (_s.Catalog.Current == null) await _s.Catalog.GetAsync(false);
                    Dictionary<string, object> tool = _s.Catalog.FindTool(toolId)
                        ?? throw new InvalidOperationException("Tool \"" + toolId + "\" is not in the catalog.");
                    Log.Write("Execute " + toolId + " by " + Environment.UserName);
                    RunResult result = await _s.Dispatcher.InvokeAsync(app => _s.Runner.Run(app, tool));
                    return result.ToDictionary();
                }

                case "captureViewport":
                    return await _s.Dispatcher.InvokeAsync(RevitContext.CaptureActiveView);

                case "submitIdea":
                {
                    Dictionary<string, object> context = await _s.Dispatcher.InvokeAsync(RevitContext.Describe);
                    return await _s.Ideas.SubmitAsync(payload, context);
                }

                case "openUrl":
                {
                    string url = payload.Str("url");
                    if (url == null || !(url.StartsWith("https://") || url.StartsWith("http://")))
                        throw new ArgumentException("Only http(s) links can be opened.");
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                    return true;
                }

                case "openFolder":
                {
                    string path = payload.Str("path");
                    if (string.IsNullOrWhiteSpace(path))
                        path = _s.Config.ScriptsRoot;
                    if (string.IsNullOrWhiteSpace(path))
                        path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "BIM_Scripts");

                    path = Environment.ExpandEnvironmentVariables(path);
                    System.IO.Directory.CreateDirectory(path);
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
                    return new Dictionary<string, object> { ["path"] = path };
                }

                case "getConfig":
                {
                    return new Dictionary<string, object>
                    {
                        ["catalogSource"] = _s.Config.CatalogSource,
                        ["catalogSheet"] = _s.Config.CatalogSheet,
                        ["scriptsRoot"] = _s.Config.ScriptsRoot,
                        ["ideasEndpoint"] = _s.Config.IdeasEndpoint,
                        ["ideasFolder"] = _s.Config.IdeasFolder,
                        ["configPath"] = _s.Config.LoadedFrom
                    };
                }

                case "saveConfig":
                {
                    string userConfigPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BIMCommandCenter", "config.json");
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(userConfigPath));

                    var current = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    if (System.IO.File.Exists(userConfigPath))
                    {
                        try { current = Json.AsObject(Json.Parse(System.IO.File.ReadAllText(userConfigPath))) ?? current; } catch {}
                    }

                    foreach (var kv in payload)
                    {
                        current[kv.Key] = kv.Value;
                    }

                    System.IO.File.WriteAllText(userConfigPath, Json.Stringify(current));
                    _s.Config = AppConfig.Load();
                    _s.Catalog = new CatalogService(_s.Config, _s.RevitYear ?? "2026");
                    _s.Runner = new ToolRunner(_s.Config);

                    return new Dictionary<string, object> { ["saved"] = true, ["configPath"] = userConfigPath };
                }

                default:
                    throw new NotSupportedException("Unknown action: " + action);
            }
        }

        private void Reply(string id, bool ok, object data, string error)
        {
            if (id == null) return;
            var message = new Dictionary<string, object> { ["replyTo"] = id, ["ok"] = ok };
            if (ok) message["data"] = data; else message["error"] = error;
            try { _web.PostWebMessageAsJson(Json.Stringify(message)); }
            catch (Exception ex) { Log.Write("Reply failed: " + ex.Message); }
        }
    }

    /// <summary>Shared services created once at Revit startup.</summary>
    public class Services
    {
        public AppConfig Config { get; set; }
        public RevitDispatcher Dispatcher { get; set; }
        public CatalogService Catalog { get; set; }
        public ToolRunner Runner { get; set; }
        public IdeaService Ideas { get; set; }
        public string RevitYear { get; set; }
    }
}
