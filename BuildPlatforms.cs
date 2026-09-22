namespace VRCDownloadTool
{
    public enum BuildPlatforms
    {
        StandaloneWindows,
        Android,
        iOS,
        Web
    }

    public static class BuildPlatformsExtensions
    {
        public static string GetPlatformString(this BuildPlatforms platforms)
        {
            switch (platforms)
            {
                case BuildPlatforms.Android:
                    return "android";
                case BuildPlatforms.iOS:
                    return "ios";
                case BuildPlatforms.Web:
                    return "web";
            }
            return "standalonewindows";
        }

        public static string GetPlatformString(this BuildPlatforms[] platforms)
        {
            string s = "";
            for (int i = 0; i < platforms.Length; i++)
            {
                BuildPlatforms platform = platforms[i];
                s += platform.GetPlatformString();
                if (i >= platforms.Length - 1) continue;
                s += ',';
            }
            return s;
        }

        /// <summary>界面上显示的平台名称。</summary>
        public static string GetDisplayName(this BuildPlatforms platforms)
        {
            switch (platforms)
            {
                case BuildPlatforms.Android:
                    return "Android 安卓版";
                case BuildPlatforms.iOS:
                    return "iOS 版";
                case BuildPlatforms.Web:
                    return "Web 网页版";
            }
            return "Windows 桌面版";
        }
    }
}
