using System;
using System.IO;
using System.Text.Json;

namespace DesktopRelay.App;
public sealed class Settings
{
    public int EntryPort { get; set; }
    public int Mode { get; set; }
    public string CustomProxy { get; set; } = "http://127.0.0.1:7892";
    public double Left { get; set; } = 80;
    public double Top { get; set; } = 100;
    public bool Topmost { get; set; }
    public string View { get; set; } = "capsule";
    private static string PathName => Path.Combine(AppContext.BaseDirectory, "settings.json");
    public static Settings Load()
    {
        if (!File.Exists(PathName)) return new();
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(PathName)) ?? throw new InvalidDataException("Empty settings"); }
        catch (Exception ex) { throw new InvalidOperationException("设置读取失败，已停止入口启动，避免改变固定端口。请检查应用旁的 settings.json。", ex); }
    }
    public void Save()
    {
        string temp = PathName + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, PathName, true);
    }
}
