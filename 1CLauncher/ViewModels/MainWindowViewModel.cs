using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.Input;
using _1CLauncher.Services;

namespace _1CLauncher.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        // Available 1C platform executables
        public ObservableCollection<string> Platforms { get; } = new();

        // map display -> exe path
        private readonly Dictionary<string, string> _platformPaths = new(StringComparer.OrdinalIgnoreCase);

        private string? _selectedPlatform;
        public string? SelectedPlatform
        {
            get => _selectedPlatform;
            set
            {
                if (SetProperty(ref _selectedPlatform, value))
                {
                    if (!string.IsNullOrEmpty(value) && _platformPaths.TryGetValue(value, out var p))
                        PlatformPath = p;
                    else
                        PlatformPath = string.Empty;
                }
            }
        }

        private string _platformPath = string.Empty;
        public string PlatformPath
        {
            get => _platformPath;
            set => SetProperty(ref _platformPath, value);
        }

        // Add a platform executable path (called from view)
        public void AddPlatformFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (File.Exists(path))
                {
                    var folder = Path.GetFileName(Path.GetDirectoryName(path) ?? string.Empty);
                    var display = (string.IsNullOrWhiteSpace(folder) ? "Manual" : folder) + " (manual)";
                    if (!Platforms.Contains(display))
                    {
                        Platforms.Add(display);
                        _platformPaths[display] = path;
                    }
                    SelectedPlatform = display;
                }
            }
            catch
            {
                // ignore
            }
        }

        // Try to discover common 1C platform executables on the machine
        public void RefreshPlatforms()
        {
            try
            {
                Platforms.Clear();

                var candidates = new List<string>();
                var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                if (!string.IsNullOrWhiteSpace(pf)) candidates.Add(pf);
                if (!string.IsNullOrWhiteSpace(pf86) && !string.Equals(pf86, pf, StringComparison.OrdinalIgnoreCase)) candidates.Add(pf86);

                foreach (var root in candidates)
                {
                    try
                    {
                        // Prefer searching under root\1cv8 and its subfolders (common install layout: 1cv8\<version>\bin\1cv8.exe)
                        var dir1 = Path.Combine(root, "1cv8");
                        if (Directory.Exists(dir1))
                        {
                            try
                            {
                                foreach (var exePath in Directory.EnumerateFiles(dir1, "1cv8c.exe", SearchOption.AllDirectories))
                                {
                                    if (File.Exists(exePath) && !Platforms.Contains(exePath, StringComparer.OrdinalIgnoreCase))
                                        Platforms.Add(exePath);
                                }
                            }
                            catch
                            {
                                // ignore access errors
                            }
                        }

                        // If none found yet, do a shallow two-level search: root\*\bin\1cv8.exe and root\*\*\bin\1cv8.exe
                        if (Platforms.Count == 0)
                        {
                            foreach (var sub in Directory.EnumerateDirectories(root))
                            {
                                try
                                {
                                    var exePath = Path.Combine(sub, "bin", "1cv8c.exe");
                                    if (File.Exists(exePath) && !Platforms.Contains(exePath, StringComparer.OrdinalIgnoreCase))
                                        Platforms.Add(exePath);

                                    // check one level deeper
                                    foreach (var sub2 in Directory.EnumerateDirectories(sub))
                                    {
                                        try
                                        {
                                            var exePath2 = Path.Combine(sub2, "bin", "1cv8c.exe");
                                            if (File.Exists(exePath2) && !Platforms.Contains(exePath2, StringComparer.OrdinalIgnoreCase))
                                                Platforms.Add(exePath2);
                                        }
                                        catch { }
                                    }
                                }
                                catch { }
                            }
                        }
                    }
                    catch { }
                }

                // keep previously selected platform if still present
                if (Platforms.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(SelectedPlatform) || !Platforms.Contains(SelectedPlatform))
                        SelectedPlatform = Platforms[0];
                }
            }
            catch
            {
                // ignore discovery errors
            }
        }

        // Input fields bound from the view
        private string _username = string.Empty;
        public string Username
        {
            get => _username;
            set
            {
                if (SetProperty(ref _username, value))
                    SaveSettings();
            }
        }

        private string _password = string.Empty;
        public string Password
        {
            get => _password;
            set => SetProperty(ref _password, value);
        }

        private string _domain = string.Empty;
        public string Domain
        {
            get => _domain;
            set
            {
                if (SetProperty(ref _domain, value))
                    SaveSettings();
            }
        }

        private string _externalUrl = string.Empty;
        public string ExternalUrl
        {
            get => _externalUrl;
            set
            {
                if (SetProperty(ref _externalUrl, value))
                    SaveSettings();
            }
        }

        // Info-bases loaded from ibases.v8i
        public class BaseItem
        {
            public string Name { get; set; } = string.Empty;
            public string Connect { get; set; } = string.Empty;
            public string Display { get; set; } = string.Empty; // shown in ComboBox
        }

        public ObservableCollection<BaseItem> Bases { get; } = new();

        private readonly Dictionary<string, string> _connects = new(StringComparer.OrdinalIgnoreCase);

        private BaseItem? _selectedBase;
        public BaseItem? SelectedBase
        {
            get => _selectedBase;
            set
            {
                if (SetProperty(ref _selectedBase, value))
                {
                    ConnectionString = value?.Connect ?? FindConnectForBase(value?.Name);
                    SaveSettings();
                }
            }
        }

        private string _connectionString = string.Empty;
        public string ConnectionString
        {
            get => _connectionString;
            set
            {
                if (SetProperty(ref _connectionString, value))
                {
                    // When connection string changes, update whether launch is allowed
                    LaunchCommand?.NotifyCanExecuteChanged();
                }
            }
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        public IAsyncRelayCommand LaunchCommand { get; }

        public MainWindowViewModel()
        {
            // Initialize command with CanExecute that requires `ws` parameter in ConnectionString
            LaunchCommand = new AsyncRelayCommand(CheckAuthAsync, CanLaunch);

            var settings = SettingsService.Load();
            Username = settings.Username;
            Domain = settings.Domain;
            ExternalUrl = settings.ExternalUrl;

            // load platforms first (so we can show platform info alongside bases)
            LoadInstalledPlatforms();

            // then load bases
            LoadBasesFromIbasesFile();
        }

        private bool CanLaunch()
        {
            if (string.IsNullOrWhiteSpace(ConnectionString))
                return false;

            // check for parameter ws anywhere in the connection string (case-insensitive)
            return ConnectionString.IndexOf("ws", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private async Task CheckAuthAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(ExternalUrl))
                {
                    StatusMessage = "ExternalUrl not configured.";
                    return;
                }

                var url = ExternalUrl.TrimEnd('/') + "/checkAuth";
                StatusMessage = "Checking authentication...";

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var payload = JsonSerializer.Serialize(new { username = Username, password = Password, domain = Domain });
                using var content = new StringContent(payload, Encoding.UTF8, "application/json");

                var resp = await client.PostAsync(url, content);
                if (!resp.IsSuccessStatusCode)
                {
                    StatusMessage = $"Request failed: {(int)resp.StatusCode} {resp.ReasonPhrase}";
                    return;
                }

                var body = await resp.Content.ReadAsStringAsync();
                try
                {
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("authenticated", out var authProp) &&
                        (authProp.ValueKind == JsonValueKind.True || authProp.ValueKind == JsonValueKind.False))
                    {
                        var authenticated = authProp.GetBoolean();
                        if (!authenticated)
                        {
                            StatusMessage = "Authentication failed";
                            return;
                        }

                        // authenticated == true
                        // try to get token
                        string? token = null;
                        if (doc.RootElement.TryGetProperty("token", out var tokenProp) && tokenProp.ValueKind == JsonValueKind.String)
                            token = tokenProp.GetString();

                        StatusMessage = "Authenticated";

                        if (string.IsNullOrWhiteSpace(token))
                        {
                            // No token - nothing to launch
                            return;
                        }

                        // extract ws parameter from ConnectionString (supports ws="..." or ws=...)
                        string ws = string.Empty;
                        try
                        {
                            var conn = ConnectionString ?? string.Empty;
                            var m = Regex.Match(conn, "\\bws\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
                            if (m.Success)
                                ws = m.Groups[1].Value.Trim();
                            else
                            {
                                m = Regex.Match(conn, "\\bws\\s*=\\s*([^;\\s]+)", RegexOptions.IgnoreCase);
                                if (m.Success)
                                    ws = m.Groups[1].Value.Trim().Trim('"');
                            }
                        }
                        catch { }

                        if (string.IsNullOrWhiteSpace(ws))
                        {
                            StatusMessage = "Authenticated, but 'ws' parameter not found in Connect; cannot launch.";
                            return;
                        }

                        // determine executable path: use only PlatformPath (mapped from selected display)
                        var exePath = PlatformPath;
                        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                        {
                            StatusMessage = "Authenticated, token received, but platform executable not selected or not found.";
                            return;
                        }

                        try
                        {
                            // prepare arguments
                            // wrap ws and token in quotes to be safe
                            var args = $"ENTERPRISE /WS \"{ws}\" /AccessToken:\"{token}\"";

                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = exePath,
                                Arguments = args,
                                UseShellExecute = false,
                                CreateNoWindow = true,
                            };

                            System.Diagnostics.Process.Start(psi);
                            StatusMessage = "Launched 1C with token";
                        }
                        catch (Exception ex)
                        {
                            StatusMessage = "Authenticated but failed to launch: " + ex.Message;
                        }

                        return;
                    }
                }
                catch
                {
                    // ignore parse errors
                }

                // If response didn't contain authenticated flag, display raw response
                StatusMessage = "Response: " + body;
            }
            catch (Exception ex)
            {
                StatusMessage = "Error: " + ex.Message;
            }
        }

        private void SaveSettings()
        {
            var settings = new AppSettings
            {
                Username = Username,
                Domain = Domain,
                ExternalUrl = ExternalUrl
            };

            SettingsService.Save(settings);
        }

        private string FindConnectForBase(string? baseName)
        {
            if (string.IsNullOrWhiteSpace(baseName))
                return string.Empty;

            // exact lookup
            if (_connects.TryGetValue(baseName.Trim(), out var conn) && !string.IsNullOrEmpty(conn))
                return conn;

            // case-insensitive lookup (dictionary is already case-insensitive, but try normalized key)
            var key = _connects.Keys.FirstOrDefault(k => string.Equals(k?.Trim(), baseName.Trim(), StringComparison.OrdinalIgnoreCase));
            if (key != null && _connects.TryGetValue(key, out conn) && !string.IsNullOrEmpty(conn))
                return conn;

            // contains lookup
            key = _connects.Keys.FirstOrDefault(k => k != null && k.IndexOf(baseName.Trim(), StringComparison.OrdinalIgnoreCase) >= 0);
            if (key != null && _connects.TryGetValue(key, out conn) && !string.IsNullOrEmpty(conn))
                return conn;

            return string.Empty;
        }

        private void LoadBasesFromIbasesFile()
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var path = Path.Combine(appData, "1C", "1CEStart", "ibases.v8i");

                if (!File.Exists(path))
                    return;

                var content = File.ReadAllText(path);
                var names = new List<(string name, string connect)>();

                // Line-based parsing: when a line [Name] is found, take the next non-empty line and extract Connect=...
                try
                {
                    var lines = File.ReadAllLines(path);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i].Trim();
                        if (line.StartsWith("[") && line.EndsWith("]"))
                        {
                            var name = line.Substring(1, line.Length - 2).Trim();
                            if (string.IsNullOrWhiteSpace(name))
                                continue;

                            string conn = string.Empty;
                            // look for next non-empty line
                            int j = i + 1;
                            while (j < lines.Length && string.IsNullOrWhiteSpace(lines[j])) j++;
                            if (j < lines.Length)
                            {
                                var next = lines[j].Trim();
                                // expect Connect=... on this line
                                var m = Regex.Match(next, @"^Connect\s*=\s*""([^""]+)""", RegexOptions.IgnoreCase);
                                if (m.Success)
                                    conn = m.Groups[1].Value.Trim();
                                else
                                {
                                    m = Regex.Match(next, @"^Connect\s*=\s*([^\r\n]+)", RegexOptions.IgnoreCase);
                                    if (m.Success)
                                        conn = m.Groups[1].Value.Trim().Trim('"');
                                }
                            }

                            names.Add((name, conn));
                            _connects[name] = conn;
                        }
                    }
                }
                catch
                {
                    // ignore parsing errors
                }

                // If none found, try section/body parsing as before
                if (!names.Any())
                {
                    try
                    {
                        var sectionPattern = new Regex(@"\[([^\]\r\n]+)\]([\s\S]*?)(?=\r?\n\[|$)", RegexOptions.Multiline);
                        foreach (Match sec in sectionPattern.Matches(content))
                        {
                            var name = sec.Groups[1].Value.Trim();
                            var body = sec.Groups[2].Value;
                            if (string.IsNullOrWhiteSpace(name))
                                continue;

                            string conn = string.Empty;
                            var m = Regex.Match(body, @"Connect\s*=\s*""([^""\]]+)""", RegexOptions.IgnoreCase);
                            if (m.Success)
                                conn = m.Groups[1].Value.Trim();
                            else
                            {
                                m = Regex.Match(body, @"Connect\s*=\s*([^\r\n]+)", RegexOptions.IgnoreCase);
                                if (m.Success)
                                    conn = m.Groups[1].Value.Trim().Trim('"');
                            }

                            names.Add((name, conn));
                            _connects[name] = conn;
                        }
                    }
                    catch
                    {
                        // ignore regex errors
                    }
                }

                // If still none found, try XML parsing
                if (!names.Any())
                {
                    try
                    {
                        var doc = XDocument.Parse(content);
                        var xmlNames = doc.Descendants().Attributes("Name").Select(a => a.Value).Where(s => !string.IsNullOrWhiteSpace(s));
                        names.AddRange(xmlNames.Select(x => (x, string.Empty)));

                        // try to get Connect attributes if present
                        var elements = doc.Descendants().Where(e => e.Attribute("Name") != null);
                        foreach (var el in elements)
                        {
                            var name = el.Attribute("Name")?.Value;
                            var conn = el.Attribute("Connect")?.Value ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                _connects[name] = conn;
                            }
                        }
                    }
                    catch
                    {
                        // ignore xml parse errors
                    }
                }

                // Fallback: regex search for Name="..." or Наименование
                if (!names.Any())
                {
                    var matches = Regex.Matches(content, @"Name\s*=\s*""([^""\]]+)""", RegexOptions.IgnoreCase);
                    foreach (Match m in matches)
                        names.Add((m.Groups[1].Value, string.Empty));

                    if (!names.Any())
                    {
                        var matchesRu = Regex.Matches(content, @"Наименование\s*=\s*""([^""\]]+)""", RegexOptions.IgnoreCase);
                        foreach (Match m in matchesRu)
                            names.Add((m.Groups[1].Value, string.Empty));
                    }
                }

                // Build display string using platform summary from detected platforms
                var platformSummary = Platforms.FirstOrDefault() ?? "(platform unknown)";

                foreach (var n in names.Select(x => x).DistinctBy(t => t.name))
                {
                    var item = new BaseItem { Name = n.name, Connect = n.connect };
                    item.Display = $"{n.name} — {platformSummary}";
                    Bases.Add(item);
                }

                if (Bases.Count > 0)
                    SelectedBase = Bases[0];
            }
            catch
            {
                // Fail silently - file might be absent or unreadable
            }
        }

        private void LoadInstalledPlatforms()
        {
            try
            {
                Platforms.Clear();
                _platformPaths.Clear();

                var pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
                var roots = new[] { pf64, pf86 }.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct();

                var versionDirPattern = new Regex("^\\d+(?:\\.\\d+)*$");

                foreach (var root in roots)
                {
                    try
                    {
                        var baseDir = Path.Combine(root, "1cv8");
                        if (!Directory.Exists(baseDir))
                            continue;

                        foreach (var sub in Directory.EnumerateDirectories(baseDir, "*", SearchOption.TopDirectoryOnly))
                        {
                            var folder = Path.GetFileName(sub) ?? string.Empty;
                            if (!versionDirPattern.IsMatch(folder))
                                continue;

                            var exeCandidates = new[] { Path.Combine(sub, "bin", "1cv8c.exe"), Path.Combine(sub, "bin", "1cv8.exe"), Path.Combine(sub, "1cv8.exe") };
                            string? foundExe = null;
                            foreach (var c in exeCandidates)
                            {
                                try { if (File.Exists(c)) { foundExe = c; break; } } catch { }
                            }

                            if (foundExe != null)
                            {
                                var arch = root.Equals(pf64, StringComparison.OrdinalIgnoreCase) ? "64-bit" : "32-bit";
                                var friendly = GetDisplayNameForPath(foundExe) ?? ("1C:Предприятие " + folder);
                                var display = friendly + " (" + arch + ")";
                                if (!Platforms.Contains(display))
                                {
                                    Platforms.Add(display);
                                    _platformPaths[display] = foundExe;
                                }
                                // set first discovered platform as selected
                                if (Platforms.Count > 0 && string.IsNullOrWhiteSpace(SelectedPlatform))
                                    SelectedPlatform = Platforms[0];
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private string? GetDisplayNameForPath(string exePath)
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return null;

                var dir = Path.GetDirectoryName(exePath) ?? string.Empty;
                var candidates = new[] { exePath, dir };

                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                        using var uninstall = baseKey.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall");
                        if (uninstall == null) continue;

                        foreach (var sub in uninstall.GetSubKeyNames())
                        {
                            try
                            {
                                using var sk = uninstall.OpenSubKey(sub);
                                if (sk == null) continue;
                                var displayName = sk.GetValue("DisplayName")?.ToString() ?? string.Empty;
                                var installLoc = sk.GetValue("InstallLocation")?.ToString() ?? string.Empty;
                                var displayIcon = sk.GetValue("DisplayIcon")?.ToString() ?? string.Empty;
                                var uninstallStr = sk.GetValue("UninstallString")?.ToString() ?? string.Empty;

                                foreach (var cand in candidates)
                                {
                                    if (!string.IsNullOrWhiteSpace(installLoc) && cand.StartsWith(installLoc, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(displayName))
                                        return displayName;
                                    if (!string.IsNullOrWhiteSpace(displayIcon) && displayIcon.IndexOf(cand, StringComparison.OrdinalIgnoreCase) >= 0 && !string.IsNullOrWhiteSpace(displayName))
                                        return displayName;
                                    if (!string.IsNullOrWhiteSpace(uninstallStr) && uninstallStr.IndexOf(cand, StringComparison.OrdinalIgnoreCase) >= 0 && !string.IsNullOrWhiteSpace(displayName))
                                        return displayName;
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }
    }
}
