using System;
using System.IO;
using System.Text;
namespace DesktopRelay.App;
public sealed class DiagnosticLog
{
    private readonly object gate = new();
    public string PathName { get; }
    private const long Limit = 1024 * 1024;
    public DiagnosticLog(string directory) => PathName = Path.Combine(directory, "relay-diagnostics.log");
    public void Write(string message)
    {
        string line = $"{DateTimeOffset.Now:O} {message.Replace('\r', ' ').Replace('\n', ' ')}{Environment.NewLine}";
        lock (gate)
        {
            if (File.Exists(PathName) && new FileInfo(PathName).Length >= Limit) File.Move(PathName, PathName + ".previous", true);
            File.AppendAllText(PathName, line, new UTF8Encoding(false));
        }
    }
}
