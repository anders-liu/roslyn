﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Execution
{
    /// <summary>
    /// This is collection of hierarchical checksum trees
    /// </summary>
    internal class SnapshotStorages
    {
        /// <summary>
        /// global asset is an asset which life time is same as host
        /// </summary>
        private readonly ConcurrentDictionary<object, Asset> _globalAssets;

        /// <summary>
        /// map from solution checksum to its hierarchical checksum tree root
        /// </summary>
        private readonly ConcurrentDictionary<ChecksumScope, Storage> _snapshots;

        public SnapshotStorages()
        {
            _globalAssets = new ConcurrentDictionary<object, Asset>(concurrencyLevel: 2, capacity: 10);
            _snapshots = new ConcurrentDictionary<ChecksumScope, Storage>(concurrencyLevel: 2, capacity: 10);

            // TODO: currently only red node we are holding in this tree is Solution. create SolutionState so
            //       that we don't hold onto any red nodes (such as Document/Project)
        }

        public void AddGlobalAsset(object value, Asset asset, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!_globalAssets.TryAdd(value, asset))
            {
                // there is existing one, make sure asset is same
                Contract.ThrowIfFalse(_globalAssets[value].Checksum == asset.Checksum);
            }
        }

        public Asset GetGlobalAsset(object value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Asset asset;
            _globalAssets.TryGetValue(value, out asset);

            return asset;
        }

        public void RemoveGlobalAsset(object value, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Asset asset;
            _globalAssets.TryRemove(value, out asset);
        }

        public SnapshotStorage CreateSnapshotStorage(Solution solution)
        {
            return new Storage(this, solution);
        }

        public ChecksumObject GetChecksumObject(Checksum checksum, CancellationToken cancellationToken)
        {
            // search snapshots we have
            foreach (var storage in _snapshots.Values)
            {
                var checksumObject = storage.TryGetChecksumObject(checksum, cancellationToken);
                if (checksumObject != null)
                {
                    return checksumObject;
                }
            }

            // search global assets
            foreach (var asset in _globalAssets.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (asset.Checksum == checksum)
                {
                    return asset;
                }
            }

            // as long as solution snapshot is pinned. it must exist in one of the storages.
            throw ExceptionUtilities.UnexpectedValue(checksum);
        }

        private ChecksumObjectCache TryGetChecksumObjectEntry(object key, string kind, CancellationToken cancellationToken)
        {
            foreach (var storage in _snapshots.Values)
            {
                var entry = storage.TryGetChecksumObjectEntry(key, kind, cancellationToken);
                if (entry != null)
                {
                    return entry;
                }
            }

            return null;
        }

        public void RegisterSnapshot(ChecksumScope snapshot, SnapshotStorage storage)
        {
            // duplicates are not allowed, there can be multiple snapshots to same solution, so no ref counting.
            Contract.ThrowIfFalse(_snapshots.TryAdd(snapshot, (Storage)storage));
        }

        public void UnregisterSnapshot(ChecksumScope snapshot)
        {
            // calling it multiple times for same snapshot is not allowed.
            Storage dummy;
            Contract.ThrowIfFalse(_snapshots.TryRemove(snapshot, out dummy));
        }

        /// <summary>
        /// Each hierarchical checksum object has 1 corresponding storage.
        /// 
        /// in storage, it will either have assets or storage for child hierarchical checksum objects.
        /// 
        /// think this as a node in syntax tree, asset as token in the tree.
        /// </summary>
        private sealed class Storage : SnapshotStorage
        {
            private readonly SnapshotStorages _owner;

            // some of data (checksum) in this cache can be moved into object itself if we decide to do so.
            // this is cache since we can always rebuild these.
            private readonly ConcurrentDictionary<object, ChecksumObjectCache> _cache;

            // additional assets that is not part of solution but added explicitly
            private ConcurrentDictionary<Checksum, Asset> _additionalAssets;

            public Storage(SnapshotStorages owner, Solution solution) :
                base(solution)
            {
                _owner = owner;
                _cache = new ConcurrentDictionary<object, ChecksumObjectCache>(concurrencyLevel: 2, capacity: 1);

                // TODO: specialize root storage vs all child storage
            }

            public override void AddAdditionalAsset(Asset asset, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                LazyInitialization.EnsureInitialized(ref _additionalAssets, () => new ConcurrentDictionary<Checksum, Asset>());

                _additionalAssets.TryAdd(asset.Checksum, asset);
            }

            public ChecksumObject TryGetChecksumObject(Checksum checksum, CancellationToken cancellationToken)
            {
                // search needed to be improved.
                ChecksumObject checksumObject;
                foreach (var entry in _cache.Values)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (entry.TryGetValue(checksum, out checksumObject))
                    {
                        // this storage has information for the checksum
                        return checksumObject;
                    }

                    var storage = entry.TryGetStorage();
                    if (storage == null)
                    {
                        // this entry doesn't have sub storage.
                        continue;
                    }

                    // ask its sub storages
                    checksumObject = storage.TryGetChecksumObject(checksum, cancellationToken);
                    if (checksumObject != null)
                    {
                        // found one
                        return checksumObject;
                    }
                }

                Asset asset = null;
                if (_additionalAssets?.TryGetValue(checksum, out asset) == true)
                {
                    return asset;
                }

                // this storage has no reference to the given checksum
                return null;
            }

            public ChecksumObjectCache TryGetChecksumObjectEntry(object key, string kind, CancellationToken cancellationToken)
            {
                // find snapshot storage that contains given key, kind tuple.
                ChecksumObjectCache self;
                ChecksumObject checksumObject;
                if (_cache.TryGetValue(key, out self) &&
                    self.TryGetValue(kind, out checksumObject))
                {
                    // this storage owns it
                    return self;
                }

                foreach (var entry in _cache.Values)
                {
                    var storage = entry.TryGetStorage();
                    if (storage == null)
                    {
                        // this entry doesn't have sub storage.
                        continue;
                    }

                    // ask its sub storages
                    var subEntry = storage.TryGetChecksumObjectEntry(key, kind, cancellationToken);
                    if (subEntry != null)
                    {
                        // found one
                        return subEntry;
                    }
                }

                // this storage has no reference to the given checksum
                return null;
            }

            public override async Task<TChecksumObject> GetOrCreateHierarchicalChecksumObjectAsync<TKey, TValue, TChecksumObject>(
                TKey key, TValue value, string kind,
                Func<TValue, string, SnapshotBuilder, AssetBuilder, CancellationToken, Task<TChecksumObject>> valueGetterAsync, CancellationToken cancellationToken)
            {
                return await GetOrCreateChecksumObjectAsync(key, value, kind, (v, k, c) =>
                {
                    var snapshotBuilder = new SnapshotBuilder(GetStorage(key));
                    var assetBuilder = new AssetBuilder(this);

                    return valueGetterAsync(v, k, snapshotBuilder, assetBuilder, c);
                }, cancellationToken).ConfigureAwait(false);
            }

            public override Task<TAsset> GetOrCreateAssetAsync<TKey, TValue, TAsset>(
                TKey key, TValue value, string kind,
                Func<TValue, string, CancellationToken, Task<TAsset>> valueGetterAsync, CancellationToken cancellationToken)
            {
                return GetOrCreateChecksumObjectAsync(key, value, kind, valueGetterAsync, cancellationToken);
            }

            private async Task<TChecksumObject> GetOrCreateChecksumObjectAsync<TKey, TValue, TChecksumObject>(
                TKey key, TValue value, string kind,
                Func<TValue, string, CancellationToken, Task<TChecksumObject>> valueGetterAsync, CancellationToken cancellationToken)
                where TKey : class where TChecksumObject : ChecksumObject
            {
                Contract.ThrowIfNull(key);

                // ask myself
                ChecksumObject checksumObject;
                var entry = TryGetChecksumObjectEntry(key, kind, cancellationToken);
                if (entry != null && entry.TryGetValue(kind, out checksumObject))
                {
                    return (TChecksumObject)SaveAndReturn(key, checksumObject, entry);
                }

                // ask owner
                entry = _owner.TryGetChecksumObjectEntry(key, kind, cancellationToken);
                if (entry == null)
                {
                    // owner doesn't have it, create one.
                    checksumObject = await valueGetterAsync(value, kind, cancellationToken).ConfigureAwait(false);
                }
                else if (!entry.TryGetValue(kind, out checksumObject))
                {
                    // owner doesn't have this particular kind, create one.
                    checksumObject = await valueGetterAsync(value, kind, cancellationToken).ConfigureAwait(false);
                }

                // record local copy (reference) and return it.
                // REVIEW: we can go ref count route rather than this (local copy). but then we need to make sure there is no leak.
                //         for now, we go local copy route since overhead is small (just duplicated reference pointer), but reduce complexity a lot.
                return (TChecksumObject)SaveAndReturn(key, checksumObject, entry);
            }

            private ChecksumObject SaveAndReturn(object key, ChecksumObject checksumObject, ChecksumObjectCache entry = null)
            {
                // create new entry if it is not already given
                entry = _cache.GetOrAdd(key, _ => entry ?? new ChecksumObjectCache(checksumObject));
                return entry.Add(checksumObject);
            }

            private SnapshotStorage GetStorage<TKey>(TKey key)
            {
                var entry = _cache.GetOrAdd(key, _ => new ChecksumObjectCache());
                return entry.GetOrCreateStorage(_owner, Solution);
            }

            internal void TestOnly_ClearCache()
            {
                _cache.Clear();
            }
        }

        /// <summary>
        /// kind to actual checksum object collection
        /// 
        /// since our hierarchical checksum tree should only hold onto green node. 
        /// different kind of checksum object might share same green node such as document state
        /// for SourceText and DocumentInfo. so we have this nested cache type
        /// </summary>
        private class ChecksumObjectCache
        {
            private ChecksumObject _checksumObject;

            private Storage _lazyStorage;
            private ConcurrentDictionary<string, ChecksumObject> _lazyKindToChecksumObjectMap;
            private ConcurrentDictionary<Checksum, ChecksumObject> _lazyChecksumToChecksumObjectMap;

            public ChecksumObjectCache()
            {
            }

            public ChecksumObjectCache(ChecksumObject checksumObject)
            {
                _checksumObject = checksumObject;
            }

            public ChecksumObject Add(ChecksumObject checksumObject)
            {
                Interlocked.CompareExchange(ref _checksumObject, checksumObject, null);

                // optimization to not create map. in whole solution level, there is
                // many case where green node is unique per checksum object such as metadata reference
                // or p2p reference.
                if (_checksumObject.Kind == checksumObject.Kind)
                {
                    // we already have one
                    Contract.Requires(_checksumObject.Checksum.Equals(checksumObject.Checksum));
                    return _checksumObject;
                }

                // more expansive case. create map to save checksum object per kind
                EnsureLazyMap();

                _lazyChecksumToChecksumObjectMap.TryAdd(checksumObject.Checksum, checksumObject);

                if (_lazyKindToChecksumObjectMap.TryAdd(checksumObject.Kind, checksumObject))
                {
                    // just added new one
                    return checksumObject;
                }

                // there is existing one.
                return _lazyKindToChecksumObjectMap[checksumObject.Kind];
            }

            public bool TryGetValue(string kind, out ChecksumObject checksumObject)
            {
                if (_checksumObject?.Kind == kind)
                {
                    checksumObject = _checksumObject;
                    return true;
                }

                if (_lazyKindToChecksumObjectMap != null)
                {
                    return _lazyKindToChecksumObjectMap.TryGetValue(kind, out checksumObject);
                }

                checksumObject = null;
                return false;
            }

            public bool TryGetValue(Checksum checksum, out ChecksumObject checksumObject)
            {
                if (_checksumObject?.Checksum == checksum)
                {
                    checksumObject = _checksumObject;
                    return true;
                }

                if (_lazyChecksumToChecksumObjectMap != null)
                {
                    return _lazyChecksumToChecksumObjectMap.TryGetValue(checksum, out checksumObject);
                }

                checksumObject = null;
                return false;
            }

            public Storage TryGetStorage()
            {
                return _lazyStorage;
            }

            public Storage GetOrCreateStorage(SnapshotStorages owner, Solution solution)
            {
                if (_lazyStorage != null)
                {
                    return _lazyStorage;
                }

                Interlocked.CompareExchange(ref _lazyStorage, new Storage(owner, solution), null);
                return _lazyStorage;
            }

            private void EnsureLazyMap()
            {
                if (_lazyKindToChecksumObjectMap == null)
                {
                    // we have multiple entries. create lazy map
                    Interlocked.CompareExchange(ref _lazyKindToChecksumObjectMap, new ConcurrentDictionary<string, ChecksumObject>(concurrencyLevel: 2, capacity: 1), null);
                }

                if (_lazyChecksumToChecksumObjectMap == null)
                {
                    // we have multiple entries. create lazy map
                    Interlocked.CompareExchange(ref _lazyChecksumToChecksumObjectMap, new ConcurrentDictionary<Checksum, ChecksumObject>(concurrencyLevel: 2, capacity: 1), null);
                }
            }
        }
    }

    /// <summary>
    /// Base type for storage. we currently have 2 implementation.
    /// 
    /// one that used for hierarchical tree, one that is used to create one off asset
    /// from asset builder such as additional or global asset which is not part of
    /// hierarchical checksum tree
    /// </summary>
    internal abstract class SnapshotStorage
    {
        public readonly Solution Solution;
        public readonly Serializer Serializer;

        protected SnapshotStorage(Solution solution)
        {
            Solution = solution;
            Serializer = new Serializer(solution.Workspace.Services);
        }

        public abstract void AddAdditionalAsset(Asset asset, CancellationToken cancellationToken);

        public abstract Task<TChecksumObject> GetOrCreateHierarchicalChecksumObjectAsync<TKey, TValue, TChecksumObject>(
            TKey key, TValue value, string kind,
            Func<TValue, string, SnapshotBuilder, AssetBuilder, CancellationToken, Task<TChecksumObject>> valueGetterAsync,
            CancellationToken cancellationToken)
            where TKey : class
            where TChecksumObject : HierarchicalChecksumObject;


        public abstract Task<TAsset> GetOrCreateAssetAsync<TKey, TValue, TAsset>(
            TKey key, TValue value, string kind,
            Func<TValue, string, CancellationToken, Task<TAsset>> valueGetterAsync,
            CancellationToken cancellationToken)
            where TKey : class
            where TAsset : Asset;
    }
}
