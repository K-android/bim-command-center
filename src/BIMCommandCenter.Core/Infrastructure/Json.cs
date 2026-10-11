using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
#if NETFRAMEWORK
using System.Web.Script.Serialization;
#else
using System.Text.Json;
#endif

namespace BIMCommandCenter.Core.Infrastructure
{
    /// <summary>
    /// Minimal JSON helper that works on .NET Framework 4.8 (Revit 2023/24) and .NET 8/10 (Revit 2025+)
    /// without shipping extra NuGet dependencies that commonly clash with other Revit add-ins.
    /// Objects become Dictionary&lt;string, object&gt; (case-insensitive), arrays become List&lt;object&gt;.
    /// </summary>
    public static class Json
    {
#if NETFRAMEWORK
        private static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };

        public static object Parse(string json) => Normalize(Serializer.DeserializeObject(json));

        public static string Stringify(object value) => Serializer.Serialize(value);

        private static object Normalize(object value)
        {
            switch (value)
            {
                case IDictionary<string, object> dict:
                    var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in dict) result[kv.Key] = Normalize(kv.Value);
                    return result;
                case object[] arr:
                    var list = new List<object>();
                    foreach (var item in arr) list.Add(Normalize(item));
                    return list;
                default:
                    return value;
            }
        }
#else
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions { WriteIndented = false };

        public static object Parse(string json)
        {
            using (JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }))
            {
                return Convert(doc.RootElement);
            }
        }

        public static string Stringify(object value) => JsonSerializer.Serialize(value, Options);

        private static object Convert(JsonElement e)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    foreach (JsonProperty p in e.EnumerateObject()) dict[p.Name] = Convert(p.Value);
                    return dict;
                case JsonValueKind.Array:
                    var list = new List<object>();
                    foreach (JsonElement item in e.EnumerateArray()) list.Add(Convert(item));
                    return list;
                case JsonValueKind.String: return e.GetString();
                case JsonValueKind.Number: return e.TryGetInt64(out long l) ? (object)l : e.GetDouble();
                case JsonValueKind.True: return true;
                case JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif

        // ---- Convenience accessors -------------------------------------------------

        public static Dictionary<string, object> AsObject(object value) => value as Dictionary<string, object>;

        public static string Str(this IDictionary<string, object> d, string key, string fallback = null)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return fallback;
            if (v is string s) return s;
            if (v is IEnumerable e && !(v is string))
            {
                var parts = new List<string>();
                foreach (object o in e) parts.Add(System.Convert.ToString(o, CultureInfo.InvariantCulture));
                return string.Join(", ", parts);
            }
            return System.Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static bool Bool(this IDictionary<string, object> d, string key, bool fallback = false)
        {
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return fallback;
            if (v is bool b) return b;
            string s = System.Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            return s == "true" || s == "yes" || s == "1" || s == "y";
        }

        public static List<string> StrList(this IDictionary<string, object> d, string key)
        {
            var result = new List<string>();
            if (d == null || !d.TryGetValue(key, out object v) || v == null) return result;
            if (v is string s)
            {
                foreach (string part in s.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                    if (part.Trim().Length > 0) result.Add(part.Trim());
                return result;
            }
            if (v is IEnumerable e)
                foreach (object o in e)
                    if (o != null) result.Add(System.Convert.ToString(o, CultureInfo.InvariantCulture).Trim());
            return result;
        }
    }
}
