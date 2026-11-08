using System.Globalization;
using System.IO;

namespace ChineseBuddy;

public static class WorksheetExport
{
    public static string DefaultFileName(DateTime localTime) =>
        $"汉字复习练习纸-{localTime.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}.pdf";

    public static string MoveToAvailablePath(string temporaryPath, string requestedPath)
    {
        string directory = Path.GetDirectoryName(requestedPath) ?? throw new ArgumentException("导出路径无效。", nameof(requestedPath));
        string name = Path.GetFileNameWithoutExtension(requestedPath);
        string extension = Path.GetExtension(requestedPath);
        for (int suffix = 1; ; suffix++)
        {
            string candidate = suffix == 1 ? requestedPath : Path.Combine(directory, $"{name}-{suffix}{extension}");
            try
            {
                File.Move(temporaryPath, candidate);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
        }
    }
}
