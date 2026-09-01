using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Microsoft.Data.Sqlite;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace RecordsData;

public class RecordsData : BasePlugin
{
    public override string ModuleName => "RecordsData";
    public override string ModuleVersion => "1.0.4";
    public override string ModuleAuthor => "KZ-Essentials";

    private string _gitHubToken = string.Empty;
    private string _gitHubRepoOwner = string.Empty;
    private string _gitHubRepoName = string.Empty;
    private string _recordsFilePath = string.Empty;
    private string _playersFilePath = string.Empty;
    private string _databaseRelativePath = string.Empty;

    private HttpClient? _httpClient;
    private string _lastSentCreated = "";
    private bool _isSyncing = false;

    private Dictionary<string, PlayerInfo> _playerCache = new Dictionary<string, PlayerInfo>();

    private const string ConfigRelativePath = @"csgo\cfg\plugins\RecordsData\config.json";

    public override void Load(bool hotReload)
    {
        LoadConfig();

        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("RecordsData", "1.0"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", _gitHubToken);

        AddCommand("css_sync", "Full sync all KZ records to GitHub", OnSyncCommand);
        AddCommand("sync", "Full sync all KZ records to GitHub", OnSyncCommand);

        var timer = new System.Timers.Timer(5000);
        timer.Elapsed += (sender, e) =>
        {
            timer.Stop();
            timer.Dispose();
            LoadLastSyncCreated();
            LoadPlayerCache();
            Console.WriteLine("[RecordsData] Database initialized after delay.");
        };
        timer.AutoReset = false;
        timer.Start();
    }

    public override void Unload(bool hotReload)
    {
        SaveLastSyncCreated();
        _httpClient?.Dispose();
    }

    private void LoadConfig()
    {
        string configFullPath = Path.Combine(GetServerRoot(), ConfigRelativePath);
        string configDir = Path.GetDirectoryName(configFullPath)!;

        if (!Directory.Exists(configDir))
            Directory.CreateDirectory(configDir);

        if (!File.Exists(configFullPath))
        {
            var defaultConfig = new ConfigData
            {
                GithubRepo = "",
                GithubToken = "",
                DatabasePath = "csgo/addons/cs2kz/data/cs2kz.sqlite3",
                RecordsPath = "data/records.json",
                PlayersPath = "data/players.json"
            };
            string json = JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(configFullPath, json);
        }

        string jsonContent = File.ReadAllText(configFullPath);
        var config = JsonSerializer.Deserialize<ConfigData>(jsonContent);
        if (config == null)
            throw new Exception("Failed to parse config.json");

        string[] parts = config.GithubRepo?.Split('/') ?? Array.Empty<string>();
        if (parts.Length != 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
            throw new Exception("Invalid github_repo format. Expected 'owner/repo'.");

        _gitHubRepoOwner = parts[0]!;
        _gitHubRepoName = parts[1]!;

        _gitHubToken = config.GithubToken ?? throw new Exception("github_token is missing in config");
        _databaseRelativePath = config.DatabasePath ?? throw new Exception("database_path is missing in config");
        _recordsFilePath = config.RecordsPath ?? throw new Exception("records_path is missing in config");
        _playersFilePath = config.PlayersPath ?? throw new Exception("players_path is missing in config");
    }

    private string GetServerRoot()
    {
        return Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "..", ".."));
    }

    private void OnSyncCommand(CCSPlayerController? player, CommandInfo command)
    {
        Task.Run(() => SyncAllRecords(player));
    }

    private async Task SyncAllRecords(CCSPlayerController? player)
    {
        if (_isSyncing)
        {
            Server.NextFrame(() => SendResult(player, false));
            return;
        }
        _isSyncing = true;

        try
        {
            var allRecords = GetAllRecords();
            if (allRecords.Count == 0)
            {
                Server.NextFrame(() => SendResult(player, false));
                return;
            }

            var gitRecords = allRecords.Select(r => new GitHubRecord
            {
                playername = r.PlayerName,
                steamid = r.SteamId,
                mapname = r.MapName,
                course = r.Course,
                courseid = r.CourseId,
                mode = r.Mode,
                teleports = r.Teleports,
                runtime = r.Runtime,
                time = r.Time,
                created = r.CreatedFormatted
            }).ToList();

            bool recordsSuccess = await ReplaceAllRecordsOnGitHub(gitRecords);
            if (!recordsSuccess)
            {
                Server.NextFrame(() => SendResult(player, false));
                return;
            }

            bool playersSuccess = await UpdatePlayersFileInternal(player);
            if (!playersSuccess)
            {
                Server.NextFrame(() => SendResult(player, false));
                return;
            }

            string maxCreated = allRecords.Max(r => r.CreatedRaw);
            _lastSentCreated = maxCreated;
            SaveLastSyncCreated();

            Server.NextFrame(() => SendResult(player, true));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RecordsData] Sync error: {ex.Message}");
            Server.NextFrame(() => SendResult(player, false));
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private async Task<bool> UpdatePlayersFileInternal(CCSPlayerController? player)
    {
        try
        {
            var allRecords = GetAllRecords();
            var uniqueSteamIds = allRecords.Select(r => r.SteamId).Distinct().ToList();

            if (_playerCache.Count == 0)
                LoadPlayerCache();

            var playerList = new List<PlayerInfo>();
            foreach (var steamId in uniqueSteamIds)
            {
                if (_playerCache.TryGetValue(steamId, out var cached))
                {
                    var dbName = allRecords.First(r => r.SteamId == steamId).PlayerName;
                    if (cached.playername != dbName)
                        cached.playername = dbName;
                    playerList.Add(cached);
                }
                else
                {
                    string playerName = allRecords.First(r => r.SteamId == steamId).PlayerName;
                    string avatarUrl = await FetchPlayerAvatar(steamId);
                    var newPlayer = new PlayerInfo
                    {
                        steamid = steamId,
                        playername = playerName,
                        avatar = avatarUrl ?? ""
                    };
                    _playerCache[steamId] = newPlayer;
                    playerList.Add(newPlayer);
                }
            }

            bool success = await ReplacePlayersOnGitHub(playerList);
            if (success)
                _playerCache = playerList.ToDictionary(p => p.steamid);
            return success;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RecordsData] UpdatePlayersFile error: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> UpdatePlayerInGitHub(string steamId, string playerName)
    {
        try
        {
            if (!_playerCache.TryGetValue(steamId, out var existing))
            {
                string avatar = await FetchPlayerAvatar(steamId);
                var newPlayer = new PlayerInfo
                {
                    steamid = steamId,
                    playername = playerName,
                    avatar = avatar ?? ""
                };
                _playerCache[steamId] = newPlayer;
            }
            else
            {
                if (existing.playername != playerName)
                    existing.playername = playerName;
            }

            var allPlayers = _playerCache.Values.ToList();
            return await ReplacePlayersOnGitHub(allPlayers);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RecordsData] UpdatePlayerInGitHub error: {ex.Message}");
            return false;
        }
    }

    private async Task<string?> FetchPlayerAvatar(string steamId)
    {
        try
        {
            string url = $"https://steamcommunity.com/profiles/{steamId}/?xml=1";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");
            var response = await _httpClient!.SendAsync(request);
            response.EnsureSuccessStatusCode();
            string xmlContent = await response.Content.ReadAsStringAsync();
            var doc = XDocument.Parse(xmlContent);
            var avatarFull = doc.Descendants("avatarFull").FirstOrDefault();
            return avatarFull?.Value?.Trim();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RecordsData] FetchPlayerAvatar error for {steamId}: {ex.Message}");
            return null;
        }
    }

    private void LoadPlayerCache()
    {
        try
        {
            string url = $"https://api.github.com/repos/{_gitHubRepoOwner}/{_gitHubRepoName}/contents/{_playersFilePath}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "RecordsData");
            var response = _httpClient!.SendAsync(request).Result;
            if (response.IsSuccessStatusCode)
            {
                var content = response.Content.ReadAsStringAsync().Result;
                using var jsonDoc = JsonDocument.Parse(content);
                if (jsonDoc.RootElement.TryGetProperty("content", out var contentElement))
                {
                    string? base64 = contentElement.GetString();
                    if (!string.IsNullOrEmpty(base64))
                    {
                        string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                        var players = JsonSerializer.Deserialize<List<PlayerInfo>>(decoded);
                        if (players != null)
                            _playerCache = players.ToDictionary(p => p.steamid);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RecordsData] LoadPlayerCache error: {ex.Message}");
        }
    }

    private async Task<bool> ReplacePlayersOnGitHub(List<PlayerInfo> players)
    {
        try
        {
            string url = $"https://api.github.com/repos/{_gitHubRepoOwner}/{_gitHubRepoName}/contents/{_playersFilePath}";
            string? sha = null;

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                var response = await _httpClient!.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using var jsonDoc = JsonDocument.Parse(content);
                    if (jsonDoc.RootElement.TryGetProperty("sha", out var shaElement))
                        sha = shaElement.GetString();
                }
            }

            string json = JsonSerializer.Serialize(players, new JsonSerializerOptions { WriteIndented = true });
            string newBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            var commitData = new
            {
                message = "Update players.json",
                content = newBase64,
                sha = sha
            };
            var jsonCommit = JsonSerializer.Serialize(commitData);
            var httpContent = new StringContent(jsonCommit, Encoding.UTF8, "application/json");

            using var putRequest = new HttpRequestMessage(HttpMethod.Put, url);
            putRequest.Content = httpContent;
            var putResponse = await _httpClient!.SendAsync(putRequest);
            return putResponse.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RecordsData] ReplacePlayersOnGitHub error: {ex.Message}");
            return false;
        }
    }

    private List<RecordInfo> GetAllRecords()
    {
        string fullDbPath = GetDatabasePath();
        if (!File.Exists(fullDbPath)) return new List<RecordInfo>();

        var records = new List<RecordInfo>();
        using var connection = new SqliteConnection($"Data Source={fullDbPath}");
        connection.Open();

        string query = @"
            SELECT 
                p.Alias AS playername,
                p.SteamID64 AS steamid,
                m.Name AS mapname,
                mc.Name AS course,
                mc.StageID AS courseid,
                md.ShortName AS mode,
                t.RunTime AS time,
                t.Teleports AS teleports,
                t.Created AS created_raw,
                strftime('%H:%M:%S %d.%m.%Y', datetime(t.Created, '+3 hours')) AS created_formatted
            FROM Times t
            JOIN Players p ON t.SteamID64 = p.SteamID64
            JOIN MapCourses mc ON t.MapCourseID = mc.ID
            JOIN Maps m ON mc.MapID = m.ID
            JOIN Modes md ON t.ModeID = md.ID
            WHERE t.RunTime > 0
            ORDER BY t.Created DESC";

        using var cmd = new SqliteCommand(query, connection);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            double time = reader.GetDouble(6);
            string createdRaw = reader.GetString(8);
            string createdFormatted = reader.IsDBNull(9) ? "" : reader.GetString(9);

            var rec = new RecordInfo
            {
                PlayerName = reader.GetString(0),
                SteamId = reader.GetString(1),
                MapName = reader.GetString(2),
                Course = reader.GetString(3),
                CourseId = reader.GetInt32(4),
                Mode = reader.GetString(5),
                Time = time,
                Teleports = reader.GetInt32(7),
                CreatedRaw = createdRaw,
                CreatedFormatted = createdFormatted,
                Runtime = FormatTime(time)
            };
            records.Add(rec);
        }
        return records;
    }

    private async Task<bool> AddRecordToGitHub(RecordInfo record)
    {
        try
        {
            string url = $"https://api.github.com/repos/{_gitHubRepoOwner}/{_gitHubRepoName}/contents/{_recordsFilePath}";
            List<GitHubRecord> existingRecords = new List<GitHubRecord>();
            string? sha = null;

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                var response = await _httpClient!.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using var jsonDoc = JsonDocument.Parse(content);
                    if (jsonDoc.RootElement.TryGetProperty("sha", out var shaElement))
                        sha = shaElement.GetString();
                    if (jsonDoc.RootElement.TryGetProperty("content", out var contentElement))
                    {
                        string? base64 = contentElement.GetString();
                        if (!string.IsNullOrEmpty(base64))
                        {
                            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
                            existingRecords = JsonSerializer.Deserialize<List<GitHubRecord>>(decoded) ?? new List<GitHubRecord>();
                        }
                    }
                }
            }

            var newGitRecord = new GitHubRecord
            {
                playername = record.PlayerName,
                steamid = record.SteamId,
                mapname = record.MapName,
                course = record.Course,
                courseid = record.CourseId,
                mode = record.Mode,
                teleports = record.Teleports,
                runtime = record.Runtime,
                time = record.Time,
                created = record.CreatedFormatted
            };

            existingRecords.Insert(0, newGitRecord);
            string json = JsonSerializer.Serialize(existingRecords, new JsonSerializerOptions { WriteIndented = true });
            string newBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            var commitData = new
            {
                message = $"Add new record - {record.PlayerName} on {record.MapName} {record.Course} ({record.Mode})",
                content = newBase64,
                sha = sha
            };
            var jsonCommit = JsonSerializer.Serialize(commitData);
            var httpContent = new StringContent(jsonCommit, Encoding.UTF8, "application/json");

            using var putRequest = new HttpRequestMessage(HttpMethod.Put, url);
            putRequest.Content = httpContent;
            var putResponse = await _httpClient!.SendAsync(putRequest);
            return putResponse.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RecordsData] GitHub prepend error: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ReplaceAllRecordsOnGitHub(List<GitHubRecord> records)
    {
        try
        {
            string url = $"https://api.github.com/repos/{_gitHubRepoOwner}/{_gitHubRepoName}/contents/{_recordsFilePath}";
            string? sha = null;

            using (var request = new HttpRequestMessage(HttpMethod.Get, url))
            {
                var response = await _httpClient!.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    using var jsonDoc = JsonDocument.Parse(content);
                    if (jsonDoc.RootElement.TryGetProperty("sha", out var shaElement))
                        sha = shaElement.GetString();
                }
            }

            string json = JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true });
            string newBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));

            var commitData = new
            {
                message = "Full sync of all records",
                content = newBase64,
                sha = sha
            };
            var jsonCommit = JsonSerializer.Serialize(commitData);
            var httpContent = new StringContent(jsonCommit, Encoding.UTF8, "application/json");

            using var putRequest = new HttpRequestMessage(HttpMethod.Put, url);
            putRequest.Content = httpContent;
            var putResponse = await _httpClient!.SendAsync(putRequest);
            return putResponse.IsSuccessStatusCode;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RecordsData] GitHub replace error: {ex.Message}");
            return false;
        }
    }

    private string GetMaxCreatedFromDb()
    {
        string fullDbPath = GetDatabasePath();
        if (!File.Exists(fullDbPath)) return "";
        using var connection = new SqliteConnection($"Data Source={fullDbPath}");
        connection.Open();
        string query = "SELECT MAX(Created) FROM Times WHERE RunTime > 0";
        using var cmd = new SqliteCommand(query, connection);
        var result = cmd.ExecuteScalar();
        return result?.ToString() ?? "";
    }

    private void LoadLastSyncCreated()
    {
        string filePath = Path.Combine(ModuleDirectory, "last_sync.txt");
        string currentMax = GetMaxCreatedFromDb();

        if (File.Exists(filePath))
        {
            string fileValue = File.ReadAllText(filePath).Trim();
            if (string.Compare(currentMax, fileValue, StringComparison.Ordinal) > 0)
            {
                _lastSentCreated = currentMax;
                SaveLastSyncCreated();
            }
            else
            {
                _lastSentCreated = fileValue;
            }
        }
        else
        {
            _lastSentCreated = currentMax;
            SaveLastSyncCreated();
        }
    }

    private void SaveLastSyncCreated()
    {
        string filePath = Path.Combine(ModuleDirectory, "last_sync.txt");
        File.WriteAllText(filePath, _lastSentCreated);
    }

    private string GetDatabasePath()
    {
        string serverRoot = GetServerRoot();
        return Path.Combine(serverRoot, _databaseRelativePath);
    }

    private string FormatTime(double timeSec)
    {
        if (timeSec <= 0) return "0.000";
        int hours = (int)(timeSec / 3600);
        int minutes = (int)((timeSec % 3600) / 60);
        int seconds = (int)(timeSec % 60);
        int ms = (int)((timeSec - Math.Floor(timeSec)) * 1000);
        if (hours > 0) return $"{hours}:{minutes:D2}:{seconds:D2}.{ms:D3}";
        if (minutes > 0) return $"{minutes}:{seconds:D2}.{ms:D3}";
        return $"{seconds}.{ms:D3}";
    }

    private void SendResult(CCSPlayerController? player, bool success)
    {
        if (player == null)
        {
            Console.WriteLine($"[RecordsData] Data synchronization {(success ? "complete" : "failed")}");
            return;
        }

        string color = success ? $"{ChatColors.Green}" : $"{ChatColors.Red}";
        string status = success ? "complete" : "failed";
        player.PrintToChat($" [{ChatColors.Blue}KZ{ChatColors.White}] Data synchronization {color}{status}");
    }
}

public class RecordInfo
{
    public string PlayerName { get; set; } = "";
    public string SteamId { get; set; } = "";
    public string MapName { get; set; } = "";
    public string Course { get; set; } = "";
    public int CourseId { get; set; }
    public string Mode { get; set; } = "";
    public int Teleports { get; set; }
    public string Runtime { get; set; } = "";
    public double Time { get; set; }
    public string CreatedRaw { get; set; } = "";
    public string CreatedFormatted { get; set; } = "";
}

public class GitHubRecord
{
    public string playername { get; set; } = "";
    public string steamid { get; set; } = "";
    public string mapname { get; set; } = "";
    public string course { get; set; } = "";
    public int courseid { get; set; }
    public string mode { get; set; } = "";
    public int teleports { get; set; }
    public string runtime { get; set; } = "";
    public double time { get; set; }
    public string created { get; set; } = "";
}

public class PlayerInfo
{
    public string steamid { get; set; } = "";
    public string playername { get; set; } = "";
    public string avatar { get; set; } = "";
}

public class ConfigData
{
    [JsonPropertyName("github_repo")]
    public string? GithubRepo { get; set; }

    [JsonPropertyName("github_token")]
    public string? GithubToken { get; set; }

    [JsonPropertyName("database_path")]
    public string? DatabasePath { get; set; }

    [JsonPropertyName("records_path")]
    public string? RecordsPath { get; set; }

    [JsonPropertyName("players_path")]
    public string? PlayersPath { get; set; }
}