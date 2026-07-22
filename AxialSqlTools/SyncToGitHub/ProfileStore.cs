using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;

public static class ProfileStore
{
    private static readonly string _path = AxialSqlTools.UserConfigPaths.GitHubSyncProfilesFile;
    private static readonly string _legacyPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "AxialSQL", "github-sync-profiles.json");

    public static List<GitHubSyncProfile> Load()
    {
        try
        {
            var path = _path;
            if (!File.Exists(path) && File.Exists(_legacyPath))
            {
                path = _legacyPath;
            }
            if (!File.Exists(path)) return new List<GitHubSyncProfile>();
            var json = File.ReadAllText(path);
            return JsonConvert.DeserializeObject<List<GitHubSyncProfile>>(json);
        }
        catch { return new List<GitHubSyncProfile>(); }
    }

    public static void Save(IEnumerable<GitHubSyncProfile> profiles)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(_path, JsonConvert.SerializeObject(profiles, Formatting.Indented));
    }
}
