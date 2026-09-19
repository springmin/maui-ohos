
// Sensor Kit for OpenHarmony via the NDK sensor API (sensors/oh_sensor.h). The host subscribes
// through OH_Sensor_* and forwards one reading per event; Accelerometer/Gyroscope/Magnetometer/
// Compass/Barometer/OrientationSensor expose them through the MAUI Essentials interfaces
// (accelerometer values are converted to G units, compass headings to degrees and barometer
// pressure to hectopascals).
using System.Runtime.InteropServices;

namespace Microsoft.Maui.Platform;

internal static class OpenHarmonySensors
{
    internal const int AccelerometerType = 1; // Sensor_Type: SENSOR_TYPE_ACCELEROMETER
    internal const int GyroscopeType = 2;     // SENSOR_TYPE_GYROSCOPE
    internal const int MagneticFieldType = 6; // SENSOR_TYPE_MAGNETIC_FIELD (magnetometer + compass)
    internal const int BarometerType = 8;     // SENSOR_TYPE_BAROMETER
    internal const int OrientationType = 256; // SENSOR_TYPE_ORIENTATION
    private const string HostLibrary = "libopenharmonyhost.so";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void SensorCallback(int type, float x, float y, float z, long timestamp);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_sensor_set_listener")]
    private static extern void SensorSetListener(IntPtr listener);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_sensor_is_supported")]
    private static extern int SensorIsSupported(int type);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_sensor_start")]
    private static extern int SensorStart(int type, int intervalMs);

    [DllImport(HostLibrary, EntryPoint = "ohos_host_sensor_stop")]
    private static extern void SensorStop();

    private static SensorCallback? _callback;
    private static bool _available = true;

    public static bool IsSupported(int type)
    {
        if (!_available) return false;
        try { return SensorIsSupported(type) == 1; }
        catch (DllNotFoundException) { _available = false; return false; }
        catch (EntryPointNotFoundException) { _available = false; return false; }
    }

    public static bool Start(int type, int intervalMs)
    {
        if (!_available) return false;
        try
        {
            _callback ??= OnReading;
            SensorSetListener(Marshal.GetFunctionPointerForDelegate(_callback));
            return SensorStart(type, intervalMs) == 0;
        }
        catch (DllNotFoundException) { _available = false; return false; }
        catch (EntryPointNotFoundException) { _available = false; return false; }
    }

    public static void Stop()
    {
        if (!_available) return;
        try { SensorStop(); }
        catch (Exception) { _available = false; }
    }

    private static void OnReading(int type, float x, float y, float z, long timestamp)
    {
        if (type == AccelerometerType) OpenHarmonyAccelerometer.Instance.OnReading(x, y, z);
        else if (type == GyroscopeType) OpenHarmonyGyroscope.Instance.OnReading(x, y, z);
        else if (type == MagneticFieldType)
        {
            OpenHarmonyMagnetometer.Instance.OnReading(x, y, z);
            OpenHarmonyCompass.Instance.OnReading(x, y, z);
        }
        else if (type == BarometerType) OpenHarmonyBarometer.Instance.OnReading(x);
        else if (type == OrientationType) OpenHarmonyOrientationSensor.Instance.OnReading(x, y, z);
    }

    /// <summary>Installs the sensor implementations as the MAUI Essentials defaults.</summary>
    public static void Install()
    {
        InstallDefault(typeof(Microsoft.Maui.Devices.Sensors.Accelerometer), OpenHarmonyAccelerometer.Instance);
        InstallDefault(typeof(Microsoft.Maui.Devices.Sensors.Gyroscope), OpenHarmonyGyroscope.Instance);
        InstallDefault(typeof(Microsoft.Maui.Devices.Sensors.Magnetometer), OpenHarmonyMagnetometer.Instance);
        InstallDefault(typeof(Microsoft.Maui.Devices.Sensors.Compass), OpenHarmonyCompass.Instance);
        InstallDefault(typeof(Microsoft.Maui.Devices.Sensors.Barometer), OpenHarmonyBarometer.Instance);
        InstallDefault(typeof(Microsoft.Maui.Devices.Sensors.OrientationSensor), OpenHarmonyOrientationSensor.Instance);
    }

    private static void InstallDefault(Type entry, object implementation)
    {
        try
        {
            const System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            // The entry points are get-only, so the backing fields are the settable surface.
            foreach (System.Reflection.FieldInfo field in entry.GetFields(flags))
            {
                if (field.FieldType.IsInstanceOfType(implementation))
                {
                    field.SetValue(null, implementation);
                }
            }
        }
        catch (Exception)
        {
            // The default stays in place when the entry point cannot be replaced.
        }
    }
}

/// <summary>MAUI Essentials accelerometer backed by the OpenHarmony sensor service (G units).</summary>
public sealed class OpenHarmonyAccelerometer : Microsoft.Maui.Devices.Sensors.IAccelerometer
{
    public static readonly OpenHarmonyAccelerometer Instance = new();
    private const float Gravity = 9.80665f;
    private bool _monitoring;

    public bool IsSupported => OpenHarmonySensors.IsSupported(OpenHarmonySensors.AccelerometerType);
    public bool IsMonitoring => _monitoring;

    public event EventHandler<Microsoft.Maui.Devices.Sensors.AccelerometerChangedEventArgs>? ReadingChanged;

    /// <summary>Raised when the measured magnitude exceeds the shake threshold (1s debounce).</summary>
    public event EventHandler? ShakeDetected;

    private static readonly long ShakeDebounceTicks = TimeSpan.TicksPerSecond;
    private long _lastShakeTicks;

    public void Start(Microsoft.Maui.Devices.Sensors.SensorSpeed sensorSpeed)
    {
        if (_monitoring) return;
        _monitoring = OpenHarmonySensors.Start(OpenHarmonySensors.AccelerometerType, IntervalFor(sensorSpeed));
    }

    public void Stop()
    {
        if (!_monitoring) return;
        OpenHarmonySensors.Stop();
        _monitoring = false;
    }

    internal void OnReading(float x, float y, float z)
    {
        ReadingChanged?.Invoke(this, new Microsoft.Maui.Devices.Sensors.AccelerometerChangedEventArgs(
            new Microsoft.Maui.Devices.Sensors.AccelerometerData(x / Gravity, y / Gravity, z / Gravity)));
        double magnitude = Math.Sqrt((x * x + y * y + z * z)) / Gravity;
        long now = DateTime.UtcNow.Ticks;
        if (magnitude > 2.5 && now - _lastShakeTicks > ShakeDebounceTicks)
        {
            _lastShakeTicks = now;
            ShakeDetected?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static int IntervalFor(Microsoft.Maui.Devices.Sensors.SensorSpeed speed) => speed switch
    {
        Microsoft.Maui.Devices.Sensors.SensorSpeed.Fastest => 5,
        Microsoft.Maui.Devices.Sensors.SensorSpeed.Game => 20,
        Microsoft.Maui.Devices.Sensors.SensorSpeed.UI => 60,
        _ => 200,
    };
}

/// <summary>MAUI Essentials gyroscope backed by the OpenHarmony sensor service (rad/s).</summary>
public sealed class OpenHarmonyGyroscope : Microsoft.Maui.Devices.Sensors.IGyroscope
{
    public static readonly OpenHarmonyGyroscope Instance = new();
    private bool _monitoring;

    public bool IsSupported => OpenHarmonySensors.IsSupported(OpenHarmonySensors.GyroscopeType);
    public bool IsMonitoring => _monitoring;

    public event EventHandler<Microsoft.Maui.Devices.Sensors.GyroscopeChangedEventArgs>? ReadingChanged;

    public void Start(Microsoft.Maui.Devices.Sensors.SensorSpeed sensorSpeed)
    {
        if (_monitoring) return;
        _monitoring = OpenHarmonySensors.Start(OpenHarmonySensors.GyroscopeType, OpenHarmonyAccelerometer.IntervalFor(sensorSpeed));
    }

    public void Stop()
    {
        if (!_monitoring) return;
        OpenHarmonySensors.Stop();
        _monitoring = false;
    }

    internal void OnReading(float x, float y, float z)
        => ReadingChanged?.Invoke(this, new Microsoft.Maui.Devices.Sensors.GyroscopeChangedEventArgs(
            new Microsoft.Maui.Devices.Sensors.GyroscopeData(x, y, z)));
}

/// <summary>MAUI Essentials magnetometer backed by the OpenHarmony sensor service (microtesla).</summary>
public sealed class OpenHarmonyMagnetometer : Microsoft.Maui.Devices.Sensors.IMagnetometer
{
    public static readonly OpenHarmonyMagnetometer Instance = new();
    private bool _monitoring;

    public bool IsSupported => OpenHarmonySensors.IsSupported(OpenHarmonySensors.MagneticFieldType);
    public bool IsMonitoring => _monitoring;

    public event EventHandler<Microsoft.Maui.Devices.Sensors.MagnetometerChangedEventArgs>? ReadingChanged;

    public void Start(Microsoft.Maui.Devices.Sensors.SensorSpeed sensorSpeed)
    {
        if (_monitoring) return;
        _monitoring = OpenHarmonySensors.Start(OpenHarmonySensors.MagneticFieldType, OpenHarmonyAccelerometer.IntervalFor(sensorSpeed));
    }

    public void Stop()
    {
        if (!_monitoring) return;
        OpenHarmonySensors.Stop();
        _monitoring = false;
    }

    internal void OnReading(float x, float y, float z)
        => ReadingChanged?.Invoke(this, new Microsoft.Maui.Devices.Sensors.MagnetometerChangedEventArgs(
            new Microsoft.Maui.Devices.Sensors.MagnetometerData(x, y, z)));
}

/// <summary>MAUI Essentials compass backed by the OpenHarmony magnetic field sensor (heading in degrees).</summary>
public sealed class OpenHarmonyCompass : Microsoft.Maui.Devices.Sensors.ICompass
{
    public static readonly OpenHarmonyCompass Instance = new();
    private bool _monitoring;

    public bool IsSupported => OpenHarmonySensors.IsSupported(OpenHarmonySensors.MagneticFieldType);
    public bool IsMonitoring => _monitoring;

    public event EventHandler<Microsoft.Maui.Devices.Sensors.CompassChangedEventArgs>? ReadingChanged;

    public void Start(Microsoft.Maui.Devices.Sensors.SensorSpeed sensorSpeed)
        => Start(sensorSpeed, false);

    /// <summary>The host forwards raw field components, so no low-pass filter is applied.</summary>
    public void Start(Microsoft.Maui.Devices.Sensors.SensorSpeed sensorSpeed, bool applyLowPassFilter)
    {
        if (_monitoring) return;
        _monitoring = OpenHarmonySensors.Start(OpenHarmonySensors.MagneticFieldType, OpenHarmonyAccelerometer.IntervalFor(sensorSpeed));
    }

    public void Stop()
    {
        if (!_monitoring) return;
        OpenHarmonySensors.Stop();
        _monitoring = false;
    }

    internal void OnReading(float x, float y, float z)
    {
        // Magnetic north heading of the x/y field components, normalised to 0..360 degrees.
        double heading = Math.Atan2(y, x) * 180.0 / Math.PI;
        if (heading < 0) heading += 360.0;
        ReadingChanged?.Invoke(this, new Microsoft.Maui.Devices.Sensors.CompassChangedEventArgs(
            new Microsoft.Maui.Devices.Sensors.CompassData(heading)));
    }
}

/// <summary>MAUI Essentials barometer backed by the OpenHarmony sensor service (hectopascals).</summary>
public sealed class OpenHarmonyBarometer : Microsoft.Maui.Devices.Sensors.IBarometer
{
    public static readonly OpenHarmonyBarometer Instance = new();
    private const double PascalPerHectopascal = 100.0;
    private bool _monitoring;

    public bool IsSupported => OpenHarmonySensors.IsSupported(OpenHarmonySensors.BarometerType);
    public bool IsMonitoring => _monitoring;

    public event EventHandler<Microsoft.Maui.Devices.Sensors.BarometerChangedEventArgs>? ReadingChanged;

    public void Start(Microsoft.Maui.Devices.Sensors.SensorSpeed sensorSpeed)
    {
        if (_monitoring) return;
        _monitoring = OpenHarmonySensors.Start(OpenHarmonySensors.BarometerType, OpenHarmonyAccelerometer.IntervalFor(sensorSpeed));
    }

    public void Stop()
    {
        if (!_monitoring) return;
        OpenHarmonySensors.Stop();
        _monitoring = false;
    }

    /// <summary>The host reports the raw pressure in pascals; Essentials expects hectopascals.</summary>
    internal void OnReading(float pressure)
        => ReadingChanged?.Invoke(this, new Microsoft.Maui.Devices.Sensors.BarometerChangedEventArgs(
            new Microsoft.Maui.Devices.Sensors.BarometerData(pressure / PascalPerHectopascal)));
}

/// <summary>MAUI Essentials orientation sensor backed by the OpenHarmony sensor service.</summary>
public sealed class OpenHarmonyOrientationSensor : Microsoft.Maui.Devices.Sensors.IOrientationSensor
{
    public static readonly OpenHarmonyOrientationSensor Instance = new();
    private bool _monitoring;

    public bool IsSupported => OpenHarmonySensors.IsSupported(OpenHarmonySensors.OrientationType);
    public bool IsMonitoring => _monitoring;

    public event EventHandler<Microsoft.Maui.Devices.Sensors.OrientationSensorChangedEventArgs>? ReadingChanged;

    public void Start(Microsoft.Maui.Devices.Sensors.SensorSpeed sensorSpeed)
    {
        if (_monitoring) return;
        _monitoring = OpenHarmonySensors.Start(OpenHarmonySensors.OrientationType, OpenHarmonyAccelerometer.IntervalFor(sensorSpeed));
    }

    public void Stop()
    {
        if (!_monitoring) return;
        OpenHarmonySensors.Stop();
        _monitoring = false;
    }

    internal void OnReading(float x, float y, float z)
    {
        // The host forwards the normalised vector part of the rotation; rebuild the scalar
        // component so the reading is a unit quaternion.
        float w = MathF.Sqrt(MathF.Max(0f, 1f - (x * x + y * y + z * z)));
        ReadingChanged?.Invoke(this, new Microsoft.Maui.Devices.Sensors.OrientationSensorChangedEventArgs(
            new Microsoft.Maui.Devices.Sensors.OrientationSensorData(x, y, z, w)));
    }
}
