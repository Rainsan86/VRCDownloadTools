using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using UnityEngine;
using Debug = UnityEngine.Debug;
using ZipFile = System.IO.Compression.ZipFile;

namespace VRCDownloadTool
{
    public static class DownloadTools
    {
        private const string LogPrefix = "[VRC下载工具] ";

        public static Stream DownloadFile(string url)
        {
            using (WebClient client = new WebClient())
                return new MemoryStream(client.DownloadData(url));
        }

        /// <summary>
        /// 用多线程方式把 <paramref name="url"/> 下载到 <paramref name="outputFile"/>，
        /// 完成后调用 <paramref name="callback"/>。多线程失败时自动回退到单线程，回调只会执行一次。
        /// </summary>
        public static void DownloadAndSaveFile(string url, string outputFile, Action callback = null,
            Action<float> progress = null)
        {
            ParallelDownloader downloader = new ParallelDownloader(url, outputFile);
            downloader.Start(progress, (success, error) =>
            {
                if (success)
                {
                    if (callback != null)
                        callback.Invoke();
                    return;
                }

                if (downloader.WasCancelled)
                {
                    if (callback != null)
                        callback.Invoke();
                    return;
                }

                Debug.LogWarning(LogPrefix + "多线程下载失败，改用单线程重试：" + error);
                DownloadAndSaveFileSingleConnection(url, outputFile, callback);
            });
        }

        /// <summary>
        /// 原有的单线程下载方式，作为兜底保留。
        /// WebClient 必须等下载结束再释放，否则正在进行的下载会被取消。
        /// </summary>
        public static void DownloadAndSaveFileSingleConnection(string url, string outputFile, Action callback)
        {
            WebClient client = new WebClient();
            client.DownloadFileCompleted += (sender, args) =>
            {
                try
                {
                    if (args.Error != null)
                        Debug.LogError(LogPrefix + "下载失败：" + args.Error);
                }
                finally
                {
                    client.Dispose();
                    if (callback != null)
                        callback.Invoke();
                }
            };

            try
            {
                client.DownloadFileAsync(new Uri(url), outputFile);
            }
            catch (Exception exception)
            {
                Debug.LogError(LogPrefix + "下载失败：" + exception);
                client.Dispose();
                if (callback != null)
                    callback.Invoke();
            }
        }

        /// <summary>
        /// 解压压缩包。Windows 上的 AssetRipper 是 .zip，Linux / macOS 从 2.0.0 起是 .tar.xz，
        /// tar 包交给系统自带的 tar 命令处理。
        /// </summary>
        public static void ExtractArchive(string fileName, string outputPath)
        {
            if (fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                ZipFile.ExtractToDirectory(fileName, outputPath);
                return;
            }

            if (fileName.EndsWith(".tar.xz", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase) ||
                fileName.EndsWith(".tar", StringComparison.OrdinalIgnoreCase))
            {
                ExtractTarArchive(fileName, outputPath);
                return;
            }

            throw new NotSupportedException("不支持的压缩包格式：" + Path.GetExtension(fileName));
        }

        private static void ExtractTarArchive(string fileName, string outputPath)
        {
            Directory.CreateDirectory(outputPath);

            ProcessStartInfo startInfo = new ProcessStartInfo("tar")
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-xf");
            startInfo.ArgumentList.Add(Path.GetFullPath(fileName));
            startInfo.ArgumentList.Add("-C");
            startInfo.ArgumentList.Add(Path.GetFullPath(outputPath));

            using (Process process = Process.Start(startInfo))
            {
                if (process == null)
                    throw new Exception("无法启动 tar 命令来解压 " + fileName);

                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (process.ExitCode != 0)
                    throw new Exception("tar 解压失败（退出码 " + process.ExitCode + "）：" + error);
            }
        }
    }
}
