#if VRC_SDK_VRCSDK3
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using VRC.Core;

namespace VRCDownloadTool.Editor
{
    public class ReflectingTools
    {
        private static readonly Dictionary<string, VRCAsset> cachedAssets = new Dictionary<string, VRCAsset>();

        private static List<ApiWorld> GetUploadedWorlds()
        {
            return (List<ApiWorld>) Convert.ChangeType(
                typeof(VRCSdkControlPanel).GetField("uploadedWorlds", BindingFlags.Static | BindingFlags.NonPublic)?
                    .GetValue(null), typeof(List<ApiWorld>)) ?? new List<ApiWorld>();
        }

        private static List<ApiAvatar> GetUploadedAvatars()
        {
            return (List<ApiAvatar>) Convert.ChangeType(
                typeof(VRCSdkControlPanel).GetField("uploadedAvatars", BindingFlags.Static | BindingFlags.NonPublic)?
                    .GetValue(null), typeof(List<ApiAvatar>)) ?? new List<ApiAvatar>();
        }

        private static ApiWorld GetApiWorldFromCache(string id)
        {
            foreach (ApiWorld apiWorld in GetUploadedWorlds())
                if (apiWorld.id == id)
                    return apiWorld;
            return null;
        }

        private static ApiAvatar GetApiAvatarFromCache(string id)
        {
            foreach (ApiAvatar apiAvatar in GetUploadedAvatars())
                if (apiAvatar.id == id)
                    return apiAvatar;
            return null;
        }

        /// <summary>
        /// 取得资产的包装对象。VRChat SDK 缓存里找不到对应数据时返回 null，
        /// 调用方需要自行判空（否则会抛出异常）。
        /// </summary>
        public static VRCAsset GetDynamicAsset(string id, VRCAssetType assetType)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            VRCAsset cached;
            if (cachedAssets.TryGetValue(id, out cached))
                return cached;

            VRCAsset asset = null;
            switch (assetType)
            {
                case VRCAssetType.Avatar:
                    ApiAvatar avatar = GetApiAvatarFromCache(id);
                    if (avatar != null)
                        asset = new VRCAsset(avatar);
                    break;
                case VRCAssetType.World:
                    ApiWorld world = GetApiWorldFromCache(id);
                    if (world != null)
                        asset = new VRCAsset(world);
                    break;
            }

            if (asset == null)
                return null;

            cachedAssets[id] = asset;
            return asset;
        }

        public static Texture2D GetApiModelTextureFromCache(string id)
        {
            foreach (KeyValuePair<string,Texture2D> keyValuePair in VRCSdkControlPanel.ImageCache)
            {
                if (keyValuePair.Key == id)
                    return keyValuePair.Value;
            }
            return null;
        }

        public static bool DidFetchContent()
        {
            bool isAvatarEmpty = GetUploadedAvatars().Count <= 0;
            bool isWorldEmpty = GetUploadedWorlds().Count <= 0;
            return !isAvatarEmpty || !isWorldEmpty;
        }
    }
}
#endif
