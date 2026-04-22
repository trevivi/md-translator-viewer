using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MdTranslatorViewer.Services;

internal sealed class AppStateService
{
    private readonly string _statePath;
    private readonly string _backupStatePath;

    public AppStateService()
    {
        var stateDirectory = AppStoragePaths.StorageRoot;
        Directory.CreateDirectory(stateDirectory);
        _statePath = AppStoragePaths.AppStatePath;
        _backupStatePath = AppStoragePaths.BackupAppStatePath;
    }

    public ViewerAppState Load()
    {
        return TryLoadState(_statePath) ??
               TryLoadState(_backupStatePath) ??
               ViewerAppState.Default;
    }

    public void Save(ViewerAppState state)
    {
        var tempPath = _statePath + ".tmp";

        try
        {
            var json = JsonSerializer.Serialize(state);
            File.WriteAllText(tempPath, json);

            if (File.Exists(_statePath))
            {
                File.Replace(tempPath, _statePath, _backupStatePath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(tempPath, _statePath);
                File.Copy(_statePath, _backupStatePath, overwrite: true);
            }
        }
        catch
        {
            TryDeleteFile(tempPath);
        }
    }

    private static ViewerAppState? TryLoadState(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ViewerAppState>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }
}

internal sealed record ViewerAppState
{
    public static ViewerAppState Default { get; } = new()
    {
        TranslateEnabled = false,
        TopTabWidthMode = TopTabWidthMode.SizeToTitle,
        ColorThemePreset = ViewerColorThemePreset.DarkModern,
        WindowPlacement = WindowPlacementState.Default,
        OpenDocumentPaths = [],
    };

    public bool TranslateEnabled { get; init; }

    public TopTabWidthMode TopTabWidthMode { get; init; } = TopTabWidthMode.SizeToTitle;

    public ViewerColorThemePreset ColorThemePreset { get; init; } = ViewerColorThemePreset.DarkModern;

    public WindowPlacementState WindowPlacement { get; init; } = WindowPlacementState.Default;

    public string[] OpenDocumentPaths { get; init; } = [];

    public string? SelectedDocumentPath { get; init; }
}

internal enum TopTabWidthMode
{
    Adaptive,
    SizeToTitle,
}

internal sealed record WindowPlacementState
{
    public static WindowPlacementState Default { get; } = new();

    public double Left { get; init; }

    public double Top { get; init; }

    public double Width { get; init; }

    public double Height { get; init; }

    public bool IsMaximized { get; init; }

    [JsonIgnore]
    public bool HasValue =>
        Width > 0 &&
        Height > 0 &&
        !double.IsNaN(Left) &&
        !double.IsNaN(Top);
}
