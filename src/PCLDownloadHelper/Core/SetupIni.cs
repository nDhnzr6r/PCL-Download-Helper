using System.IO;
using System.Text;

namespace PclDownloadHelper.Core;

/// <summary>Setup.ini 的读写，保留原有键顺序与换行风格。</summary>
public sealed class SetupIni
{
    private readonly List<string> _lines = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasBom;
    private string _newline = "\r\n";

    public string Path { get; }

    public SetupIni(string path)
    {
        Path = path;
        if (!File.Exists(path)) return;
        var raw = File.ReadAllBytes(path);
        _hasBom = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF;
        var text = new UTF8Encoding(false).GetString(raw, _hasBom ? 3 : 0, raw.Length - (_hasBom ? 3 : 0));
        _newline = text.Contains("\r\n") ? "\r\n" : "\n";
        _lines.AddRange(text.Split(new[] { _newline }, StringSplitOptions.None));
        foreach (var line in _lines)
        {
            var index = line.IndexOf(':');
            if (index <= 0) continue;
            var key = line.Substring(0, index).Trim();
            if (key.Length > 0) _values[key] = line.Substring(index + 1);
        }
    }

    public string? Get(string key)
    {
        return _values.TryGetValue(key, out var value) ? value : null;
    }

    public void Set(string key, string value)
    {
        _values[key] = value;
        for (var i = 0; i < _lines.Count; i++)
        {
            var index = _lines[i].IndexOf(':');
            if (index <= 0) continue;
            if (!string.Equals(_lines[i].Substring(0, index).Trim(), key, StringComparison.OrdinalIgnoreCase)) continue;
            _lines[i] = _lines[i].Substring(0, index) + ":" + value;
            return;
        }
        if (_lines.Count > 0 && _lines[_lines.Count - 1].Length == 0)
            _lines.Insert(_lines.Count - 1, $"{key}:{value}");
        else
            _lines.Add($"{key}:{value}");
    }

    public void Save()
    {
        var text = string.Join(_newline, _lines);
        var bytes = new UTF8Encoding(false).GetBytes(text);
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = Path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
        {
            if (_hasBom) stream.Write(new byte[] { 0xEF, 0xBB, 0xBF }, 0, 3);
            stream.Write(bytes, 0, bytes.Length);
        }
        if (File.Exists(Path)) File.Delete(Path);
        File.Move(temp, Path);
    }
}
