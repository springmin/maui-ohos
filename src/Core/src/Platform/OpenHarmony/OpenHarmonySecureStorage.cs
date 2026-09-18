// ISecureStorage for OpenHarmony: values are obfuscated with a per-install key stored next to
// the data file. This is NOT hardware-backed (the platform keystore/HUKS integration is a
// later iteration), so treat it as tamper-obfuscation rather than real secret protection.
using System.Text;
using Microsoft.Maui.Storage;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonySecureStorage : ISecureStorage
{
    private readonly string _path;
    private readonly byte[] _key;

    public OpenHarmonySecureStorage(string? path = null)
    {
        _path = path ?? Path.Combine(OpenHarmonyPaths.DataDirectory, "secure.dat");
        string keyPath = _path + ".key";
        if (File.Exists(keyPath))
        {
            _key = File.ReadAllBytes(keyPath);
        }
        else
        {
            _key = new byte[32];
            Random.Shared.NextBytes(_key);
            try
            {
                File.WriteAllBytes(keyPath, _key);
            }
            catch
            {
                // Best effort: the key stays in memory for this session.
            }
        }
    }

    public Task<string?> GetAsync(string key)
    {
        Dictionary<string, string> values = Load();
        return Task.FromResult(values.TryGetValue(key, out string? value) ? value : null);
    }

    public Task SetAsync(string key, string value)
    {
        Dictionary<string, string> values = Load();
        values[key] = value;
        Save(values);
        return Task.CompletedTask;
    }

    public bool Remove(string key)
    {
        Dictionary<string, string> values = Load();
        bool removed = values.Remove(key);
        Save(values);
        return removed;
    }

    public void RemoveAll()
    {
        Save(new Dictionary<string, string>());
    }

    private Dictionary<string, string> Load()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(_path))
            {
                return values;
            }
            foreach (string line in File.ReadAllLines(_path))
            {
                string[] parts = line.Split('|', 2);
                if (parts.Length == 2)
                {
                    values[Decode(parts[0])] = Decode(parts[1]);
                }
            }
        }
        catch (Exception ex)
        {
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus($"[maui] secure storage load failed: {ex.GetType().Name}");
        }
        return values;
    }

    private void Save(Dictionary<string, string> values)
    {
        try
        {
            var builder = new StringBuilder();
            foreach (KeyValuePair<string, string> entry in values)
            {
                builder.Append(Encode(entry.Key)).Append('|').Append(Encode(entry.Value)).Append('\n');
            }
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(_path, builder.ToString());
        }
        catch (Exception ex)
        {
            Microsoft.OpenHarmony.Hosting.OpenHarmonyBridge.WriteStatus($"[maui] secure storage save failed: {ex.GetType().Name}");
        }
    }

    private string Encode(string value)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        for (int i = 0; i < bytes.Length; i++)
        {
            bytes[i] ^= _key[i % _key.Length];
        }
        return Convert.ToBase64String(bytes);
    }

    private string Decode(string value)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(value);
            for (int i = 0; i < bytes.Length; i++)
            {
                bytes[i] ^= _key[i % _key.Length];
            }
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }
}
