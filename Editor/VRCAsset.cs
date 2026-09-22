#if VRC_SDK_VRCSDK3
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BestHTTP.JSON;
using UnityEngine;
using UnityEngine.Networking;
using VRC.Core;

namespace VRCDownloadTool.Editor
{
    public class VRCAsset
    {
        /// <summary>文件名中"资产名"部分的最大长度，避免超出 Windows 路径长度限制。</summary>
        private const int MaxNameLengthInFileName = 60;

        /// <summary>进度条里资产本体占的比重，剩下的留给图片。</summary>
        private const float BundleProgressWeight = 95f;

        private const string LogPrefix = "[VRC下载工具] ";

        public string Name { get; }
        public string Id { get; }
        public Texture2D Texture { get; }
        public int Version { get; }
        public string FileLocation { get; private set; }

        /// <summary>随资产一起下载下来的原图路径，没有时为 null。</summary>
        public string ImageLocation { get; private set; }

        private byte[] _fileBytes;

        /// <summary>
        /// 原始的 AssetBundle 内容。按需从 <see cref="FileLocation"/> 读取，
        /// 这样刚下载完的大文件不会一直占着内存。
        /// </summary>
        public byte[] FileBytes
        {
            get
            {
                if (_fileBytes != null)
                    return _fileBytes;
                if (string.IsNullOrEmpty(FileLocation) || !File.Exists(FileLocation))
                    return null;
                try
                {
                    return _fileBytes = File.ReadAllBytes(FileLocation);
                }
                catch (Exception exception)
                {
                    Debug.LogError(LogPrefix + "无法读取文件 " + FileLocation + "：" + exception.Message);
                    return null;
                }
            }
            private set { _fileBytes = value; }
        }

        public BuildPlatforms[] SupportedPlatforms { get; private set; } = Array.Empty<BuildPlatforms>();

        private VRCAssetType _vrcAssetType;
        private ApiWorld _world;
        private ApiAvatar _avatar;

        private ParallelDownloader _downloader;
        private bool _cancelled;

        private string _fileEnding
        {
            get
            {
                switch (_vrcAssetType)
                {
                    case VRCAssetType.World:
                        return "w";
                    case VRCAssetType.Avatar:
                        return "a";
                }
                return String.Empty;
            }
        }

        public VRCAsset(ApiWorld world)
        {
            _vrcAssetType = VRCAssetType.World;
            _world = world;
            Name = world.name;
            Id = world.id;
            Texture = ReflectingTools.GetApiModelTextureFromCache(world.id);
            Version = world.version;
            List<BuildPlatforms> supportedPlatforms = new List<BuildPlatforms>();
            _world.Fetch(
                new[]
                {
                    BuildPlatforms.StandaloneWindows,
                    BuildPlatforms.Android,
                    BuildPlatforms.iOS
                }.GetPlatformString(), container =>
            {
                Json.JObject j = (Json.JObject) container.Data;
                Json.JArray up = j["unityPackages"].Array;
                foreach (Json.Token token in up)
                {
                    Json.JObject l = token.Object;
                    switch (l["platform"].StringInstance.ToLower())
                    {
                        case "standalonewindows":
                            supportedPlatforms.Add(BuildPlatforms.StandaloneWindows);
                            break;
                        case "android":
                            supportedPlatforms.Add(BuildPlatforms.Android);
                            break;
                        case "ios":
                            supportedPlatforms.Add(BuildPlatforms.iOS);
                            break;
                        case "web":
                            supportedPlatforms.Add(BuildPlatforms.Web);
                            break;
                    }
                }
                SupportedPlatforms = supportedPlatforms.ToArray();
            }, container =>
            {
                Debug.LogError(LogPrefix + "获取世界支持的平台列表失败。");
            });
        }
        
        public VRCAsset(ApiAvatar avatar)
        {
            _vrcAssetType = VRCAssetType.Avatar;
            _avatar = avatar;
            Name = avatar.name;
            Id = avatar.id;
            Texture = ReflectingTools.GetApiModelTextureFromCache(avatar.id);
            List<BuildPlatforms> supportedPlatforms = new List<BuildPlatforms>();
            foreach (ApiAvatar.UnityPackage avatarUnityPackage in avatar.unityPackages)
            {
                if(avatarUnityPackage == null || avatarUnityPackage.platform == null) continue;
                switch (avatarUnityPackage.platform.ToLower())
                {
                    case "standalonewindows":
                        supportedPlatforms.Add(BuildPlatforms.StandaloneWindows);
                        break;
                    case "android":
                        supportedPlatforms.Add(BuildPlatforms.Android);
                        break;
                    case "ios":
                        supportedPlatforms.Add(BuildPlatforms.iOS);
                        break;
                    case "web":
                        supportedPlatforms.Add(BuildPlatforms.Web);
                        break;
                }
            }
            SupportedPlatforms = supportedPlatforms.ToArray();
            Version = avatar.version;
        }

        /// <summary>资产原本的图片地址（不是 SDK 内容列表里的缩略图）。</summary>
        public string ImageUrl
        {
            get
            {
                if (_world != null)
                    return _world.imageUrl;
                if (_avatar != null)
                    return _avatar.imageUrl;
                return null;
            }
        }

        /// <summary>下载文件的基础名：资产原本的名字 + 蓝图ID。</summary>
        public string BuildBaseName()
        {
            string safeName = SanitizeFileName(Name);
            if (string.IsNullOrEmpty(safeName))
                safeName = "asset";
            return safeName + "_" + Id;
        }

        /// <summary>下载得到的文件名：基础名 + .vrcw / .vrca，例如 "My World_wrld_xxx.vrcw"。</summary>
        public string BuildOutputFileName()
        {
            return BuildBaseName() + ".vrc" + _fileEnding;
        }

        /// <summary>取消正在进行的下载（包括还没拿到下载地址的等待阶段）。</summary>
        public void CancelDownload()
        {
            _cancelled = true;
            ParallelDownloader downloader = _downloader;
            if (downloader != null)
                downloader.Abort();
        }

        /// <summary>
        /// 下载资产。<paramref name="percentage"/> 收到 0~100 的进度，
        /// <paramref name="onDone"/> 收到是否下载成功（取消也算失败）。
        /// </summary>
        public void DownloadAsset(string path, BuildPlatforms platform, Action<float> percentage = null, Action<bool> onDone = null)
        {
            // 每个资产放进一个以自身命名的独立文件夹，资产本体和图片都放在里面。
            string assetFolder = Path.Combine(path, BuildBaseName());
            if (!Directory.Exists(assetFolder))
                Directory.CreateDirectory(assetFolder);

            string outputFile = Path.Combine(assetFolder, BuildOutputFileName());
            _cancelled = false;

            if (_world != null)
            {
                string assetUrl = String.Empty;
                _world.Fetch(platform.GetPlatformString(), container =>
                {
                    if (_cancelled)
                    {
                        Notify(onDone, false);
                        return;
                    }

                    Json.JObject j = (Json.JObject) container.Data;
                    Json.JArray up = j["unityPackages"].Array;
                    bool didOne = false;
                    foreach (Json.Token token in up)
                    {
                        Json.JObject l = token.Object;
                        if (l["platform"].StringInstance == platform.GetPlatformString())
                        {
                            assetUrl = l["assetUrl"].StringInstance;
                            didOne = true;
                        }
                    }

                    if (!didOne && up.Count > 0)
                        assetUrl = up[0].Object["assetUrl"].StringInstance;

                    if (!string.IsNullOrEmpty(assetUrl))
                        StartDownload(assetUrl, outputFile, percentage, onDone);
                    else
                    {
                        Debug.LogError(LogPrefix + "找不到平台 " + platform.GetPlatformString() +
                                       " 对应的世界资源，可能该版本不存在。");
                        Notify(onDone, false);
                    }
                }, container =>
                {
                    Debug.LogError(LogPrefix + "获取世界资源地址失败。");
                    Notify(onDone, false);
                });
            }
            else if (_avatar != null)
            {
                ApiAvatar.UnityPackage package =
                    _avatar.unityPackages.FirstOrDefault(x => x.platform == platform.GetPlatformString());
                if (package == null || string.IsNullOrEmpty(package.assetUrl))
                {
                    Debug.LogError(LogPrefix + "找不到平台 " + platform.GetPlatformString() +
                                   " 对应的模型资源，可能该版本不存在。");
                    Notify(onDone, false);
                    return;
                }

                StartDownload(package.assetUrl, outputFile, percentage, onDone);
            }
            else
            {
                Debug.LogError(LogPrefix + "该资产没有可用的 VRChat API 数据。");
                Notify(onDone, false);
            }
        }

        private void StartDownload(string assetUrl, string outputFile, Action<float> percentage, Action<bool> onDone)
        {
            if (_cancelled)
            {
                Notify(onDone, false);
                return;
            }

            ParallelDownloader downloader = new ParallelDownloader(assetUrl, outputFile)
            {
                MaxConnections = DownloadSettings.Connections,
                MinimumChunkSize = DownloadSettings.MinimumChunkSizeBytes,
                RetriesPerPart = DownloadSettings.RetriesPerPart,
                ConfigureRequest = ConfigureVRCRequest
            };
            _downloader = downloader;

            downloader.Start(MapProgress(percentage, 0f, BundleProgressWeight), (success, error) =>
            {
                _downloader = null;

                if (_cancelled)
                {
                    DeleteFileIfExists(outputFile);
                    Notify(onDone, false);
                    return;
                }

                if (success)
                {
                    FileLocation = outputFile;
                    DownloadAssetImage(outputFile, percentage, onDone);
                    return;
                }

                Debug.LogWarning(LogPrefix + "多线程下载失败，改用 VRChat SDK 的单线程下载器重试：" + error);
                DownloadWithSdk(assetUrl, outputFile, percentage, onDone);
            });
        }

        /// <summary>
        /// 下载资产原本的图片，保存到和资产同一个目录、使用同一个基础名。
        /// 图片下载失败不影响资产本体，只是少一张图。
        /// </summary>
        private void DownloadAssetImage(string outputFile, Action<float> percentage, Action<bool> onDone)
        {
            string imageUrl = ImageUrl;
            if (string.IsNullOrEmpty(imageUrl))
            {
                Debug.LogWarning(LogPrefix + "该资产没有可下载的图片。");
                NotifySuccess(percentage, onDone);
                return;
            }

            string directory = Path.GetDirectoryName(outputFile) ?? String.Empty;
            // 以 ~ 结尾的临时文件 Unity 会忽略，不会触发导入。
            string tempFile = Path.Combine(directory, BuildBaseName() + ".image~");

            ParallelDownloader downloader = new ParallelDownloader(imageUrl, tempFile)
            {
                MaxConnections = 1,
                RetriesPerPart = DownloadSettings.RetriesPerPart,
                ConfigureRequest = ConfigureVRCRequest
            };
            _downloader = downloader;

            downloader.Start(MapProgress(percentage, BundleProgressWeight, 100f), (success, error) =>
            {
                _downloader = null;

                if (!success)
                {
                    DeleteFileIfExists(tempFile);
                    if (!_cancelled)
                        Debug.LogWarning(LogPrefix + "图片下载失败（资产本体已经下载成功）：" + error);
                    NotifySuccess(percentage, onDone);
                    return;
                }

                string imageFile = MoveImageIntoPlace(tempFile, directory);
                if (imageFile == null)
                    Debug.LogWarning(LogPrefix + "无法保存资产的图片。");
                else
                    ImageLocation = imageFile;

                NotifySuccess(percentage, onDone);
            });
        }

        private string MoveImageIntoPlace(string tempFile, string directory)
        {
            try
            {
                string target = Path.Combine(directory, BuildBaseName() + DetectImageExtension(tempFile));
                if (File.Exists(target))
                    File.Delete(target);
                File.Move(tempFile, target);
                return target;
            }
            catch (Exception exception)
            {
                Debug.LogError(LogPrefix + "保存资产图片失败：" + exception);
                DeleteFileIfExists(tempFile);
                return null;
            }
        }

        /// <summary>按文件头判断图片格式，避免把 JPG 存成 .png。</summary>
        private static string DetectImageExtension(string file)
        {
            byte[] header = new byte[12];
            int read;
            using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                read = stream.Read(header, 0, header.Length);
            }

            if (read >= 8 && header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E && header[3] == 0x47)
                return ".png";
            if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF)
                return ".jpg";
            if (read >= 6 && header[0] == 0x47 && header[1] == 0x49 && header[2] == 0x46)
                return ".gif";
            if (read >= 12 && header[0] == 0x52 && header[1] == 0x49 && header[2] == 0x46 && header[3] == 0x46 &&
                header[8] == 0x57 && header[9] == 0x45 && header[10] == 0x42 && header[11] == 0x50)
                return ".webp";
            if (read >= 2 && header[0] == 0x42 && header[1] == 0x4D)
                return ".bmp";
            return ".png";
        }

        /// <summary>把子下载的 0~100 进度映射到整体的某一段区间。</summary>
        private static Action<float> MapProgress(Action<float> percentage, float from, float to)
        {
            if (percentage == null)
                return null;
            return value => percentage(from + Mathf.Clamp01(value / 100f) * (to - from));
        }

        /// <summary>
        /// 附加与 VRChat SDK 自身请求完全一致的认证信息，让自建的分段请求和
        /// <see cref="ApiFile.DownloadFile"/> 表现一致。SDK 只是被调用，不做任何修改。
        /// </summary>
        private static void ConfigureVRCRequest(UnityWebRequest request)
        {
            API.CertVerifyUnityWebRequest(request);
            API.AuthenticateUnityWebRequest(request);
            API.PopulateUnityWebRequestHeaders(request);
        }

        /// <summary>
        /// 通过 VRChat SDK 做单连接下载，作为最后的兜底，保证下载行为和以前完全一致。
        /// </summary>
        private void DownloadWithSdk(string AssetURL, string outputFile, Action<float> percentage, Action<bool> onDone)
        {
            ApiFile.DownloadFile(AssetURL, bytes =>
            {
                if (_cancelled)
                {
                    DeleteFileIfExists(outputFile);
                    Notify(onDone, false);
                    return;
                }

                try
                {
                    using (FileStream fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush();
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogError(LogPrefix + "保存下载的资产失败：" + exception);
                    Notify(onDone, false);
                    return;
                }

                FileLocation = outputFile;
                FileBytes = bytes;
                DownloadAssetImage(outputFile, percentage, onDone);
            }, e =>
            {
                Debug.LogError(LogPrefix + "SDK 下载失败：" + e);
                Notify(onDone, false);
            }, (l, l1) =>
            {
                if (percentage != null && l1 > 0)
                    percentage.Invoke(Mathf.Clamp01((float)l / l1) * BundleProgressWeight);
            });
        }

        private static void Notify(Action<bool> onDone, bool success)
        {
            if (onDone != null)
                onDone.Invoke(success);
        }

        /// <summary>
        /// 下载成功时先把进度补满到 100%，再通知调用方，
        /// 避免图片被跳过或下载失败时进度条停在 95%。
        /// </summary>
        private static void NotifySuccess(Action<float> percentage, Action<bool> onDone)
        {
            if (percentage != null)
            {
                try
                {
                    percentage.Invoke(100f);
                }
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                }
            }

            Notify(onDone, true);
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
                Debug.LogWarning(LogPrefix + "无法删除未完成的文件：" + exception.Message);
            }
        }

        /// <summary>把资产名转换成合法的文件名片段。</summary>
        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return String.Empty;

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder builder = new StringBuilder(name.Length);
            foreach (char c in name)
            {
                bool isInvalid = false;
                for (int i = 0; i < invalid.Length; i++)
                {
                    if (invalid[i] == c)
                    {
                        isInvalid = true;
                        break;
                    }
                }
                builder.Append(isInvalid ? '_' : c);
            }

            string result = builder.ToString().Trim().Trim('.');
            if (result.Length > MaxNameLengthInFileName)
                result = result.Substring(0, MaxNameLengthInFileName).Trim();
            return result;
        }

        public override string ToString()
        {
            string g = Name + "\n" +
                       Id + "\n";
            return g;
        }
    }
}
#endif
