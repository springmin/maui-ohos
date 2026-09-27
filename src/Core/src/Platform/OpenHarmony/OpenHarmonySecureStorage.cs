// ISecureStorage for OpenHarmony: values are sealed through HUKS (Universal KeyStore) with a
// device-bound AES-256-GCM key when the device provides the keystore (the host's native engine,
// see OpenHarmonyKeystore); otherwise they fall back to a per-install obfuscation key next to the
// data file (documented as not hardware-backed).
using System.Globalization;
using System.Text;
using Microsoft.Maui.Storage;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonySecureStorage : ISecureStorage
{
    private readonly string _path;
    private readonly string _alias;
    private readonly byte[] _key;

    public OpenHarmonySecureStorage(string? path = null)
    {
        _path = path ?? Path.Combine(OpenHarmonyPaths.DataDirectory, "secure.dat");
        // HUKS keys are app-scoped, but the alias is derived from the store path so separate
        // stores (and the file-backed test instances) never share one keystore key. The prefix
        // namespaces the key for this library and v1 pins the scheme
        // (AES-256-GCM, sealed as nonce||ciphertext||tag).
        _alias = "maui.ohos.securestorage.v1." + PathHash(_path);
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

    /// <summary>
    /// True when this device exposes the HUKS-backed keystore path: the value key is generated
    /// and kept by the system keystore so the ciphertext on disk is device-bound. False is the
    /// honest fallback state: values are obfuscated with the per-install file key only, not
    /// hardware-backed.
    /// </summary>
    public static bool IsHardwareBacked => OpenHarmonyKeystore.IsAvailable;

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
            byte[] cipher;
            try
            {
                cipher = Convert.FromBase64String(value[KeystorePrefix.Length..]);
            }
            catch (FormatException)
            {
                // A corrupted k1: entry reads as absent; malformed data never throws out of
                // GetAsync (a failed keystore decrypt below already answers null).
                return null;
            }
            byte[]? plain = await OpenHarmonyKeystore.DecryptAsync(_alias, cipher);
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        return value;
    }

    public async Task SetAsync(string key, string value)
    {
        Dictionary<string, string> values = Load();
        // Prefer the HUKS-backed keystore; the wrapper never throws.
        if (await OpenHarmonyKeystore.EnsureKeyAsync(_alias))
        {
            byte[]? cipher = await OpenHarmonyKeystore.EncryptAsync(_alias, Encoding.UTF8.GetBytes(value));
            if (cipher is not null)
            {
                values[key] = KeystorePrefix + Convert.ToBase64String(cipher);
                Save(values);
                return;
            }
        }
        // The keystore path failed (no HUKS on this device, or the op was rejected): the value
        // is obfuscated with the per-install file key only. Report it once so the weaker
        // protection is visible at runtime, not just in the header.
        OpenHarmonyStatus.Once("securestorage.filekey",
            "secure storage is using the per-install file key: the HUKS-backed key path failed, values are obfuscated but not hardware-backed");
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
        // Best effort: drop the device-bound key too, so a cleared store leaves no key that
        // could decrypt a stray ciphertext. The call never throws and is intentionally not
        // awaited (ISecureStorage.RemoveAll is synchronous).
        _ = OpenHarmonyKeystore.DeleteKeyAsync(_alias);
    }

    // FNV-1a over the UTF-8 path: a stable alias across processes and runs (HUKS aliases must
    // be stable) and across .NET versions, unlike string.GetHashCode.
    private static string PathHash(string path)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(path))
        {
            hash ^= b;
            hash *= 1099511628211UL;
        }
        return hash.ToString("x16", CultureInfo.InvariantCulture);
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
