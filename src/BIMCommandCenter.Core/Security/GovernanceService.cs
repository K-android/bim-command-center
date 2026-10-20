using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BIMCommandCenter.Core.Infrastructure;

namespace BIMCommandCenter.Core.Security
{
    public class GovernanceUser
    {
        public string Id { get; set; } = string.Empty;
        public string Username { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string Pin { get; set; } = string.Empty;
        public string Role { get; set; } = "JuniorModeler";
        public string Discipline { get; set; } = "All";
        public bool Active { get; set; } = true;
    }

    public class ScriptRule
    {
        public string ToolId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string SafetyLevel { get; set; } = string.Empty;
        public bool Published { get; set; } = true;
        public List<string> AllowedRoles { get; set; } = new List<string>();
        public List<string> AllowedDisciplines { get; set; } = new List<string>();
    }

    public class UserSession
    {
        public string Username { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Role { get; set; } = "JuniorModeler";
        public string Discipline { get; set; } = "All";
        public string Email { get; set; } = string.Empty;

        public Dictionary<string, object> ToDictionary() => new Dictionary<string, object>
        {
            ["username"] = Username,
            ["displayName"] = DisplayName,
            ["role"] = Role,
            ["discipline"] = Discipline,
            ["email"] = Email
        };
    }

    public class GovernanceData
    {
        public string SchemaVersion { get; set; } = "2.0";
        public Dictionary<string, object> Settings { get; set; } = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        public List<GovernanceUser> Users { get; set; } = new List<GovernanceUser>();
        public Dictionary<string, ScriptRule> ScriptRules { get; set; } = new Dictionary<string, ScriptRule>(StringComparer.OrdinalIgnoreCase);
    }

    public class GovernanceService
    {
        private static string FallbackCachePath => Path.Combine(AppConfig.LocalDataDir, "governance_cache.json");

        private readonly AppConfig _config;
        private GovernanceData _data;
        private DateTime _lastLoaded = DateTime.MinValue;

        public GovernanceService(AppConfig config)
        {
            _config = config;
        }

        public GovernanceData Data => _data;
        public UserSession CurrentUser { get; private set; }
        public string LoadedPath { get; private set; }

        public string SessionFilePath => Path.Combine(AppConfig.LocalDataDir, "session.json");

        public string ResolveGovernancePath()
        {
            if (!string.IsNullOrWhiteSpace(_config.ScriptsRoot))
            {
                string expanded = Environment.ExpandEnvironmentVariables(_config.ScriptsRoot);
                string primary = Path.Combine(expanded, "governance.json");
                if (File.Exists(primary)) return primary;
            }

            if (File.Exists(FallbackCachePath)) return FallbackCachePath;

            return null;
        }

        public GovernanceData LoadGovernance(bool force = false)
        {
            string path = ResolveGovernancePath();
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                if (_data == null) _data = new GovernanceData();
                return _data;
            }

            try
            {
                DateTime lastWrite = File.GetLastWriteTimeUtc(path);
                if (!force && _data != null && lastWrite <= _lastLoaded && Path.Equals(LoadedPath, path))
                {
                    return _data;
                }

                string rawJson = File.ReadAllText(path);

                // Whenever governance.json is loaded from ScriptsRoot, cache a copy locally for offline fallback
                if (!string.Equals(path, FallbackCachePath, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(FallbackCachePath));
                        File.WriteAllText(FallbackCachePath, rawJson);
                    }
                    catch { }
                }

                var root = Json.Parse(rawJson) as IDictionary<string, object>;
                if (root == null) return _data ?? new GovernanceData();

                var gov = new GovernanceData
                {
                    SchemaVersion = root.Str("schemaVersion", "2.0")
                };

                // Settings
                if (root.TryGetValue("settings", out object sObj) && sObj is IDictionary<string, object> settingsDict)
                {
                    foreach (var kvp in settingsDict) gov.Settings[kvp.Key] = kvp.Value;
                }

                // Users
                if (root.TryGetValue("users", out object uObj) && uObj is IEnumerable<object> uList)
                {
                    foreach (var item in uList)
                    {
                        if (item is IDictionary<string, object> ud)
                        {
                            gov.Users.Add(new GovernanceUser
                            {
                                Id = ud.Str("id", ""),
                                Username = ud.Str("username", ""),
                                DisplayName = ud.Str("displayName", ud.Str("username", "")),
                                Email = ud.Str("email", ""),
                                Pin = ud.Str("pin", "").Trim(),
                                Role = ud.Str("role", "JuniorModeler"),
                                Discipline = ud.Str("discipline", "All"),
                                Active = ud.Bool("active", true)
                            });
                        }
                    }
                }

                // ScriptRules
                if (root.TryGetValue("scriptRules", out object rObj) && rObj is IDictionary<string, object> rulesDict)
                {
                    foreach (var kvp in rulesDict)
                    {
                        if (kvp.Value is IDictionary<string, object> rd)
                        {
                            string toolId = kvp.Key;
                            var rule = new ScriptRule
                            {
                                ToolId = toolId,
                                Title = rd.Str("title", ""),
                                Category = rd.Str("category", ""),
                                Description = rd.Str("description", ""),
                                SafetyLevel = rd.Str("safetyLevel", ""),
                                Published = rd.Bool("published", true),
                                AllowedRoles = rd.StrList("allowedRoles"),
                                AllowedDisciplines = rd.StrList("allowedDisciplines")
                            };
                            gov.ScriptRules[toolId] = rule;
                        }
                    }
                }

                _data = gov;
                LoadedPath = path;
                _lastLoaded = lastWrite;

                // Sync current user's profile if already logged in
                if (CurrentUser != null)
                {
                    var liveUser = _data.Users.FirstOrDefault(u => string.Equals(u.Username, CurrentUser.Username, StringComparison.OrdinalIgnoreCase));
                    if (liveUser != null && liveUser.Active)
                    {
                        CurrentUser.DisplayName = liveUser.DisplayName;
                        CurrentUser.Role = liveUser.Role;
                        CurrentUser.Discipline = liveUser.Discipline;
                        CurrentUser.Email = liveUser.Email;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("LoadGovernance error: " + ex.Message);
            }

            return _data ?? new GovernanceData();
        }

        public void InitializeSession()
        {
            if (CurrentUser != null) return;

            LoadGovernance(false);

            try
            {
                string sessionFile = SessionFilePath;
                if (File.Exists(sessionFile))
                {
                    string json = File.ReadAllText(sessionFile);
                    var dict = Json.Parse(json) as IDictionary<string, object>;
                    string username = dict?.Str("username");
                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        var user = _data?.Users?.FirstOrDefault(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));
                        if (user != null && user.Active)
                        {
                            CurrentUser = new UserSession
                            {
                                Username = user.Username,
                                DisplayName = user.DisplayName,
                                Role = user.Role,
                                Discipline = user.Discipline,
                                Email = user.Email
                            };
                            Log.Write($"Restored remembered session for '{user.Username}' ({user.Role} • {user.Discipline}).");
                            return;
                        }
                    }

                    // Stale or invalid session
                    try { File.Delete(sessionFile); } catch { }
                }
            }
            catch (Exception ex)
            {
                Log.Write("InitializeSession error: " + ex.Message);
            }
        }

        public bool Login(string usernameOrEmail, string pin, bool rememberMe, out string error)
        {
            LoadGovernance(true);

            if (_data == null || _data.Users.Count == 0)
            {
                error = "Governance user database is unconfigured or empty.";
                return false;
            }

            string input = (usernameOrEmail ?? "").Trim();
            string pinInput = (pin ?? "").Trim();

            if (string.IsNullOrEmpty(input))
            {
                error = "Please enter your username or email.";
                return false;
            }

            var user = _data.Users.FirstOrDefault(u =>
                string.Equals(u.Username, input, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(u.Email, input, StringComparison.OrdinalIgnoreCase));

            if (user == null)
            {
                error = $"User \"{input}\" is not registered in BIM Control Room.";
                return false;
            }

            if (!user.Active)
            {
                error = "This user account has been deactivated by BIM Administration.";
                return false;
            }

            if (string.IsNullOrEmpty(user.Pin) || user.Pin != pinInput)
            {
                error = "Invalid PIN. Please check your credentials.";
                return false;
            }

            CurrentUser = new UserSession
            {
                Username = user.Username,
                DisplayName = user.DisplayName,
                Role = user.Role,
                Discipline = user.Discipline,
                Email = user.Email
            };

            try
            {
                if (rememberMe)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(SessionFilePath));
                    File.WriteAllText(SessionFilePath, Json.Stringify(CurrentUser.ToDictionary()));
                }
                else
                {
                    if (File.Exists(SessionFilePath)) File.Delete(SessionFilePath);
                }
            }
            catch (Exception ex)
            {
                Log.Write("Persist session error: " + ex.Message);
            }

            Log.Write($"User '{user.Username}' logged in successfully as {user.Role} ({user.Discipline}).");
            error = null;
            return true;
        }

        public void Logout()
        {
            string prevUser = CurrentUser?.Username;
            CurrentUser = null;
            try
            {
                if (File.Exists(SessionFilePath)) File.Delete(SessionFilePath);
            }
            catch { }

            Log.Write($"User '{prevUser ?? "unknown"}' logged out.");
        }

        public ScriptRule FindRuleForTool(string toolId, string title = null, string targetPath = null)
        {
            if (_data == null || _data.ScriptRules.Count == 0) return null;

            if (!string.IsNullOrEmpty(toolId))
            {
                if (_data.ScriptRules.TryGetValue(toolId, out var r1)) return r1;

                // Try with "local-" prefix
                if (!toolId.StartsWith("local-", StringComparison.OrdinalIgnoreCase))
                {
                    if (_data.ScriptRules.TryGetValue("local-" + toolId, out var r2)) return r2;
                }
                else
                {
                    // Try without "local-" prefix
                    string stripped = toolId.Substring(6);
                    if (_data.ScriptRules.TryGetValue(stripped, out var r3)) return r3;
                }
            }

            // Fallback match by target filename without extension
            if (!string.IsNullOrEmpty(targetPath))
            {
                string bName = Path.GetFileNameWithoutExtension(targetPath).ToLowerInvariant();
                foreach (var kvp in _data.ScriptRules)
                {
                    if (string.Equals(kvp.Key, bName, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(kvp.Key, "local-" + bName, StringComparison.OrdinalIgnoreCase))
                        return kvp.Value;
                }
            }

            return null;
        }

        public void ApplyGovernanceRule(Dictionary<string, object> tool)
        {
            if (tool == null) return;
            string toolId = tool.Str("toolId");
            string title = tool.Str("title");
            string targetPath = tool.Str("targetPath");

            var rule = FindRuleForTool(toolId, title, targetPath);
            if (rule != null)
            {
                if (!string.IsNullOrWhiteSpace(rule.Title))
                {
                    tool["title"] = rule.Title;
                    tool["name"] = rule.Title;
                }
                if (!string.IsNullOrWhiteSpace(rule.Category))
                {
                    tool["category"] = rule.Category;
                }
                if (!string.IsNullOrWhiteSpace(rule.Description))
                {
                    tool["description"] = rule.Description;
                    tool["intendedOutcome"] = rule.Description;
                }
                if (!string.IsNullOrWhiteSpace(rule.SafetyLevel))
                {
                    tool["safetyLevel"] = rule.SafetyLevel;
                }
                tool["published"] = rule.Published;
                tool["allowedRoles"] = rule.AllowedRoles;
                tool["allowedDisciplines"] = rule.AllowedDisciplines;
            }
        }

        public bool IsToolVisible(UserSession user, Dictionary<string, object> tool)
        {
            if (tool == null) return false;
            string toolId = tool.Str("toolId");
            string title = tool.Str("title");
            string targetPath = tool.Str("targetPath");

            var rule = FindRuleForTool(toolId, title, targetPath);
            if (rule != null)
            {
                if (!rule.Published) return false;

                if (user != null)
                {
                    // Admin has universal script visibility
                    if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
                        return true;

                    // Role check
                    if (rule.AllowedRoles != null && rule.AllowedRoles.Count > 0)
                    {
                        if (!rule.AllowedRoles.Any(r => string.Equals(r, user.Role, StringComparison.OrdinalIgnoreCase)))
                            return false;
                    }

                    // Discipline check
                    if (!string.Equals(user.Discipline, "All", StringComparison.OrdinalIgnoreCase))
                    {
                        if (rule.AllowedDisciplines != null && rule.AllowedDisciplines.Count > 0 &&
                            !rule.AllowedDisciplines.Any(d => string.Equals(d, "All", StringComparison.OrdinalIgnoreCase)))
                        {
                            if (!rule.AllowedDisciplines.Any(d => string.Equals(d, user.Discipline, StringComparison.OrdinalIgnoreCase)))
                                return false;
                        }
                    }
                }
            }

            return true;
        }

        public bool IsAuthorized(UserSession user, string toolId, out string reason)
        {
            LoadGovernance(false);

            if (_data == null || _data.Users == null || _data.Users.Count == 0)
            {
                reason = null;
                return true;
            }

            if (user == null)
            {
                reason = "User must be authenticated to execute automations.";
                return false;
            }

            if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            {
                reason = null;
                return true;
            }

            var rule = FindRuleForTool(toolId);
            if (rule != null)
            {
                if (!rule.Published)
                {
                    reason = $"Script \"{rule.Title ?? toolId}\" is unpublished and cannot be executed.";
                    return false;
                }

                if (rule.AllowedRoles != null && rule.AllowedRoles.Count > 0)
                {
                    if (!rule.AllowedRoles.Any(r => string.Equals(r, user.Role, StringComparison.OrdinalIgnoreCase)))
                    {
                        reason = $"Your role ({user.Role}) is not permitted to execute this tool. Allowed roles: {string.Join(", ", rule.AllowedRoles)}.";
                        return false;
                    }
                }

                if (!string.Equals(user.Discipline, "All", StringComparison.OrdinalIgnoreCase))
                {
                    if (rule.AllowedDisciplines != null && rule.AllowedDisciplines.Count > 0 &&
                        !rule.AllowedDisciplines.Any(d => string.Equals(d, "All", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (!rule.AllowedDisciplines.Any(d => string.Equals(d, user.Discipline, StringComparison.OrdinalIgnoreCase)))
                        {
                            reason = $"Your discipline ({user.Discipline}) is not permitted to execute this tool. Allowed disciplines: {string.Join(", ", rule.AllowedDisciplines)}.";
                            return false;
                        }
                    }
                }
            }

            reason = null;
            return true;
        }
    }
}
