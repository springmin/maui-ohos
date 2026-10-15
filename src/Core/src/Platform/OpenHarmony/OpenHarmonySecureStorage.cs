// ISecureStorage for OpenHarmony: values are stored in the platform Asset Store (OH_Asset_*,
// DEVICE_FIRST_UNLOCKED; host_asset.c) when the device provides it - the preferred,
// credential-oriented backend - and sealed through HUKS (Universal KeyStore) with a device-bound
// AES-256-GCM key otherwise (the host's native engine, see OpenHarmonyKeystore). Only when
// neither strong backend is available does the store fall back to a per-install obfuscation key
// next to the data file (documented as not hardware-backed); OpenHarmonySecureStorage.FailClosed
// (the default) refuses even that: a Set that cannot use a strong backend throws and no value is
// written, so a credential-bearing app never silently downgrades. An app that accepts the weak
// fallback opts out by setting FailClosed = false.
//
// The data file stays the key index and the legacy store: a key written through the Asset Store
// carries the "a1:" marker (the value itself lives only in the store), a HUKS-sealed value
// carries "k1:", and pre-existing file-key values stay readable. Reads consult the backend the
// index names, so old data (k1: / file fallback entries) parses unchanged.
using System.Globalization;
using System.Text;
using Microsoft.Maui.Storage;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonySecureStorage : ISecureStorage
{
    /// <summary>Bytes of the per-install fallback key (AES-256 key material for the XOR stream).</summary>
    private const int FileKeyBytes = 32;

    /// <summary>
    /// The data-file marker of a value that lives in the Asset Store. Not a secret: it names the
    /// backend, so RemoveAll can enumerate the store-backed keys without the value ever touching
    /// this file.
    /// </summary>
    private const string AssetMarker = "a1:";

    private readonly string _path;
    private readonly string _alias;
    private readonly string _assetAliasPrefix;
    private readonly byte[] _key;
    // Serializes the load-modify-save sequences (and the file reads) of this store: two
    // concurrent Set calls would otherwise apply their values to the same snapshot and the
    // later save would drop the earlier one. Never held across an await.
    private readonly object _fileSync = new();

    public OpenHarmonySecureStorage(string? path = null)
    {
        _path = path ?? Path.Combine(OpenHarmonyPaths.DataDirectory, "secure.dat");
        // HUKS keys are app-scoped, but the alias is derived from the store path so separate
        // stores (and the file-backed test instances) never share one keystore key. The prefix
        // namespaces the key for this library and v1 pins the scheme
        // (AES-256-GCM, sealed as nonce||ciphertext||tag).
        _alias = "maui.ohos.securestorage.v1." + PathHash(_path);
        // One Asset Store alias per key: the store hash namespaces the install and the key hash
        // keeps the key name (possibly sensitive itself) out of the system asset database and
        // every log. Both halves are stable across processes and runs.
        _assetAliasPrefix = "maui.ohos.securestorage.asset.v1." + PathHash(_path) + ".";
        _key = LoadOrCreateFileKey(_path + ".key");
    }

    /// <summary>
    /// The per-install fallback key next to the data file. A missing, truncated, wrong-length
    /// or unreadable key file is replaced: a zero-length file would otherwise divide by zero
    /// in <see cref="Encode"/>, and a wrong-length key cannot round-trip existing values. The
    /// write is best effort with owner-only permissions; the new key stays in memory when it
    /// fails (the old values then read as empty, exactly like a lost key).
    /// </summary>
    private static byte[] LoadOrCreateFileKey(string keyPath)
    {
        try
        {
            byte[] existing = File.ReadAllBytes(keyPath);
            if (existing.Length == FileKeyBytes)
            {
                EnsureOwnerOnlyFile(keyPath);
                return existing;
            }
        }
        catch
        {
            // Missing or unreadable: fall through to regeneration.
        }
        byte[] key = new byte[FileKeyBytes];
        Random.Shared.NextBytes(key);
        try
        {
            File.WriteAllBytes(keyPath, key);
            EnsureOwnerOnlyFile(keyPath);
        }
        catch
        {
            // Best effort: the key stays in memory for this session.
        }
        return key;
    }

    /// <summary>Owner read/write only (0600) for the key material; best effort off-device.</summary>
    private static void EnsureOwnerOnlyFile(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch
        {
            // Not a Unix file system: the app sandbox still bounds the file to this app.
        }
    }

    /// <summary>
    /// Security policy for a failed strong chain. True (the default) is fail-closed: a Set that
    /// can use neither the Asset Store nor the HUKS-backed keystore throws and nothing is
    /// written, and an indexed value whose backend read fails throws instead of reading as
    /// absent. False is the explicit opt-out for hosts without a strong backend: the value is
    /// obfuscated with the per-install file key and the downgrade is reported once through
    /// <see cref="OpenHarmonyStatus.Once"/>. Credential-bearing apps keep the default; the
    /// weak fallback exists for headless and reduced hosts that opt out deliberately.
    /// </summary>
    public static bool FailClosed { get; set; } = true;

    /// <summary>
    /// True when this device exposes a device-bound credential backend: the Asset Store
    /// (OH_Asset_*) or the HUKS-backed keystore. False is the honest fallback state: values
    /// would be obfuscated with the per-install file key only, not hardware-backed (and the
    /// default fail-closed policy refuses to write them).
    /// </summary>
    public static bool IsHardwareBacked => OpenHarmonyAssetStore.IsAvailable || OpenHarmonyKeystore.IsAvailable;

    private const string KeystorePrefix = "k1:";

    public async Task<string?> GetAsync(string key)
    {
        Dictionary<string, string> values;
        lock (_fileSync)
        {
            values = Load();
        }
        if (!values.TryGetValue(key, out string? value))
        {
            return null;
        }
        if (value == AssetMarker)
        {
            // The index names the Asset Store backend: a missing entry or a failed read is the
            // same data-loss shape as an undecryptable sealed value, and fail-closed reports it
            // instead of answering absent.
            (int rc, string data) = await OpenHarmonyAssetStore.GetAsync(AssetAlias(key)).ConfigureAwait(false);
            if (rc == OpenHarmonyAssetStore.RcOk)
            {
                byte[] plain;
                try
                {
                    plain = Convert.FromBase64String(data);
                }
                catch (FormatException)
                {
                    // A corrupted answer reads as absent; malformed data never throws out of
                    // GetAsync (mirroring the k1: shape below).
                    return null;
                }
                return Encoding.UTF8.GetString(plain);
            }
            if (FailClosed)
            {
                throw new InvalidOperationException(
                    $"secure storage is fail-closed and the Asset Store read failed for '{key}'");
            }
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
            byte[]? plain = await OpenHarmonyKeystore.DecryptAsync(_alias, cipher).ConfigureAwait(false);
            if (plain is null && FailClosed)
            {
                // Fail-closed: the value exists but its HUKS-backed read failed (no keystore,
                // a rejected op, or lost key material). Reading as absent would hide the
                // failure; the caller decides what an unreadable credential means.
                throw new InvalidOperationException(
                    $"secure storage is fail-closed and the HUKS-backed read failed for '{key}'");
            }
            return plain is null ? null : Encoding.UTF8.GetString(plain);
        }
        return value;
    }

    public async Task SetAsync(string key, string value)
    {
        // Prefer the Asset Store - the credential backend; the HUKS keystore is the second
        // strong tier, and the per-install file key the weak fallback that fail-closed refuses.
        // Every network/keystore/store call happens before the file lock, so the store never
        // blocks a thread across an await.
        if (await OpenHarmonyAssetStore.PutAsync(AssetAlias(key), Convert.ToBase64String(Encoding.UTF8.GetBytes(value))).ConfigureAwait(false))
        {
            RecordAssetEntry(key);
            return;
        }
        if (await OpenHarmonyKeystore.EnsureKeyAsync(_alias).ConfigureAwait(false))
        {
            byte[]? cipher = await OpenHarmonyKeystore.EncryptAsync(_alias, Encoding.UTF8.GetBytes(value)).ConfigureAwait(false);
            if (cipher is not null)
            {
                lock (_fileSync)
                {
                    Dictionary<string, string> values = Load();
                    values[key] = KeystorePrefix + Convert.ToBase64String(cipher);
                    Save(values);
                }
                return;
            }
        }
        if (FailClosed)
        {
            // Fail-closed (the default): no strong backend answered, so the value is not
            // persisted at all - a credential is never silently downgraded to the per-install
            // file key. The throw is the observable signal; the caller owns the failure (no
            // fallback write, no status note).
            throw new InvalidOperationException(
                "secure storage is fail-closed and neither the Asset Store nor the HUKS-backed key path is available: the value was not stored (OpenHarmonySecureStorage.FailClosed)");
        }
        // The opt-out path: the value is obfuscated with the per-install file key only. Report
        // it once so the weaker protection is visible at runtime, not just in the header.
        OpenHarmonyStatus.Once("securestorage.filekey",
            "secure storage is using the per-install file key: no device-bound backend answered, values are obfuscated but not hardware-backed");
        lock (_fileSync)
        {
            Dictionary<string, string> values = Load();
            values[key] = value;
            Save(values);
        }
    }

    public bool Remove(string key)
    {
        bool removed;
        bool wasAsset;
        lock (_fileSync)
        {
            Dictionary<string, string> values = Load();
            wasAsset = values.TryGetValue(key, out string? existing) && existing == AssetMarker;
            removed = values.Remove(key);
            Save(values);
        }
        // Best effort: drop the store entry too (the index entry is already gone, so the value
        // is unreachable even if the background delete fails). The call never throws.
        if (wasAsset)
        {
            _ = OpenHarmonyAssetStore.RemoveAsync(AssetAlias(key));
        }
        return removed;
    }

    public void RemoveAll()
    {
        List<string> assetKeys = new();
        lock (_fileSync)
        {
            Dictionary<string, string> values = Load();
            foreach (KeyValuePair<string, string> entry in values)
            {
                if (entry.Value == AssetMarker)
                {
                    assetKeys.Add(entry.Key);
                }
            }
            Save(new Dictionary<string, string>());
        }
        // Best effort: drop the indexed store entries and the device-bound key too, so a
        // cleared store leaves no value that could be read again. The calls never throw and are
        // intentionally not awaited (ISecureStorage.RemoveAll is synchronous).
        _ = RemoveAssetEntriesAsync(assetKeys);
        _ = OpenHarmonyKeystore.DeleteKeyAsync(_alias);
    }

    private async Task RemoveAssetEntriesAsync(List<string> keys)
    {
        foreach (string key in keys)
        {
            await OpenHarmonyAssetStore.RemoveAsync(AssetAlias(key)).ConfigureAwait(false);
        }
    }

    /// <summary>The key's Asset Store alias (see the constructor for the scheme).</summary>
    private string AssetAlias(string key) => _assetAliasPrefix + Hash64(key);

    /// <summary>
    /// Records that the key lives in the Asset Store (the value itself is not copied here).
    /// The marker also replaces any legacy value for the key, so the index always names exactly
    /// one backend.
    /// </summary>
    private void RecordAssetEntry(string key)
    {
        lock (_fileSync)
        {
            Dictionary<string, string> values = Load();
            values[key] = AssetMarker;
            Save(values);
        }
    }

    // FNV-1a over the UTF-8 text: a stable alias/hash across processes and runs, unlike
    // string.GetHashCode. The key name stays out of the asset database (the hash is opaque).
    private static string PathHash(string path) => Hash64(path);

    private static string Hash64(string value)
    {
        ulong hash = 14695981039346656037UL;
        foreach (byte b in Encoding.UTF8.GetBytes(value))
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
            EnsureOwnerOnlyFile(_path);
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
