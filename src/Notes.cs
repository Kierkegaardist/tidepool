using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace ConsoleDesktop;

/// <summary>Windows 10 notifications, read from the user's own notification store (wpndatabase.db, what Action Center shows).
/// UserNotificationListener needs package identity + a consent prompt; this file is plain user data and needs neither.</summary>
static class Notes
{
    public record Note(long id, string app, string title, string body, long at);
    static readonly string Db = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db");
    static DateTime stamp;

    /// <summary>The newest 20 toasts, newest first; null when the store has not changed since the last call.</summary>
    public static List<Note>? ReadIfChanged()
    {
        var t = new[] { Db, Db + "-wal" }.Where(File.Exists).Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
        if (t == stamp) return null;
        stamp = t;
        var list = new List<Note>();
        try
        {
            using var c = new SqliteConnection($"Data Source={Db};Mode=ReadOnly;Pooling=False");
            c.Open();
            var cmd = c.CreateCommand();
            cmd.CommandText = @"select n.Id, h.PrimaryId, (select AssetValue from HandlerAssets a where a.HandlerId = h.RecordId and a.AssetKey = 'DisplayName'), n.Payload, n.ArrivalTime
                from Notification n join NotificationHandler h on h.RecordId = n.HandlerId where n.Type = 'toast' order by n.ArrivalTime desc limit 20";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var xml = r.GetValue(3) is byte[] b ? System.Text.Encoding.UTF8.GetString(b) : r.GetValue(3)?.ToString() ?? "";
                var texts = Regex.Matches(xml, @"<text[^>]*>(?:<!\[CDATA\[)?(.*?)(?:\]\]>)?</text>", RegexOptions.Singleline)
                    .Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value).Trim()).Where(s => s != "").ToList();
                if (texts.Count == 0) continue;
                var app = r.IsDBNull(2) ? AppName(r.GetString(1)) : r.GetString(2);
                var at = DateTimeOffset.FromFileTime(r.GetInt64(4)).ToUnixTimeSeconds();
                list.Add(new(r.GetInt64(0), app, texts[0], string.Join(" ", texts.Skip(1)), at));
            }
        }
        catch (Exception e) { Log.Write("notifications unreadable: " + e.Message); }
        return list;
    }

    /// <summary>"Microsoft.PowerToysWin32" -> "PowerToys", "com.nvidia.nvapp" -> "NVIDIA", "...!App" -> its package name.</summary>
    static string AppName(string id)
    {
        var known = new Dictionary<string, string> { ["nvidia"] = "NVIDIA", ["powertoys"] = "PowerToys", ["skydrive"] = "OneDrive", ["steam"] = "Steam", ["discord"] = "Discord", ["firefox"] = "Firefox", ["chrome"] = "Chrome", ["claude"] = "Claude", ["powershell"] = "PowerShell", ["windowsterminal"] = "Terminal" };
        var k = known.Keys.FirstOrDefault(k => id.Contains(k, StringComparison.OrdinalIgnoreCase));
        if (k != null) return known[k];
        var s = id.Split('!')[0].Split('_')[0].Split('.').Last(x => x != "Desktop" && x != "exe");
        s = Path.GetFileNameWithoutExtension(s);
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : id;   // "powershell"-style ids read as names
    }
}
