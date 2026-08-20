using System.Collections.Generic;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;

namespace ControlTower.Infrastructure.Library
{
    /// <summary>
    /// Routes asset transfer to the local or SSH implementation based on the
    /// target path shape. SSH targets are recognised by the scp-style "host:path"
    /// pattern (a colon at index > 1, distinguishing from "C:\..." Windows paths).
    /// </summary>
    public sealed class AssetTransferRouter : IAssetTransferService
    {
        private readonly IAssetTransferService _local;
        private readonly IAssetTransferService _ssh;

        public AssetTransferRouter(IAssetTransferService local, IAssetTransferService ssh)
        {
            _local = local;
            _ssh = ssh;
        }

        public AssetPushPlan PreparePush(
            LibraryAsset asset,
            AssetType assetType,
            string libraryRoot,
            string targetProjectRoot,
            System.Collections.Generic.IEnumerable<string> includedFiles = null)
        {
            return Pick(targetProjectRoot).PreparePush(asset, assetType, libraryRoot, targetProjectRoot, includedFiles);
        }

        public AssetPushResult ApplyPush(AssetPushPlan plan)
        {
            // ApplyPush always writes from SourceAbsolutePath to TargetAbsolutePath.
            // For push, target is the project (local or SSH). For pull, target is
            // the library (always local — files were downloaded to temp during
            // PreparePull). Pick by ResolvedTargetPath, which describes the
            // destination of the actual write.
            return Pick(plan?.ResolvedTargetPath ?? plan?.TargetRoot).ApplyPush(plan);
        }

        public AssetPushPlan PreparePull(
            LibraryAsset asset,
            AssetType assetType,
            string libraryRoot,
            string sourceProjectRoot)
        {
            return Pick(sourceProjectRoot).PreparePull(asset, assetType, libraryRoot, sourceProjectRoot);
        }

        public static bool IsSshTarget(string targetRoot)
        {
            if (string.IsNullOrWhiteSpace(targetRoot)) return false;
            var colon = targetRoot.IndexOf(':');
            return colon > 1 && colon < targetRoot.Length - 1;
        }

        private IAssetTransferService Pick(string targetRoot)
            => IsSshTarget(targetRoot) ? _ssh : _local;
    }
}
