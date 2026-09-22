// ISecureStorage for OpenHarmony: values are encrypted through HUKS (Universal KeyStore) when
// the ArkTS sink is available; otherwise they fall back to a per-install obfuscation key next
// to the data file (documented as not hardware-backed).
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

    private const string Alias = "dotnet.securestorage.1";
    private const string KeystorePrefix = "k1:";

    public async Task<string?> GetAsync(string key)
    {
        Dictionary<string, string> values = Load();
        if (!values.TryGetValue(key, out string? value))
        {
            return null;
        }
        if (value.StartsWith(KeystorePrefix, StringComparison.Ordinal))
        {
            byte[]? plain = await OpenHarmonyKeystore.DecryptAsync(Alias, Convert.FromBase64String(value[KeystorePrefix.Length..]));
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        return value;
    }

    public async Task SetAsync(string key, string value)
    {
        Dictionary<string, string> values = Load();
        // Prefer the hardware-backed keystore; the wrapper never throws.
        if (await OpenHarmonyKeystore.EnsureKeyAsync(Alias))
        {
            byte[]? cipher = await OpenHarmonyKeystore.EncryptAsync(Alias, Encoding.UTF8.GetBytes(value));
            if (cipher is not null)
            {
                values[key] = KeystorePrefix + Convert.ToBase64String(cipher);
                Save(values);
                return;
            }
        }
        // HUKS is unavailable: the value is obfuscated with the per-install file key only.
        // Report it once so the weaker protection is visible at runtime, not just in the header.
        OpenHarmonyStatus.Once("securestorage.filekey",
            "secure storage is using the per-install file key: HUKS is unavailable, values are obfuscated but not hardware-backed");
        values[key] = value;
        Save(values);
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
