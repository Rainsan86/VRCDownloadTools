using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace VRCDownloadTool
{
    public class RipperHandler
    {
        private const string URI = "http://127.0.0.1:42176/";
        private const int SPECIFIC_PORT = 42176;
        private const string LogPrefix = "[VRC下载工具] ";

        /// <summary>
        /// AssetRipper 发布包里的可执行文件名。2.x 仍然是 AssetRipper.GUI.Free，
        /// 这里多列几个名字，避免上游改名后直接失效。
        /// </summary>
        private static readonly string[] ExecutableNames =
        {
            "AssetRipper.GUI.Free.exe",
            "AssetRipper.GUI.Free",
            "AssetRipper.exe",
            "AssetRipper"
        };

        /// <summary>用户手动指定的 AssetRipper 可执行文件完整路径（由编辑器设置写入）。</summary>
        public static string CustomExecutablePath { get; set; }

        public string WorkingDirectory { get; private set; }

        private static bool Is64BitProcess
        {
            get
            {
                return RuntimeInformation.ProcessArchitecture != Architecture.Arm &&
                       RuntimeInformation.ProcessArchitecture != Architecture.Arm64;
            }
        }

        private static string PlatformToken
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return "win";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return "mac";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                    return "linux";
                throw new Exception("无法识别的操作系统平台");
            }
        }

        /// <summary>
        /// AssetRipper 官方发布地址。
        /// 注意：Windows 是 .zip，Linux / macOS 从 2.0.0 起改成了 .tar.xz。
        /// </summary>
        private string DownloadURL
        {
            get
            {
                string baseURL = "https://github.com/AssetRipper/AssetRipper/releases/latest/download/AssetRipper_";
                baseURL += PlatformToken + "_";
                baseURL += Is64BitProcess ? "x64" : "arm64";
                baseURL += RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ".zip" : ".tar.xz";
                return baseURL;
            }
        }

        private string ArchiveFileName
        {
            get
            {
                return DownloadURL.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
                    ? "AssetRipper.zip"
                    : "AssetRipper.tar.xz";
            }
        }

        /// <summary>实际使用的 AssetRipper 可执行文件路径（优先使用手动指定的）。</summary>
        public string ExecutablePath
        {
            get
            {
                string custom = CustomExecutablePath;
                if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
                    return custom;
                return FindBundledExecutable();
            }
        }

        /// <summary>实际使用的工作目录。</summary>
        public string ExecutableDirectory
        {
            get
            {
                string path = ExecutablePath;
                if (string.IsNullOrEmpty(path))
                    return WorkingDirectory;

                string directory = Path.GetDirectoryName(path);
                return string.IsNullOrEmpty(directory) ? WorkingDirectory : directory;
            }
        }

        /// <summary>是否正在使用用户手动指定的 AssetRipper。</summary>
        public bool IsUsingCustomExecutable
        {
            get
            {
                string custom = CustomExecutablePath;
                return !string.IsNullOrEmpty(custom) && File.Exists(custom);
            }
        }

        public bool isPresent => File.Exists(ExecutablePath);

        public bool IsRunning => Process.GetProcessesByName("AssetRipper.GUI.Free").Length > 0;
        public bool IsWorking { get; private set; }
        public float Progress { get; private set; }

        private static StreamWriter myStreamWriter;
        private ParallelDownloader _downloader;

        public void SetWorkingDirectory()
        {
            WorkingDirectory = Path.Combine(Directory.GetParent(Application.dataPath).FullName, "AssetRipper");
        }

        /// <summary>
        /// 在默认目录里找 AssetRipper 可执行文件；解压出子目录或者上游改了文件名时也能找到。
        /// </summary>
        private string FindBundledExecutable()
        {
            if (string.IsNullOrEmpty(WorkingDirectory))
                return string.Empty;

            foreach (string name in ExecutableNames)
            {
                string candidate = Path.Combine(WorkingDirectory, name);
                if (File.Exists(candidate))
                    return candidate;
            }

            if (!Directory.Exists(WorkingDirectory))
                return Path.Combine(WorkingDirectory, ExecutableNames[0]);

            try
            {
                foreach (string file in Directory.GetFiles(WorkingDirectory, "*", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileName(file);
                    foreach (string candidate in ExecutableNames)
                    {
                        if (name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                            return file;
                    }
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(LogPrefix + "搜索 AssetRipper 可执行文件失败：" + exception.Message);
            }

            return Path.Combine(WorkingDirectory, ExecutableNames[0]);
        }

        /// <summary>
        /// 下载并解压 AssetRipper。<paramref name="progress"/> 收到 0~100 的下载进度，
        /// <paramref name="onDone"/> 收到是否下载并解压成功。
        /// </summary>
        public void Download(Action<bool> onDone, Action<float> progress = null)
        {
            try
            {
                if (!Directory.Exists(WorkingDirectory))
                    Directory.CreateDirectory(WorkingDirectory);
                else
                    Directory.Delete(WorkingDirectory, true);
            }
            catch (Exception exception)
            {
                Debug.LogError(LogPrefix + "无法重置 AssetRipper 目录：" + exception);
                Invoke(onDone, false);
                return;
            }

            string archivePath = Path.Combine(WorkingDirectory, ArchiveFileName);

            ParallelDownloader downloader = new ParallelDownloader(DownloadURL, archivePath);
            _downloader = downloader;

            downloader.Start(progress, (success, error) =>
            {
                _downloader = null;

                if (downloader.WasCancelled)
                {
                    DeleteFileIfExists(archivePath);
                    Invoke(onDone, false);
                    return;
                }

                if (success)
                {
                    ExtractAndFinish(archivePath, onDone);
                    return;
                }

                Debug.LogWarning(LogPrefix + "AssetRipper 多线程下载失败，改用单线程重试：" + error);
                DownloadTools.DownloadAndSaveFileSingleConnection(DownloadURL, archivePath, () =>
                {
                    if (downloader.WasCancelled)
                    {
                        DeleteFileIfExists(archivePath);
                        Invoke(onDone, false);
                        return;
                    }

                    ExtractAndFinish(archivePath, onDone);
                });
            });
        }

        /// <summary>取消正在进行的 AssetRipper 下载。</summary>
        public void CancelDownload()
        {
            ParallelDownloader downloader = _downloader;
            if (downloader != null)
                downloader.Abort();
        }

        private void ExtractAndFinish(string archivePath, Action<bool> onDone)
        {
            try
            {
                DownloadTools.ExtractArchive(archivePath, WorkingDirectory);
                DeleteFileIfExists(archivePath);
                Invoke(onDone, true);
            }
            catch (Exception exception)
            {
                Debug.LogError(LogPrefix + "解压 AssetRipper 失败：" + exception);
                Invoke(onDone, false);
            }
        }

        private static void Invoke(Action<bool> onDone, bool success)
        {
            if (onDone != null)
                onDone.Invoke(success);
        }

        private static void DeleteFileIfExists(string file)
        {
            try
            {
                if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception exception)
            {
                Debug.LogWarning(LogPrefix + "无法删除文件 " + file + "：" + exception.Message);
            }
        }

        public void StartApplication()
        {
            Process process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                WorkingDirectory = ExecutableDirectory,
                Arguments = $"--port {SPECIFIC_PORT} --headless",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            process.OutputDataReceived += (sender, eventArgs) =>
            {
                string data = eventArgs.Data ?? String.Empty;
                Debug.Log(data);
                float? p = GetProgress(data);
                if(p == null) return;
                Progress = p.Value;
            };
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                process.StartInfo.FileName = ExecutablePath;
                process.Start();
                process.StandardInput.AutoFlush = true;
                myStreamWriter = process.StandardInput;
                process.BeginOutputReadLine();
            }
            else
            {
                // Make sure the file is executable first
                Process chmodProcess = new Process
                {
                    StartInfo = new ProcessStartInfo("chmod", $"+x \"{ExecutablePath}\"")
                    {
                        CreateNoWindow = true
                    },
                    EnableRaisingEvents = true
                };
                chmodProcess.Exited += (sender, args) =>
                {
                    process.StartInfo.FileName = ExecutablePath;
                    process.Start();
                    process.StandardInput.AutoFlush = true;
                    myStreamWriter = process.StandardInput;
                    process.BeginOutputReadLine();
                };
                chmodProcess.Start();
            }
        }

        public void StopApplication()
        {
            foreach (Process process in Process.GetProcessesByName("AssetRipper.GUI.Free"))
            {
                process.Kill();
            }
        }

        private float? GetProgress(string inp)
        {
            string[] s = inp.Split(' ');
            if (s.Length <= 0) return null;
            if (s[0].ToLower() != "exportprogress") return null;
            string progressString = s[2];
            progressString = progressString.TrimStart('(');
            progressString = progressString.TrimEnd(')');
            string[] progressSplit = progressString.Split('/');
            int num = Convert.ToInt32(progressSplit[0]);
            int den = Convert.ToInt32(progressSplit[1]);
            return (float) num / den;
        }

        public async void Rip(string assetFile, string outputDirectoryName, Action complete = null)
        {
            if (!IsRunning) StartApplication();
            IsWorking = true;
            // Create HTTP Client
            using HttpClient httpClient = new HttpClient();
            // Reset
            await httpClient.PostAsync(URI + "Reset", new StringContent(""));
            // Load Target File
            Dictionary<string, string> loadParameters = new Dictionary<string, string>
            {
                ["Path"] = Path.GetFullPath(assetFile)
            };
            await httpClient.PostAsync(URI + "LoadFolder", new FormUrlEncodedContent(loadParameters));
            // Export to Unity Project
            Dictionary<string, string> exportParameters = new Dictionary<string, string>
            {
                ["Path"] = outputDirectoryName
            };
            await httpClient.PostAsync(URI + "Export/UnityProject", new FormUrlEncodedContent(exportParameters));
            IsWorking = false;
            StopApplication();
            complete?.Invoke();
            Progress = 0;
        }
    }
}
