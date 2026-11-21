using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Win32;
namespace Win11Taskbar.Core;

public static class Log
{
    static readonly object Gate=new();
    public static string Folder=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"Win11Taskbar");
    public static bool DebugEnabled {get;set;}
    public static void Write(string level,string message){if(level=="DEBUG"&&!DebugEnabled)return;try{lock(Gate){var dir=Path.Combine(Folder,"Logs");Directory.CreateDirectory(dir);var p=Path.Combine(dir,"latest.log");if(File.Exists(p)&&new FileInfo(p).Length>2_000_000)File.Move(p,p+".1",true);File.AppendAllText(p,$"{DateTimeOffset.Now:O} [{level}] {message}{Environment.NewLine}");}}catch(IOException){}catch(UnauthorizedAccessException){}}
}
public sealed class SettingsService
{
    readonly string folder;
    public SettingsService(string? folder=null){this.folder=folder??Log.Folder;}
    public string FilePath=>Path.Combine(folder,"settings.json");
    static readonly JsonSerializerOptions Options=new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase,WriteIndented=true};
    public Settings Load()
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            var settings = JsonSerializer.Deserialize<Settings>(stream, Options)
                ?? throw new JsonException("Settings file is null.");
            settings.Validate();
            return settings;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new();
        }
        catch (JsonException e)
        {
            Log.Write("WARNING", $"Using default settings: {e.Message}");
            StateFile.PreserveCorruptFile(FilePath);
            return new();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Write("WARNING", $"Could not read settings: {e.Message}");
            return new();
        }
    }

    public void Save(Settings settings) => StateFile.Write(FilePath, settings, Options);
}
public static class AutostartService
{
    const string Key=@"Software\Microsoft\Windows\CurrentVersion\Run";
    public static void Set(bool enabled){using var key=Registry.CurrentUser.CreateSubKey(Key);if(enabled)key.SetValue("Win11Taskbar",$"\"{Environment.ProcessPath}\" --minimized");else key.DeleteValue("Win11Taskbar",false);}
    public static bool Enabled(){using var key=Registry.CurrentUser.OpenSubKey(Key);return key?.GetValue("Win11Taskbar")!=null;}
}
public static class UpdateService
{
    public static async Task<string> CheckAsync(string endpoint)
    {
        if(!Uri.TryCreate(endpoint,UriKind.Absolute,out var uri)||uri.Scheme!="https")return "Источник обновлений пока не настроен. Укажите HTTPS GitHub Releases API в settings.json (updateEndpoint). Ничего не загружено и не установлено.";
        var currentVersion = typeof(UpdateService).Assembly.GetName().Version!;
        using var client=new HttpClient{Timeout=TimeSpan.FromSeconds(12)};client.DefaultRequestHeaders.UserAgent.ParseAdd($"Win11Taskbar/{currentVersion.ToString(3)}");
        using var stream=await client.GetStreamAsync(uri);using var doc=await JsonDocument.ParseAsync(stream);
        var tag=doc.RootElement.GetProperty("tag_name").GetString()??"";
        return Version.TryParse(tag.TrimStart('v','V'),out var version)&&version>currentVersion?$"Доступна версия {tag}. Откройте страницу релиза вашего проекта для ручного скачивания.":$"Установлена актуальная версия ({currentVersion.ToString(3)}).";
    }
}

