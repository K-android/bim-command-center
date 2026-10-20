using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BIMCommandCenter.Core.Infrastructure;

namespace BIMCommandCenter.Core.Ideas
{
    /// <summary>
    /// Sends "Request Automation" submissions to the backlog.
    /// Preferred: a Power Automate "When an HTTP request is received" flow that adds a row to the
    /// SharePoint list / Excel table (no app registration or user login needed inside Revit).
    /// Fallback: writes a JSON file to ideasFolder (e.g. a shared network folder) or local app data.
    /// </summary>
    public class IdeaService
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        private readonly AppConfig _config;

        public IdeaService(AppConfig config) => _config = config;

        public async Task<Dictionary<string, object>> SubmitAsync(Dictionary<string, object> idea, Dictionary<string, object> context)
        {
            var record = new Dictionary<string, object>
            {
                ["ideaTitle"] = idea.Str("ideaTitle", ""),
                ["problemStatement"] = idea.Str("problemStatement", ""),
                ["categorySuggested"] = idea.Str("categorySuggested", ""),
                ["submitterEmail"] = idea.Str("submitterEmail", ""),
                ["submitterName"] = Environment.UserName,
                ["machine"] = Environment.MachineName,
                ["revitVersionUsed"] = context.Str("revitVersion", ""),
                ["documentTitle"] = context.Str("documentTitle", ""),
                ["status"] = "New",
                ["createdAt"] = DateTime.UtcNow.ToString("o")
            };

            if (string.IsNullOrWhiteSpace(record.Str("ideaTitle")))
                throw new ArgumentException("Idea title is required.");

            if (!string.IsNullOrWhiteSpace(_config.IdeasEndpoint))
            {
                var content = new StringContent(Json.Stringify(record), Encoding.UTF8, "application/json");
                HttpResponseMessage response = await Http.PostAsync(_config.IdeasEndpoint, content).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException("Backlog endpoint returned " + (int)response.StatusCode + " " + response.ReasonPhrase);
                return new Dictionary<string, object> { ["destination"] = "SharePoint backlog" };
            }

            string folder = string.IsNullOrWhiteSpace(_config.IdeasFolder)
                ? Path.Combine(AppConfig.LocalDataDir, "ideas")
                : _config.IdeasFolder;
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "_" + Environment.UserName + ".json");
            File.WriteAllText(file, Json.Stringify(record));
            return new Dictionary<string, object> { ["destination"] = file };
        }
    }
}
