using System.IO;
using System.Text.Json;
using Win11Taskbar.Native;

namespace Win11Taskbar.Core;

internal static class StateFile
{
    public static void Write<T>(string path, T value, JsonSerializerOptions options)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Write("WARNING", $"Could not clean temporary state file: {e.Message}");
            }
        }
    }

    public static void PreserveCorruptFile(string path)
    {
        var backup = path + ".corrupt-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Move(path, backup);
            Log.Write("INFO", $"Preserved damaged state in {Path.GetFileName(backup)}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write("WARNING", $"Could not preserve damaged {Path.GetFileName(path)}: {e.Message}");
        }
    }
}

public static class RecoveryJournal
{
    public static string DefaultPath => Path.Combine(Log.Folder, "recovery.json");

    internal static void Save(string path, IEnumerable<WindowSnapshot> snapshots) =>
        StateFile.Write(path, snapshots, WindowSnapshot.JsonOptions);

    public static bool TryRestore(string path)
    {
        WindowSnapshot[] snapshots;
        try
        {
            using var stream = File.OpenRead(path);
            snapshots = JsonSerializer.Deserialize<WindowSnapshot[]>(stream, WindowSnapshot.JsonOptions)
                ?? throw new JsonException("Recovery journal is null.");
            if (snapshots.Any(snapshot => snapshot is null))
                throw new JsonException("Recovery journal contains an empty entry.");
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
        catch (JsonException e)
        {
            Log.Write("WARNING", $"Ignoring damaged recovery journal: {e.Message}");
            StateFile.PreserveCorruptFile(path);
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write("WARNING", $"Could not read recovery journal: {e.Message}");
            return false;
        }

        try
        {
            WindowSnapshot.RestoreAll(snapshots);
            return true;
        }
        catch (Exception e)
        {
            Log.Write("WARNING", $"Could not restore taskbar geometry: {e.Message}");
            return false;
        }
    }
}
