using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace PeriView.App.Services;

/// <summary>
/// 简单日志记录器，提供静态方法记录日志到文件
/// </summary>
public static class Logger
{
    private static readonly string LogFilePath;
    private static readonly object LockObject = new();
    private static volatile bool _isEnabled = true;

    static Logger()
    {
        try
        {
            // 日志目录：用户数据目录/PeriView/Logs
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var logDir = Path.Combine(appData, "PeriView", "Logs");
            Directory.CreateDirectory(logDir);

            // 日志文件名：PeriView_yyyyMMdd_HHmmss.log
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            LogFilePath = Path.Combine(logDir, $"PeriView_{timestamp}.log");

            // 清理旧日志（保留最近7天）
            CleanupOldLogs(logDir);

            Info($"日志系统初始化完成，日志文件：{LogFilePath}");
        }
        catch (Exception ex)
        {
            // 如果初始化失败，使用备用路径（桌面）
            try
            {
                var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                LogFilePath = Path.Combine(desktop, $"PeriView_{DateTime.Now:yyyyMMdd_HHmmss}.log");
                File.WriteAllText(LogFilePath, $"[ERROR] 日志系统初始化失败，使用备用路径：{ex.Message}{Environment.NewLine}");
            }
            catch
            {
                // 如果备用路径也失败，则无法记录日志
                LogFilePath = string.Empty;
            }
        }
    }

    /// <summary>
    /// 记录信息级别日志
    /// </summary>
    public static void Info(string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Log("INFO", message, null, memberName, filePath, lineNumber);
    }

    /// <summary>
    /// 记录警告级别日志
    /// </summary>
    public static void Warning(string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Log("WARN", message, null, memberName, filePath, lineNumber);
    }

    /// <summary>
    /// 记录错误级别日志（带异常）
    /// </summary>
    public static void Error(string message, Exception? exception = null,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Log("ERROR", message, exception, memberName, filePath, lineNumber);
    }

    /// <summary>
    /// 记录调试级别日志（仅Debug模式）
    /// </summary>
    [Conditional("DEBUG")]
    public static void Debug(string message,
        [CallerMemberName] string memberName = "",
        [CallerFilePath] string filePath = "",
        [CallerLineNumber] int lineNumber = 0)
    {
        Log("DEBUG", message, null, memberName, filePath, lineNumber);
    }

    /// <summary>
    /// 启用或禁用日志记录
    /// </summary>
    public static void SetEnabled(bool enabled)
    {
        _isEnabled = enabled;
    }

    /// <summary>
    /// 获取当前日志文件路径
    /// </summary>
    public static string GetLogFilePath()
    {
        return LogFilePath;
    }

    /// <summary>
    /// 获取日志目录占用的磁盘空间（MB）
    /// </summary>
    public static double GetLogDirectorySizeMb()
    {
        try
        {
            var logDir = Path.GetDirectoryName(LogFilePath);
            if (string.IsNullOrEmpty(logDir) || !Directory.Exists(logDir))
                return 0.0;

            var files = Directory.GetFiles(logDir, "*.log");
            long totalBytes = 0;
            foreach (var file in files)
            {
                var info = new FileInfo(file);
                totalBytes += info.Length;
            }

            return totalBytes / (1024.0 * 1024.0);
        }
        catch
        {
            return 0.0;
        }
    }

    /// <summary>
    /// 打开日志目录
    /// </summary>
    public static void OpenLogDirectory()
    {
        try
        {
            var logDir = Path.GetDirectoryName(LogFilePath);
            if (!string.IsNullOrEmpty(logDir) && Directory.Exists(logDir))
            {
                Process.Start("explorer.exe", logDir);
            }
        }
        catch (Exception ex)
        {
            // 记录错误，但无法使用日志系统（可能循环）
            Trace.WriteLine($"[Logger] 无法打开日志目录：{ex.Message}");
        }
    }

    private static void Log(string level, string message, Exception? exception,
        string memberName, string filePath, int lineNumber)
    {
        if (!_isEnabled || string.IsNullOrEmpty(LogFilePath))
            return;

        try
        {
            var fileName = Path.GetFileNameWithoutExtension(filePath);
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            var logLine = $"{timestamp} [{level}] {fileName}.{memberName}:{lineNumber} - {message}";

            if (exception != null)
            {
                logLine += $"{Environment.NewLine}异常: {exception.GetType().Name}: {exception.Message}{Environment.NewLine}{exception.StackTrace}";
            }

            lock (LockObject)
            {
                // 检查文件大小，如果超过5MB则轮转
                if (File.Exists(LogFilePath))
                {
                    var fileInfo = new FileInfo(LogFilePath);
                    if (fileInfo.Length > 5 * 1024 * 1024) // 5MB
                    {
                        RotateLogFile();
                    }
                }

                File.AppendAllText(LogFilePath, logLine + Environment.NewLine);
            }
        }
        catch
        {
            // 忽略日志记录期间的错误
        }
    }

    private static void RotateLogFile()
    {
        try
        {
            var dir = Path.GetDirectoryName(LogFilePath);
            var baseName = Path.GetFileNameWithoutExtension(LogFilePath);
            var ext = Path.GetExtension(LogFilePath);

            // 新文件名：baseName_rotation_yyyyMMdd_HHmmss.ext
            var rotationTime = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var newName = $"{baseName}_rotation_{rotationTime}{ext}";
            var newPath = Path.Combine(dir!, newName);

            File.Move(LogFilePath, newPath);
        }
        catch
        {
            // 轮转失败，继续使用原文件
        }
    }

    private static void CleanupOldLogs(string logDir)
    {
        try
        {
            var files = Directory.GetFiles(logDir, "*.log");
            var cutoff = DateTime.Now.AddDays(-7);

            foreach (var file in files)
            {
                var info = new FileInfo(file);
                if (info.LastWriteTime < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // 忽略清理错误
        }
    }
}