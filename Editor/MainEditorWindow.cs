using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
#if VRC_SDK_VRCSDK3
using VRC.Core;
#endif

namespace VRCDownloadTool.Editor
{
    /// <summary>
    /// VRC下载工具的主窗口。
    /// </summary>
    public class MainEditorWindow : EditorWindow
    {
        private const string WindowTitle = "VRC下载工具";
        private const string LogPrefix = "[VRC下载工具] ";
        private const string FallbackOutputFolder = "Assets/VRCDownloadTools/Output/Bundles";
        private const string FacsUtilitiesUrl = "https://github.com/Rainsan86/com.facs01.utilities";

        private string _outputAssetBundles;

        private static MainEditorWindow Instance { get; set; }

        [MenuItem("VRC下载工具/主窗口")]
        private static void ShowWindow()
        {
            MainEditorWindow window = GetWindow<MainEditorWindow>();
            window.titleContent = new GUIContent(WindowTitle);
            window.minSize = new Vector2(380f, 460f);
            Instance = window;
        }

        private void OnEnable()
        {
            Instance = this;
        }

        private void OnDisable()
        {
            if (Instance == this)
                Instance = null;
        }

        private readonly RipperHandler _ripperHandler = new RipperHandler();

        // 资产下载状态
        private bool _isAssetDownloading;
        private bool _assetDownloadCancelled;
        private float _assetDownloadProgress;
        private string _assetDownloadName = String.Empty;
        private VRCAsset _downloadingAsset;

        // AssetRipper 下载状态
        private bool _isRipperDownloading;
        private bool _ripperDownloadCancelled;
        private float _ripperDownloadProgress;

        // 界面状态
        private static Vector2 _contentScroll;
        private static bool ShowDownloadSettings;

        private GUIStyle _titleStyle;
        private GUIStyle _thumbnailStyle;

        /// <summary>
        /// 资产的下载目录。根据本脚本自身的位置推导，插件文件夹改了名字也不会失效。
        /// </summary>
        private string OutputAssetBundles
        {
            get
            {
                if (!string.IsNullOrEmpty(_outputAssetBundles))
                    return _outputAssetBundles;

                try
                {
                    MonoScript script = MonoScript.FromScriptableObject(this);
                    string scriptPath = script != null ? AssetDatabase.GetAssetPath(script) : null;
                    if (!string.IsNullOrEmpty(scriptPath))
                    {
                        // scriptPath 形如 Assets/<插件目录>/Editor/MainEditorWindow.cs
                        string pluginRoot = Path.GetDirectoryName(Path.GetDirectoryName(scriptPath));
                        if (!string.IsNullOrEmpty(pluginRoot))
                        {
                            _outputAssetBundles = (pluginRoot + "/Output/Bundles").Replace('\\', '/');
                            return _outputAssetBundles;
                        }
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning(LogPrefix + "无法推导下载目录，改用默认路径：" + exception.Message);
                }

                _outputAssetBundles = FallbackOutputFolder;
                return _outputAssetBundles;
            }
        }

        private GUIStyle TitleStyle
        {
            get
            {
                if (_titleStyle == null)
                    _titleStyle = new GUIStyle(EditorStyles.boldLabel)
                    {
                        fontSize = 16,
                        alignment = TextAnchor.MiddleCenter
                    };
                return _titleStyle;
            }
        }

        private GUIStyle ThumbnailStyle
        {
            get
            {
                if (_thumbnailStyle == null)
                    _thumbnailStyle = new GUIStyle(GUI.skin.box)
                    {
                        alignment = TextAnchor.MiddleCenter
                    };
                return _thumbnailStyle;
            }
        }

        // ------------------------------------------------------------------ 界面绘制

        public void OnGUI()
        {
            _ripperHandler.SetWorkingDirectory();

            // 整个窗口共用一个滚动视图，窗口再小也不会被裁掉内容。
            _contentScroll = EditorGUILayout.BeginScrollView(_contentScroll);

            DrawHeader();

#if VRC_SDK_VRCSDK3
            if (!string.IsNullOrEmpty(SelectedTools.SelectedFile))
                DrawManageFileMenu();
            else if (!string.IsNullOrEmpty(SelectedTools.SelectedAvatarId))
                ShowAssetScreen(SelectedTools.SelectedAvatarId);
            else if (!string.IsNullOrEmpty(SelectedTools.SelectedWorldId))
                ShowAssetScreen(SelectedTools.SelectedWorldId);
            else
                DrawHome();
#else
            EditorGUILayout.HelpBox(
                "没有找到 VRC_SDK_VRCSDK3 宏定义，当前工程里是否已经安装 VRCSDK？\n" +
                "请先通过 VRChat Creator Companion 安装 VRChat SDK - Base 与 VRChat SDK - Avatars。",
                MessageType.Error);
#endif

            DrawDownloadSettings();

            EditorGUILayout.EndScrollView();
        }

        private void DrawHeader()
        {
            EditorGUILayout.Space(6f);
            GUILayout.Label(WindowTitle, TitleStyle);
            GUILayout.Label("找回并下载你自己上传的 VRChat 模型与世界资源", EditorStyles.centeredGreyMiniLabel);
            EditorGUILayout.Space(6f);
            DrawSeparator();
            EditorGUILayout.Space(6f);
        }

        private static void DrawSeparator()
        {
            Rect rect = EditorGUILayout.GetControlRect(false, 1f);
            EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.4f));
        }

#if VRC_SDK_VRCSDK3
        private void DrawHome()
        {
            if (!APIUser.IsLoggedIn)
            {
                EditorGUILayout.HelpBox("请先在上方的 VRChat SDK 控制面板中登录你的账号，登录后才能下载资产。",
                    MessageType.Warning);
            }
            else if (!ReflectingTools.DidFetchContent())
            {
                EditorGUILayout.HelpBox(
                    "还没有获取到你的内容列表。\n" +
                    "请打开 VRChat SDK 控制面板，进入 Content Manager 标签页，等待内容列表加载完成。",
                    MessageType.Info);
            }
            else if (SelectedTools.SelectedAssetType == VRCAssetType.Unknown)
            {
                GUILayout.Label("请选择要下载的资产类型", EditorStyles.boldLabel);
                EditorGUILayout.Space(2f);
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("模型 Avatar", GUILayout.Height(28f)))
                    SelectedTools.SelectedAssetType = VRCAssetType.Avatar;
                if (GUILayout.Button("世界 World", GUILayout.Height(28f)))
                    SelectedTools.SelectedAssetType = VRCAssetType.World;
                EditorGUILayout.EndHorizontal();
            }
            else
            {
                ShowList();
            }

            EditorGUILayout.Space(10f);
            DrawManageAssetBundles();
            DrawAssetRipperSection();
            DrawRecommendedTools();
        }

        private void ShowList()
        {
            string target = SelectedTools.SelectedAssetType == VRCAssetType.World ? "世界" : "模型";

            // 返回按钮放在最上面：资产多的时候不用一路拉到底。
            EditorGUILayout.BeginHorizontal();
            bool goBack = GUILayout.Button("← 返回", GUILayout.Width(80f), GUILayout.Height(24f));
            GUILayout.Label("请选择要下载的" + target, EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4f);

            if (goBack)
            {
                _contentScroll = Vector2.zero;
                SelectedTools.SelectedAssetType = VRCAssetType.Unknown;
                return;
            }

            int shown = 0;
            foreach (KeyValuePair<string, Texture2D> asset in VRCSdkControlPanel.ImageCache)
            {
                VRCAssetType assetType = SelectedTools.GetAssetTypeFromId(asset.Key);
                if (assetType != SelectedTools.SelectedAssetType)
                    continue;

                VRCAsset vrcAsset = ReflectingTools.GetDynamicAsset(asset.Key, assetType);
                if (vrcAsset == null)
                    continue;

                shown++;
                EditorGUILayout.BeginHorizontal();
                GUILayout.Box(vrcAsset.Texture, ThumbnailStyle, GUILayout.Width(64f), GUILayout.Height(64f));
                EditorGUILayout.BeginVertical();
                GUILayout.Label(vrcAsset.Name, EditorStyles.wordWrappedLabel);
                GUILayout.Label(vrcAsset.Id, EditorStyles.miniLabel);
                EditorGUILayout.EndVertical();
                if (GUILayout.Button("选择", GUILayout.Width(60f), GUILayout.Height(26f)))
                    SelectAsset(assetType, vrcAsset.Id);
                EditorGUILayout.EndHorizontal();
                EditorGUILayout.Space(4f);
            }

            if (shown == 0)
                EditorGUILayout.HelpBox("没有找到可下载的" + target + "。", MessageType.Info);
        }

        private static void SelectAsset(VRCAssetType assetType, string id)
        {
            if (assetType == VRCAssetType.World)
                SelectedTools.SelectedWorldId = id;
            else
                SelectedTools.SelectedAvatarId = id;
        }

        private static void ClearAssetSelection()
        {
            SelectedTools.SelectedAvatarId = String.Empty;
            SelectedTools.SelectedWorldId = String.Empty;
        }

        private void ShowAssetScreen(string id)
        {
            if (!VRCSdkControlPanel.ImageCache.ContainsKey(id))
            {
                ClearAssetSelection();
                return;
            }

            VRCAsset vrcAsset = ReflectingTools.GetDynamicAsset(id, SelectedTools.SelectedAssetType);
            if (vrcAsset == null)
            {
                EditorGUILayout.HelpBox("找不到该资产的 VRChat API 数据，请回到 VRChat SDK 控制面板重新获取内容。",
                    MessageType.Warning);
                if (GUILayout.Button("← 返回资产列表", GUILayout.Width(130f), GUILayout.Height(24f)))
                    ClearAssetSelection();
                return;
            }

            // 返回按钮放在最上面。
            EditorGUILayout.BeginHorizontal();
            bool goBack = false;
            using (new EditorGUI.DisabledScope(_isAssetDownloading))
            {
                goBack = GUILayout.Button("← 返回资产列表", GUILayout.Width(130f), GUILayout.Height(24f));
            }
            GUILayout.Label("已选择的资产", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4f);

            if (goBack)
            {
                ClearAssetSelection();
                return;
            }

            EditorGUILayout.BeginHorizontal();
            GUILayout.Box(vrcAsset.Texture, ThumbnailStyle, GUILayout.Width(96f), GUILayout.Height(96f));
            EditorGUILayout.BeginVertical();
            GUILayout.Label("名称：" + vrcAsset.Name, EditorStyles.wordWrappedLabel);
            GUILayout.Label("蓝图ID：" + vrcAsset.Id, EditorStyles.wordWrappedMiniLabel);
            GUILayout.Label("版本：" + vrcAsset.Version, EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.EndVertical();
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(8f);

            if (vrcAsset.SupportedPlatforms.Length == 0)
            {
                EditorGUILayout.HelpBox("该资产没有可下载的平台版本。", MessageType.Warning);
            }
            else
            {
                GUILayout.Label("可下载的平台版本", EditorStyles.boldLabel);
                using (new EditorGUI.DisabledScope(_isAssetDownloading))
                {
                    foreach (BuildPlatforms platform in vrcAsset.SupportedPlatforms)
                    {
                        if (GUILayout.Button("下载 " + platform.GetDisplayName(), GUILayout.Height(26f)))
                            BeginAssetDownload(vrcAsset, platform);
                    }
                }
                EditorGUILayout.HelpBox("下载资产时会一并保存资产原本的图片，文件名和资产相同。", MessageType.None);
            }

            if (_isAssetDownloading)
            {
                EditorGUILayout.Space(8f);
                GUILayout.Label("正在下载：" + _assetDownloadName, EditorStyles.boldLabel);
                Rect progressRect = EditorGUILayout.GetControlRect(false, 18f);
                EditorGUI.ProgressBar(progressRect, _assetDownloadProgress / 100f,
                    Mathf.RoundToInt(_assetDownloadProgress) + "%");
                if (GUILayout.Button("取消下载", GUILayout.Height(24f)))
                    CancelAssetDownload();
                Repaint();
            }
        }

        private void BeginAssetDownload(VRCAsset vrcAsset, BuildPlatforms platform)
        {
            if (_isAssetDownloading)
                return;

            _isAssetDownloading = true;
            _assetDownloadCancelled = false;
            _assetDownloadProgress = 0f;
            _assetDownloadName = vrcAsset.Name;
            _downloadingAsset = vrcAsset;

            vrcAsset.DownloadAsset(OutputAssetBundles, platform, percent =>
            {
                _assetDownloadProgress = percent;
                if (Instance != null)
                    Instance.Repaint();
            }, success =>
            {
                bool cancelled = _assetDownloadCancelled;
                _assetDownloadCancelled = false;
                _downloadingAsset = null;

                if (cancelled)
                {
                    ResetAssetDownloadState();
                    Debug.Log(LogPrefix + "已取消下载：" + vrcAsset.Name);
                    return;
                }

                if (!success)
                {
                    ResetAssetDownloadState();
                    EditorUtility.DisplayDialog(WindowTitle, "下载失败，请查看 Console 面板了解详情。", "确定");
                    return;
                }

                // 先让进度条停在 100% 并请求重绘，下一帧再收尾，这样 100% 会真的显示出来。
                _assetDownloadProgress = 100f;
                if (Instance != null)
                    Instance.Repaint();

                EditorApplication.delayCall += () =>
                {
                    ResetAssetDownloadState();
                    ClearAssetSelection();
                    AssetDatabase.Refresh();
                    EditorUtility.DisplayDialog(WindowTitle,
                        "下载完成！\n\n文件：" + vrcAsset.BuildOutputFileName(), "好的");
                    if (Instance != null)
                        Instance.Repaint();
                };
            });
        }

        private void CancelAssetDownload()
        {
            VRCAsset asset = _downloadingAsset;
            if (asset == null)
                return;

            _assetDownloadCancelled = true;
            asset.CancelDownload();
        }

        private void ResetAssetDownloadState()
        {
            _isAssetDownloading = false;
            _assetDownloadProgress = 0f;
            _assetDownloadName = String.Empty;
            _downloadingAsset = null;

            if (Instance != null)
                Instance.Repaint();
        }

        private void DrawManageAssetBundles()
        {
            DrawSeparator();
            EditorGUILayout.Space(6f);
            GUILayout.Label("已下载的资产", EditorStyles.boldLabel);

            if (!Directory.Exists(OutputAssetBundles))
            {
                EditorGUILayout.HelpBox("还没有下载任何资产。", MessageType.Info);
                return;
            }

            string[] files = Directory.GetFiles(OutputAssetBundles, "*", SearchOption.AllDirectories)
                .Where(file => SelectedTools.GetAssetTypeFromFileType(file) != VRCAssetType.Unknown)
                .ToArray();

            if (files.Length == 0)
            {
                EditorGUILayout.HelpBox("还没有下载任何资产。", MessageType.Info);
                return;
            }

            foreach (string file in files)
                if (GUILayout.Button(new GUIContent(Path.GetFileName(file), file), GUILayout.Height(22f)))
                    SelectedTools.SelectedFile = file;
        }

        private void DrawManageFileMenu()
        {
            string file = SelectedTools.SelectedFile;
            VRCAssetType fileType = SelectedTools.GetAssetTypeFromFileType(file);

            // 返回按钮放在最上面。
            EditorGUILayout.BeginHorizontal();
            bool goBack = false;
            using (new EditorGUI.DisabledScope(_ripperHandler.IsWorking))
            {
                goBack = GUILayout.Button("← 返回", GUILayout.Width(80f), GUILayout.Height(24f));
            }
            GUILayout.Label(fileType == VRCAssetType.World ? "世界资源" : "模型资源", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4f);

            if (goBack)
            {
                SelectedTools.SelectedFile = String.Empty;
                return;
            }

            GUILayout.Label(Path.GetFileName(file), EditorStyles.wordWrappedLabel);
            EditorGUILayout.Space(6f);

            if (fileType == VRCAssetType.World && !EditorApplication.isPlaying)
                EditorGUILayout.HelpBox("世界资源需要进入播放模式（Play）后才能载入场景。", MessageType.Info);
            else if (GUILayout.Button("载入到当前场景", GUILayout.Height(26f)))
                AssetLoader.LoadAssetBundle(file);

            if (GUILayout.Button("在资源管理器中显示", GUILayout.Height(26f)))
                RevealInExplorer(file);

            string imageFile = FindCompanionImage(file);
            if (imageFile != null)
            {
                GUILayout.Label("配套图片：" + Path.GetFileName(imageFile), EditorStyles.miniLabel);
                if (GUILayout.Button("在资源管理器中显示图片", GUILayout.Height(24f)))
                    RevealInExplorer(imageFile);
            }

            if (GUILayout.Button("删除该资产", GUILayout.Height(26f)))
            {
                string message = "确定要删除下面这个文件吗？\n\n" + Path.GetFileName(file);
                if (imageFile != null)
                    message += "\n" + Path.GetFileName(imageFile);
                if (EditorUtility.DisplayDialog(WindowTitle, message, "删除", "取消"))
                {
                    DeleteDownloadedAsset(file);
                    SelectedTools.SelectedFile = String.Empty;
                    return;
                }
            }

            EditorGUILayout.Space(8f);
            DrawSeparator();
            EditorGUILayout.Space(6f);
            DrawRipSection(file);
        }

        private void DrawRipSection(string file)
        {
            GUILayout.Label("解包成 Unity 工程（AssetRipper）", EditorStyles.boldLabel);

            if (!_ripperHandler.isPresent)
            {
                EditorGUILayout.HelpBox("需要先在主界面下载 AssetRipper。", MessageType.Info);
                return;
            }

            if (_ripperHandler.IsWorking)
            {
                GUILayout.Label("正在解包，请稍候……", EditorStyles.miniLabel);
                Rect progressRect = EditorGUILayout.GetControlRect(false, 18f);
                EditorGUI.ProgressBar(progressRect, _ripperHandler.Progress,
                    Mathf.RoundToInt(_ripperHandler.Progress * 100f) + "%");
                Repaint();
                return;
            }

            if (GUILayout.Button("选择输出文件夹并解包", GUILayout.Height(26f)))
                BeginRip(file);

            EditorGUILayout.HelpBox(
                "解包会清空所选文件夹里的全部内容，请选择一个空文件夹，且不要选在当前 Unity 工程内。",
                MessageType.Info);
        }

        private void BeginRip(string file)
        {
            string path = EditorUtility.OpenFolderPanel("选择导出文件夹", "Assets", String.Empty);
            if (string.IsNullOrEmpty(path))
                return;

            int childLength = Directory.GetDirectories(path).Length + Directory.GetFiles(path).Length;
            if (childLength > 0)
            {
                if (!EditorUtility.DisplayDialog(WindowTitle,
                        "你选择的文件夹不是空的，AssetRipper 会删除该文件夹里的所有文件！\n\n确定要继续吗？",
                        "继续", "取消"))
                    return;
            }

            if (IOTools.IsChildDirectory(Application.dataPath, path))
            {
                if (!EditorUtility.DisplayDialog(WindowTitle,
                        "你选择的文件夹在当前 Unity 工程内，导出的资源会被放进当前工程，可能会引发问题。\n\n确定要继续吗？",
                        "继续", "取消"))
                    return;
            }

            _ripperHandler.Rip(Path.GetFullPath(file), path, () =>
            {
                if (Directory.Exists(path))
                {
                    EditorUtility.DisplayDialog(WindowTitle, "解包完成！", "好的");
                }
                else
                {
                    Debug.LogWarning(LogPrefix + "解包后目录不存在：" + path);
                    EditorUtility.DisplayDialog(WindowTitle, "解包失败，请查看 Console 面板了解详情。", "确定");
                }
            });
        }

        private void DrawAssetRipperSection()
        {
            DrawSeparator();
            EditorGUILayout.Space(6f);
            GUILayout.Label("AssetRipper 解包工具", EditorStyles.boldLabel);

            if (_ripperHandler.IsUsingCustomExecutable)
                EditorGUILayout.HelpBox("正在使用你手动指定的 AssetRipper。", MessageType.Info);
            else if (_ripperHandler.isPresent)
                EditorGUILayout.HelpBox("AssetRipper 已就绪，可以把下载到的资源解包成 Unity 工程。", MessageType.Info);
            else
                EditorGUILayout.HelpBox(
                    "AssetRipper 用于把下载到的 AssetBundle 解包成可以直接打开的 Unity 工程。\n" +
                    "如果你已经自己下载过 AssetRipper，可以直接手动指定它的位置，不必再下载一次。",
                    MessageType.Info);

            if (_isRipperDownloading)
            {
                Rect progressRect = EditorGUILayout.GetControlRect(false, 18f);
                EditorGUI.ProgressBar(progressRect, _ripperDownloadProgress / 100f,
                    "下载 AssetRipper " + Mathf.RoundToInt(_ripperDownloadProgress) + "%");
                if (GUILayout.Button("取消下载", GUILayout.Height(24f)))
                    CancelRipperDownload();
                Repaint();
                return;
            }

            string label = _ripperHandler.isPresent ? "重新下载并安装 AssetRipper" : "下载 AssetRipper";
            if (GUILayout.Button(label, GUILayout.Height(26f)))
                BeginRipperDownload();

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("手动选择 AssetRipper", GUILayout.Height(24f)))
                SelectAssetRipperManually();
            if (_ripperHandler.IsUsingCustomExecutable)
            {
                if (GUILayout.Button("改回自动下载的版本", GUILayout.Height(24f)))
                    AssetRipperSettings.CustomExecutablePath = String.Empty;
            }
            EditorGUILayout.EndHorizontal();

            if (_ripperHandler.IsUsingCustomExecutable)
                GUILayout.Label("当前使用：" + _ripperHandler.ExecutablePath, EditorStyles.wordWrappedMiniLabel);
            else
                GUILayout.Label("下载目录：" + _ripperHandler.WorkingDirectory, EditorStyles.wordWrappedMiniLabel);
        }

        private void DrawRecommendedTools()
        {
            DrawSeparator();
            EditorGUILayout.Space(6f);
            GUILayout.Label("推荐插件", EditorStyles.boldLabel);

            EditorGUILayout.HelpBox(
                "FACS Utilities：Avatar 工程文件修复插件，用来检查和修复 Avatar 的 Unity 工程。\n" +
                "把 Avatar 工程用 Unity 打开后，可以先用它过一遍。",
                MessageType.Info);

            if (GUILayout.Button("打开 FACS Utilities 的 GitHub 页面", GUILayout.Height(26f)))
                Application.OpenURL(FacsUtilitiesUrl);

            GUILayout.Label(FacsUtilitiesUrl, EditorStyles.wordWrappedMiniLabel);
        }

        private void SelectAssetRipperManually()
        {
            string startDirectory = String.Empty;
            string current = _ripperHandler.ExecutablePath;
            if (!string.IsNullOrEmpty(current))
            {
                string directory = Path.GetDirectoryName(current);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                    startDirectory = directory;
            }

            string picked = EditorUtility.OpenFilePanel("选择 AssetRipper 可执行文件", startDirectory, String.Empty);
            if (string.IsNullOrEmpty(picked))
                return;

            if (!File.Exists(picked))
            {
                EditorUtility.DisplayDialog(WindowTitle, "找不到这个文件：\n" + picked, "确定");
                return;
            }

            string fileName = Path.GetFileName(picked);
            if (!fileName.StartsWith("AssetRipper", StringComparison.OrdinalIgnoreCase))
            {
                if (!EditorUtility.DisplayDialog(WindowTitle,
                        "这个文件名看起来不是 AssetRipper：\n" + fileName + "\n\n仍然要使用它吗？", "使用", "取消"))
                    return;
            }

            AssetRipperSettings.CustomExecutablePath = picked;
        }

        private void BeginRipperDownload()
        {
            if (_isRipperDownloading)
                return;

            if (_ripperHandler.IsUsingCustomExecutable)
            {
                if (!EditorUtility.DisplayDialog(WindowTitle,
                        "你正在使用手动指定的 AssetRipper：\n" + _ripperHandler.ExecutablePath +
                        "\n\n下载新版只会影响自动下载目录：\n" + _ripperHandler.WorkingDirectory +
                        "\n\n确定要继续吗？", "继续", "取消"))
                    return;
            }
            else if (_ripperHandler.isPresent)
            {
                if (!EditorUtility.DisplayDialog(WindowTitle,
                        "重新安装会先删除现有的 AssetRipper 目录，确定要继续吗？", "继续", "取消"))
                    return;
            }

            _isRipperDownloading = true;
            _ripperDownloadCancelled = false;
            _ripperDownloadProgress = 0f;

            _ripperHandler.Download(success =>
            {
                bool cancelled = _ripperDownloadCancelled;
                _ripperDownloadCancelled = false;
                _isRipperDownloading = false;
                _ripperDownloadProgress = 0f;

                if (Instance != null)
                    Instance.Repaint();

                if (cancelled)
                {
                    Debug.Log(LogPrefix + "已取消下载 AssetRipper。");
                    return;
                }

                if (success)
                    EditorUtility.DisplayDialog(WindowTitle, "AssetRipper 下载并解压完成！", "好的");
                else
                    EditorUtility.DisplayDialog(WindowTitle, "AssetRipper 下载失败，请查看 Console 面板了解详情。", "确定");
            }, percent =>
            {
                _ripperDownloadProgress = percent;
                if (Instance != null)
                    Instance.Repaint();
            });
        }

        private void CancelRipperDownload()
        {
            _ripperDownloadCancelled = true;
            _ripperHandler.CancelDownload();
        }

        private static void RevealInExplorer(string file)
        {
            try
            {
                string fullPath = Path.GetFullPath(file);
                if (!File.Exists(fullPath))
                {
                    EditorUtility.DisplayDialog(WindowTitle, "文件不存在：\n" + fullPath, "确定");
                    return;
                }

                EditorUtility.RevealInFinder(fullPath);
            }
            catch (Exception exception)
            {
                Debug.LogError(LogPrefix + "无法在资源管理器中显示文件：" + exception);
            }
        }

        /// <summary>资产图片支持的扩展名。</summary>
        private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

        /// <summary>找到与资产同名的图片，没有则返回 null。</summary>
        private static string FindCompanionImage(string assetFile)
        {
            try
            {
                string baseName = Path.Combine(Path.GetDirectoryName(assetFile) ?? String.Empty,
                    Path.GetFileNameWithoutExtension(assetFile));
                foreach (string extension in ImageExtensions)
                {
                    string candidate = baseName + extension;
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning(LogPrefix + "查找配套图片失败：" + exception.Message);
            }
            return null;
        }

        private static void DeleteDownloadedAsset(string file)
        {
            try
            {
                string directory = Path.GetDirectoryName(file) ?? String.Empty;
                string baseName = Path.GetFileNameWithoutExtension(file);

                // 新布局：资产放在以自身命名的独立文件夹里，整个文件夹一起删掉。
                if (Path.GetFileName(directory).Equals(baseName, StringComparison.Ordinal))
                {
                    DeleteFolderWithMeta(directory);
                    AssetDatabase.Refresh();
                    return;
                }

                // 旧布局：直接放在下载根目录下。
                DeleteFileWithMeta(file);

                string imageBaseName = Path.Combine(directory, baseName);
                foreach (string extension in ImageExtensions)
                    DeleteFileWithMeta(imageBaseName + extension);

                AssetDatabase.Refresh();
            }
            catch (Exception exception)
            {
                Debug.LogError(LogPrefix + "删除资产失败：" + exception);
            }
        }

        private static void DeleteFolderWithMeta(string folder)
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, true);
            if (File.Exists(folder + ".meta"))
                File.Delete(folder + ".meta");
        }

        private static void DeleteFileWithMeta(string file)
        {
            if (File.Exists(file))
                File.Delete(file);
            if (File.Exists(file + ".meta"))
                File.Delete(file + ".meta");
        }
#endif

        private void DrawDownloadSettings()
        {
            EditorGUILayout.Space(10f);
            DrawSeparator();
            EditorGUILayout.Space(4f);

            ShowDownloadSettings = EditorGUILayout.Foldout(ShowDownloadSettings, "下载设置", true);
            if (!ShowDownloadSettings)
                return;

            EditorGUI.indentLevel++;

            int connections = EditorGUILayout.IntSlider("并发连接数", DownloadSettings.Connections, 1, 16);
            if (connections != DownloadSettings.Connections)
                DownloadSettings.Connections = connections;

            int chunkSize = EditorGUILayout.IntSlider("最小分块大小 (MB)", DownloadSettings.MinimumChunkSizeMB, 1, 64);
            if (chunkSize != DownloadSettings.MinimumChunkSizeMB)
                DownloadSettings.MinimumChunkSizeMB = chunkSize;

            int retries = EditorGUILayout.IntSlider("失败重试次数", DownloadSettings.RetriesPerPart, 0, 20);
            if (retries != DownloadSettings.RetriesPerPart)
                DownloadSettings.RetriesPerPart = retries;

            EditorGUI.indentLevel--;

            EditorGUILayout.HelpBox(
                "大文件会被拆成多个分段同时下载。体积小于「最小分块大小 × 2」的文件只用一个连接。\n" +
                "重试次数越大，越不容易回退到 SDK 的单线程下载。",
                MessageType.None);
        }
    }
}
