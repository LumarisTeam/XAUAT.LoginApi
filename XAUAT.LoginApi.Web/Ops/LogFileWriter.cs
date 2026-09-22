using System.Text;

namespace XAUAT.LoginApi.Ops;

/// <summary>
/// 按天轮转的日志文件写入器。
/// <para>
/// 对齐 Flask 的 <c>TimedRotatingFileHandler(when='midnight', backupCount=31)</c>：
/// 当前文件 <c>logs/log.log</c>，跨天时改名为 <c>logs/log.log.yyyy-MM-dd</c>，保留最近 31 个。
/// 启动时由 <see cref="InMemoryLogStore.LoadFromFiles"/> 回填，使重启后 <c>/Logs</c> 仍有历史。
/// </para>
/// <para>
/// 刻意手写而不用 Serilog：Serilog 的 File sink 在 Native AOT 下有反射告警，
/// 而本文件格式简单且必须与 Flask 的既有日志文件保持兼容。
/// </para>
/// </summary>
internal sealed class LogFileWriter
{
    private const int BackupCount = 31;
    private const string FileName = "log.log";

    /// <summary>
    /// **无 BOM** 的 UTF-8。
    /// <para>
    /// 必须显式声明：<c>Encoding.UTF8</c> 这个静态属性带 <c>encoderShouldEmitUTF8Identifier: true</c>，
    /// 用它建文件会在开头写入 <c>EF BB BF</c>，于是**第一行**永远匹配不上回填正则
    /// （<c>^\[...</c> 之前多了三个字节），重启后那一行日志就静默丢失了。
    /// Flask 的 <c>open(..., encoding='utf-8')</c> 不写 BOM，这里必须一致，
    /// 否则切回 Flask 时同样读不了我们写的文件。
    /// </para>
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly Lock _gate = new();
    private readonly string _currentPath;

    private DateOnly _currentDate;

    public LogFileWriter(string directory)
    {
        _directory = directory;
        _currentPath = Path.Combine(directory, FileName);
        _currentDate = DateOnly.FromDateTime(DateTime.Now);
        Directory.CreateDirectory(directory);
    }

    public void Append(string line)
    {
        lock (_gate)
        {
            try
            {
                RotateIfNeeded();
                File.AppendAllText(_currentPath, line + Environment.NewLine, Utf8NoBom);
            }
            catch (IOException)
            {
                // 磁盘满/权限问题不该把请求打挂——日志是尽力而为
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void RotateIfNeeded()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _currentDate) return;

        if (File.Exists(_currentPath))
        {
            var rotated = $"{_currentPath}.{_currentDate:yyyy-MM-dd}";
            File.Move(_currentPath, rotated, overwrite: true);
            PruneOldFiles();
        }

        _currentDate = today;
    }

    private void PruneOldFiles()
    {
        var rotatedFiles = Directory.GetFiles(_directory, $"{FileName}.*");
        if (rotatedFiles.Length <= BackupCount) return;

        // 文件名里的日期是定长 yyyy-MM-dd，字典序即时间序
        Array.Sort(rotatedFiles, StringComparer.Ordinal);
        foreach (var file in rotatedFiles[..^BackupCount])
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
            }
        }
    }
}
