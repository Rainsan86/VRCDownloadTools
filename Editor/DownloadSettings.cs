using UnityEditor;
using UnityEngine;

namespace VRCDownloadTool.Editor
{
    /// <summary>
    /// 多线程下载的可调参数，保存在 EditorPrefs 中，并同步给
    /// <see cref="ParallelDownloader"/>，这样 AssetRipper 的下载也会遵守同样的设置。
    /// </summary>
    [InitializeOnLoad]
    public static class DownloadSettings
    {
        private const string ConnectionsKey = "VRCDownloadTool.Connections";
        private const string MinimumChunkSizeKey = "VRCDownloadTool.MinimumChunkSizeMB";
        private const string RetriesKey = "VRCDownloadTool.RetriesPerPart";

        private const int ConnectionsMaximum = 16;
        private const int ChunkSizeMaximumMB = 128;
        private const int RetriesMaximum = 20;

        static DownloadSettings()
        {
            ApplyToDownloader();
        }

        /// <summary>大文件下载时使用的并发连接数。</summary>
        public static int Connections
        {
            get { return Mathf.Clamp(EditorPrefs.GetInt(ConnectionsKey, ParallelDownloader.DefaultMaxConnections), 1, ConnectionsMaximum); }
            set
            {
                EditorPrefs.SetInt(ConnectionsKey, Mathf.Clamp(value, 1, ConnectionsMaximum));
                ApplyToDownloader();
            }
        }

        /// <summary>单个连接最少负责多少数据，单位 MB。</summary>
        public static int MinimumChunkSizeMB
        {
            get { return Mathf.Clamp(EditorPrefs.GetInt(MinimumChunkSizeKey, 8), 1, ChunkSizeMaximumMB); }
            set
            {
                EditorPrefs.SetInt(MinimumChunkSizeKey, Mathf.Clamp(value, 1, ChunkSizeMaximumMB));
                ApplyToDownloader();
            }
        }

        public static long MinimumChunkSizeBytes
        {
            get { return (long)MinimumChunkSizeMB * 1024L * 1024L; }
        }

        /// <summary>单个分段失败后额外重试的次数，调大可以降低回退到 SDK 单线程下载的概率。</summary>
        public static int RetriesPerPart
        {
            get { return Mathf.Clamp(EditorPrefs.GetInt(RetriesKey, ParallelDownloader.DefaultRetriesPerPart), 0, RetriesMaximum); }
            set
            {
                EditorPrefs.SetInt(RetriesKey, Mathf.Clamp(value, 0, RetriesMaximum));
                ApplyToDownloader();
            }
        }

        private static void ApplyToDownloader()
        {
            ParallelDownloader.DefaultMaxConnections = Connections;
            ParallelDownloader.DefaultMinimumChunkSize = MinimumChunkSizeBytes;
            ParallelDownloader.DefaultRetriesPerPart = RetriesPerPart;
        }
    }
}
