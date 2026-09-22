using UnityEditor;

namespace VRCDownloadTool.Editor
{
    /// <summary>
    /// 手动指定 AssetRipper 可执行文件的位置，保存在 EditorPrefs 中，
    /// 并在编辑器加载时同步给 <see cref="RipperHandler"/>。
    /// </summary>
    [InitializeOnLoad]
    public static class AssetRipperSettings
    {
        private const string CustomExecutableKey = "VRCDownloadTool.AssetRipperExecutable";

        static AssetRipperSettings()
        {
            Apply();
        }

        /// <summary>手动指定的可执行文件完整路径，空字符串表示使用自动下载的版本。</summary>
        public static string CustomExecutablePath
        {
            get { return EditorPrefs.GetString(CustomExecutableKey, string.Empty); }
            set
            {
                EditorPrefs.SetString(CustomExecutableKey, value ?? string.Empty);
                Apply();
            }
        }

        private static void Apply()
        {
            RipperHandler.CustomExecutablePath = CustomExecutablePath;
        }
    }
}
