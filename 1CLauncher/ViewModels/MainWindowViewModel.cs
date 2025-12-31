using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
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
        public ObservableCollection<string> Bases { get; } = new();

        private readonly Dictionary<string, string> _connects = new(StringComparer.OrdinalIgnoreCase);

        private string? _selectedBase;
        public string? SelectedBase
        {
            get => _selectedBase;
            set
            {
                if (SetProperty(ref _selectedBase, value))
                {
                    ConnectionString = FindConnectForBase(value);
                    SaveSettings();
                }
            }
        }

        private string _connectionString = string.Empty;
        public string ConnectionString
        {
            get => _connectionString;
            set => SetProperty(ref _connectionString, value);
        }

        private string _statusMessage = string.Empty;
        public string StatusMessage
        {
            get => _statusMessage;
            set => SetProperty(ref _statusMessage, value);
        }

        // Platforms support
        public ObservableCollection<string> Platforms { get; } = new();
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

        public IAsyncRelayCommand LaunchCommand { get; }

        public MainWindowViewModel()
        {
            var settings = SettingsService.Load();
            Username = settings.Username;
            Domain = settings.Domain;
            ExternalUrl = settings.ExternalUrl;

            LoadBasesFromIbasesFile();

            // load platforms
            LoadInstalledPlatforms();
            if (Platforms.Count == 0)
                StatusMessage = "No 1C platforms found. Use Browse to add one or click Refresh.";
            else
                StatusMessage = $"Found {Platforms.Count} platform(s).";

            // keep existing command for auth
            LaunchCommand = new AsyncRelayCommand(CheckAuthAsync);
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
                        StatusMessage = authenticated ? "Authenticated" : "Authentication failed";
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
                var names = new List<string>();

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

                            names.Add(name);
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

                            names.Add(name);
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
                        names.AddRange(xmlNames);

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
                        names.Add(m.Groups[1].Value);

                    if (!names.Any())
                    {
                        var matchesRu = Regex.Matches(content, @"Наименование\s*=\s*""([^""\]]+)""", RegexOptions.IgnoreCase);
                        foreach (Match m in matchesRu)
                            names.Add(m.Groups[1].Value);
                    }
                }

                foreach (var n in names.Distinct())
                    Bases.Add(n);

                if (Bases.Count > 0)
                    SelectedBase = Bases[0];
            }
            catch
            {
                // Fail silently - file might be absent or unreadable
            }
        }

        public void RefreshPlatforms()
        {
            LoadInstalledPlatforms();
            if (Platforms.Count == 0)
                StatusMessage = "No 1C platforms found. Use Browse to add one or click Refresh.";
            else
                StatusMessage = $"Found {Platforms.Count} platform(s).";
        }

        public void AddPlatformFromPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                if (!File.Exists(path)) return;
                var dir = Path.GetDirectoryName(path) ?? string.Empty;
                var folder = Path.GetFileName(dir) ?? string.Empty;
                var display = folder + " - " + path;
                if (!Platforms.Contains(display)) { Platforms.Add(display); _platformPaths[display] = path; }
                SelectedPlatform = display; PlatformPath = path; StatusMessage = $"Added platform: {display}";
            }
            catch (Exception ex) { StatusMessage = "Error adding platform: " + ex.Message; }
        }

        private void LoadInstalledPlatforms()
        {
            try
            {
                Platforms.Clear();
                _platformPaths.Clear();

                var pf64 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

                var roots = new List<(string path, string arch)>();
                if (!string.IsNullOrWhiteSpace(pf64) && Directory.Exists(Path.Combine(pf64, "1cv8"))) roots.Add((pf64, "64-bit"));
                if (!string.IsNullOrWhiteSpace(pf86) && Directory.Exists(Path.Combine(pf86, "1cv8"))) roots.Add((pf86, "32-bit"));

                var versionDirPattern = new Regex("^\\d+(?:\\.\\d+)*$");

                foreach (var (root, arch) in roots)
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
                                var display = folder + " (" + arch + ") - " + foundExe;
                                if (!Platforms.Contains(display)) { Platforms.Add(display); _platformPaths[display] = foundExe; }
                            }
                        }
                    }
                    catch { }
                }

                // fallback: scan program files for 1cv8.exe
                if (!Platforms.Any())
                {
                    var patterns = new[] { "1cv8c.exe", "1cv8.exe", "1cv8s.exe" };
                    foreach (var root in new[] { pf64, pf86 }.Distinct().Where(p => !string.IsNullOrWhiteSpace(p)))
                    {
                        if (!Directory.Exists(root)) continue;
                        foreach (var pattern in patterns)
                        {
                            try
                            {
                                var matches = Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).Take(200);
                                foreach (var m in matches)
                                {
                                    try
                                    {
                                        var dir = Path.GetFileName(Path.GetDirectoryName(m) ?? string.Empty);
                                        var display = dir + " - " + m;
                                        if (!Platforms.Contains(display)) { Platforms.Add(display); _platformPaths[display] = m; }
                                    }
                                    catch { }
                                }
                            }
                            catch { }
                        }
                    }
                }

                if (Platforms.Count > 0) SelectedPlatform = Platforms[0];
            }
            catch { }
        }
    }
}
