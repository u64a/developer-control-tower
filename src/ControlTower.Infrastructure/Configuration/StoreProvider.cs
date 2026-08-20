using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;

namespace ControlTower.Infrastructure.Configuration
{
    public sealed class StoreProvider : IStoreProvider
    {
        private readonly IReadOnlyList<RepoStore> _stores;

        public StoreProvider(IEnumerable<RepoStore> stores)
        {
            _stores = (stores ?? Array.Empty<RepoStore>()).ToList();
        }

        public IReadOnlyList<RepoStore> GetStores()
        {
            return _stores;
        }

        public RepoStore GetStore(string storeId)
        {
            if (string.IsNullOrWhiteSpace(storeId))
            {
                return null;
            }

            return _stores.FirstOrDefault(
                s => string.Equals(s.Id, storeId, StringComparison.OrdinalIgnoreCase));
        }

        public string ResolveProjectPath(string storeId, string projectId, string folder)
        {
            var store = GetStore(storeId);
            if (store == null || string.IsNullOrWhiteSpace(store.Root))
            {
                return string.Empty;
            }

            var folderName = string.IsNullOrWhiteSpace(folder) ? projectId : folder;
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return store.Root;
            }

            if (store.IsSsh)
            {
                // Return user@host:root/folder for SSH stores
                var remotePath = store.Root.TrimEnd('/', '\\') + "/" + folderName;
                var userPrefix = string.IsNullOrWhiteSpace(store.User) ? "" : store.User + "@";
                return userPrefix + store.Host + ":" + remotePath;
            }

            return Path.GetFullPath(Path.Combine(store.Root, folderName));
        }
    }
}
