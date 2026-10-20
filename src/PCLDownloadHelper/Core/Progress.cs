namespace PclDownloadHelper.Core;

/// <summary>安装进度快照。</summary>
public sealed class InstallProgress
{
    public double Overall { get; init; }
    public string Stage { get; init; } = "";
    public string Detail { get; init; } = "";
    public long Speed { get; init; }
    public int FilesDone { get; init; }
    public int FilesTotal { get; init; }
}

/// <summary>折算总进度并按时间节流上报。</summary>
public sealed class ProgressTracker
{
    private readonly IProgress<InstallProgress> _target;
    private readonly object _lock = new();
    private readonly DateTime _start = DateTime.UtcNow;

    private double _base;
    private double _span;
    private double _within;
    private string _stage = "";
    private string _detail = "";
    private long _bytes;
    private long _lastBytes;
    private DateTime _lastReport = DateTime.MinValue;
    private DateTime _lastSpeedAt = DateTime.UtcNow;
    private long _speed;
    private int _filesDone;
    private int _filesTotal;

    public ProgressTracker(IProgress<InstallProgress> target)
    {
        _target = target;
    }

    public void EnterStage(string stage, double baseValue, double span)
    {
        lock (_lock)
        {
            _stage = stage;
            _base = baseValue;
            _span = span;
            _within = 0;
            _detail = "";
        }
        Report(true);
    }

    public void SetWithin(double value)
    {
        lock (_lock) _within = Math.Max(_within, Math.Min(1, value));
        Report();
    }

    public void SetFiles(int done, int total)
    {
        lock (_lock)
        {
            _filesDone = done;
            _filesTotal = total;
        }
        Report();
    }

    public void SetDetail(string detail)
    {
        lock (_lock) _detail = detail;
        Report();
    }

    public void AddBytes(long count)
    {
        lock (_lock) _bytes += count;
        Report();
    }

    public void ResetFiles()
    {
        lock (_lock)
        {
            _filesDone = 0;
            _filesTotal = 0;
        }
    }

    public void Report(bool force = false)
    {
        InstallProgress snapshot;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var elapsed = (now - _lastSpeedAt).TotalSeconds;
            if (elapsed >= 0.6)
            {
                var instant = (long)((_bytes - _lastBytes) / elapsed);
                _speed = _speed == 0 ? instant : (long)(_speed * 0.6 + instant * 0.4);
                _lastBytes = _bytes;
                _lastSpeedAt = now;
            }
            if (!force && (now - _lastReport).TotalMilliseconds < 100) return;
            _lastReport = now;
            snapshot = new InstallProgress
            {
                Overall = Math.Min(1, _base + _span * Math.Max(0, Math.Min(1, _within))),
                Stage = _stage,
                Detail = _detail,
                Speed = _speed,
                FilesDone = _filesDone,
                FilesTotal = _filesTotal
            };
        }
        _target.Report(snapshot);
    }

    public TimeSpan Elapsed => DateTime.UtcNow - _start;
}
