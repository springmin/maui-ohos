// File-backed IPreferences for OpenHarmony: values are stored as tagged lines in the app's
// data directory (the ArkTS ability's files dir, or the temp directory outside an app).
using System.Globalization;
using System.Text;
using Microsoft.Maui.Storage;
using Microsoft.OpenHarmony.Hosting;

namespace Microsoft.Maui.Platform;

public sealed class OpenHarmonyPreferences : IPreferences
{
    private readonly string _path;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly object _sync = new();

    public OpenHarmonyPreferences(string? path = null)
    {
        _path = path ?? Path.Combine(OpenHarmonyPaths.DataDirectory, "preferences.txt");
        Load();
    }

    /// <summary>Storage file (exposed for tests/diagnostics).</summary>
    public string FilePath => _path;

    private static string Key(string? sharedName, string key) => $"{(sharedName ?? string.Empty)}|{key}";

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }
            foreach (string line in File.ReadAllLines(_path))
            {
                string[] parts = line.Split('\t', 3);
                if (parts.Length == 3)
                {
                    _values[parts[0]] = parts[1] + "\t" + parts[2];
                }
            }
        }
        catch (Exception ex)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] preferences load failed: {ex.GetType().Name}");
        }
    }

    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            var builder = new StringBuilder();
            foreach (KeyValuePair<string, string> entry in _values)
            {
                builder.Append(entry.Key).Append('\t').Append(entry.Value).Append('\n');
            }
            File.WriteAllText(_path, builder.ToString());
        }
        catch (Exception ex)
        {
            OpenHarmonyBridge.WriteStatus($"[maui] preferences save failed: {ex.GetType().Name}");
        }
    }

    public bool ContainsKey(string key, string? sharedName = null)
    {
        lock (_sync)
        {
            return _values.ContainsKey(Key(sharedName, key));
        }
    }

    public void Remove(string key, string? sharedName = null)
    {
        lock (_sync)
        {
            _values.Remove(Key(sharedName, key));
            Save();
        }
    }

    public void Clear(string? sharedName = null)
    {
        lock (_sync)
        {
            string prefix = $"{(sharedName ?? string.Empty)}|";
            foreach (string stored in _values.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
            {
                _values.Remove(stored);
            }
            Save();
        }
    }

    public void Set<T>(string key, T value, string? sharedName = null)
    {
        lock (_sync)
        {
            _values[Key(sharedName, key)] = $"{TypeTag(typeof(T))}\t{Format(value)}";
            Save();
        }
    }

    public T Get<T>(string key, T defaultValue, string? sharedName = null)
    {
        lock (_sync)
        {
            if (!_values.TryGetValue(Key(sharedName, key), out string? stored))
            {
                return defaultValue;
            }
            string[] parts = stored.Split('\t', 2);
            if (parts.Length != 2 || parts[0] != TypeTag(typeof(T)))
            {
                return defaultValue;
            }
            try
            {
                return (T)Parse(typeof(T), parts[1]);
            }
            catch
            {
                return defaultValue;
            }
        }
    }

    private static string TypeTag(Type type) => type.Name;

    private static string Format(object? value) => value switch
    {
        null => string.Empty,
        DateTime date => date.ToString("O", CultureInfo.InvariantCulture),
        DateTimeOffset offset => offset.ToString("O", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static object Parse(Type type, string value) => type switch
    {
        Type t when t == typeof(string) => value,
        Type t when t == typeof(bool) => bool.Parse(value),
        Type t when t == typeof(int) => int.Parse(value, CultureInfo.InvariantCulture),
        Type t when t == typeof(long) => long.Parse(value, CultureInfo.InvariantCulture),
        Type t when t == typeof(double) => double.Parse(value, CultureInfo.InvariantCulture),
        Type t when t == typeof(float) => float.Parse(value, CultureInfo.InvariantCulture),
        Type t when t == typeof(DateTime) => DateTime.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        Type t when t == typeof(DateTimeOffset) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
        _ => throw new NotSupportedException(type.Name),
    };
}
