using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MdTranslatorViewer.Services;

internal static class AppStoragePaths
{
    private const string PortableMarkerFileName = "portable.flag";
    private const string PortableDataDirectoryName = "data";
    private const string ProductDirectoryName = "MdTranslatorViewer";
    private static readonly Lazy<StorageContext> Context = new(CreateContext);

    public static string BaseDirectory => Context.Value.BaseDirectory;

    public static string StorageRoot => Context.Value.StorageRoot;

    public static bool IsPortable => Context.Value.IsPortable;

    public static bool PortableModeRequested => Context.Value.PortableModeRequested;

    public static string AppStatePath => Path.Combine(StorageRoot, "app-state.json");

    public static string BackupAppStatePath => Path.Combine(StorageRoot, "app-state.backup.json");

    public static string DiagnosticLogPath => Path.Combine(StorageRoot, "diagnostics.log");

    public static string TranslationCacheDirectory => Path.Combine(StorageRoot, "TranslationCache");

    public static string WebViewSessionsDirectory => Path.Combine(StorageRoot, "WebView2Sessions");

    public static string CreateScopedMutexName(string baseName)
    {
        return $"{baseName}.{Context.Value.InstanceScopeSuffix}";
    }

    public static string CreateScopedPipeName(string baseName)
    {
        return $"{baseName}.{Context.Value.InstanceScopeSuffix}";
    }

    private static StorageContext CreateContext()
    {
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var instanceScopeSuffix = CreateInstanceScopeSuffix(baseDirectory);
        var portableModeRequested = File.Exists(Path.Combine(baseDirectory, PortableMarkerFileName));

        if (portableModeRequested)
        {
            var portableStorageRoot = Path.Combine(baseDirectory, PortableDataDirectoryName);
            if (TryEnsureDirectory(portableStorageRoot))
            {
                return new StorageContext(
                    baseDirectory,
                    portableStorageRoot,
                    instanceScopeSuffix,
                    true,
                    true);
            }

            var fallbackPortableRoot = Path.Combine(
                GetDefaultStorageRoot(),
                "Portable",
                instanceScopeSuffix);
            Directory.CreateDirectory(fallbackPortableRoot);
            return new StorageContext(
                baseDirectory,
                fallbackPortableRoot,
                instanceScopeSuffix,
                false,
                true);
        }

        var defaultStorageRoot = GetDefaultStorageRoot();
        Directory.CreateDirectory(defaultStorageRoot);
        return new StorageContext(
            baseDirectory,
            defaultStorageRoot,
            instanceScopeSuffix,
            false,
            false);
    }

    private static string GetDefaultStorageRoot()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            ProductDirectoryName);
    }

    private static bool TryEnsureDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CreateInstanceScopeSuffix(string baseDirectory)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(baseDirectory));
        return Convert.ToHexString(bytes, 0, 8);
    }

    private sealed record StorageContext(
        string BaseDirectory,
        string StorageRoot,
        string InstanceScopeSuffix,
        bool IsPortable,
        bool PortableModeRequested);
}
