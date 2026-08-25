using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using BepInEx;
using UnityEngine;

namespace Clan;

internal static partial class ClanEmoji
{
    private const int EmojiFileProtocolVersion = 3;
    private const int EmojiChunkBytes = 64 * 1024;
    private const int MaximumParallelEmojiChunks = 4;
    private const int MaximumEmojiRequestBytes = 512;
    private const int MaximumEmojiResponseBytes = EmojiChunkBytes + 512;
    private const int MaximumChunkRetries = 3;
    private const float ChunkResponseTimeoutSeconds = 5f;
    private const long EmojiCacheMaximumBytes = 384L * 1024 * 1024;
    private const long EmojiCacheTrimmedBytes = 320L * 1024 * 1024;
    private const long EmojiReloadDebounceTicks = TimeSpan.TicksPerMillisecond * 750;
    private const int MaximumReloadRetries = 3;
    private const float PeerRequestTokensPerSecond = 64f;
    private const float PeerRequestTokenCapacity = 32f;
    private const float PeerTransferBytesPerSecond = 0.8f * 1024 * 1024;
    private const float PeerTransferByteCapacity = MaximumParallelEmojiChunks * EmojiChunkBytes;
    private const float GlobalTransferBytesPerSecond = 3.2f * 1024 * 1024;
    private const float GlobalTransferByteCapacity = 16f * EmojiChunkBytes;
    private const int MaximumMediaSendQueueBytes = 4 * EmojiChunkBytes;
    private const float MalformedLogWindowSeconds = 10f;

    private static readonly string EmojiFileRequestRpc =
        $"{ClanPlugin.ModGUID}.media.file.request.v{EmojiFileProtocolVersion}";
    private static readonly string EmojiFileResponseRpc =
        $"{ClanPlugin.ModGUID}.media.file.response.v{EmojiFileProtocolVersion}";

    private static readonly Dictionary<ZRpc, PeerTransferBudget> EmojiPeerBudgets = new();
    private static readonly GlobalTransferBudget EmojiGlobalTransferBudget = new();

    private static ServerEmojiCatalog? _serverEmojiCatalog;
    private static ClientEmojiCatalog? _clientEmojiCatalog;
    private static FileSystemWatcher? _emojiFileWatcher;
    private static FileSystemWatcher? _emblemFileWatcher;
    private static long _emojiReloadRequestedAtTicks;
    private static int _emojiReloadFailures;
    private static int _emojiWatcherNeedsRestart;
    private static int _emojiWatcherRestartFailures;

    private static string EmojiCacheDirectory =>
        Path.Combine(Paths.CachePath, "Clan");

    private static bool HasEmojiServerCatalog => _serverEmojiCatalog != null;

    private static bool IsCurrentEmojiServerManifest(string manifest)
    {
        return _serverEmojiCatalog != null &&
               StringComparer.Ordinal.Equals(_serverEmojiCatalog.Manifest, manifest);
    }

    private static void InitEmojiSync()
    {
        try
        {
            Directory.CreateDirectory(EmojiDirectory);
            Directory.CreateDirectory(EmblemDirectory);
            Directory.CreateDirectory(EmojiCacheDirectory);
            DeletePartialEmojiCacheFiles();
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan media cache could not be initialized: {ex.Message}");
        }
    }

    private static void DisposeEmojiSync()
    {
        DisposeEmojiFileWatcher();
        _clientEmojiCatalog = null;
        _serverEmojiCatalog = null;
        EmojiPeerBudgets.Clear();
        EmojiGlobalTransferBudget.Reset();
        Interlocked.Exchange(ref _emojiReloadRequestedAtTicks, 0L);
        Interlocked.Exchange(ref _emojiWatcherNeedsRestart, 0);
        Interlocked.Exchange(ref _emojiWatcherRestartFailures, 0);
    }

    private static void ResetEmojiSyncSession(ZNet? session)
    {
        _clientEmojiCatalog = null;
        _serverEmojiCatalog = null;
        EmojiPeerBudgets.Clear();
        EmojiGlobalTransferBudget.Reset();
        Interlocked.Exchange(ref _emojiReloadRequestedAtTicks, 0L);
        Interlocked.Exchange(ref _emojiReloadFailures, 0);
        Interlocked.Exchange(ref _emojiWatcherNeedsRestart, 0);
        Interlocked.Exchange(ref _emojiWatcherRestartFailures, 0);
        DisposeEmojiFileWatcher();

        if (session?.IsServer() == true)
        {
            CreateEmojiFileWatcher();
        }
    }

    internal static void RegisterEmojiFileRpc(ZNet znet, ZNetPeer peer)
    {
        if (znet.IsServer())
        {
            peer.m_rpc.Register<ZPackage>(
                EmojiFileRequestRpc,
                (Action<ZRpc, ZPackage>)((rpc, package) =>
                    HandleEmojiFileRequest(peer, rpc, package)));
        }
        else
        {
            peer.m_rpc.Register<ZPackage>(
                EmojiFileResponseRpc,
                (Action<ZRpc, ZPackage>)HandleEmojiFileResponse);
        }
    }

    private static void PublishEmojiServerCatalog(
        string manifest,
        IReadOnlyList<ManifestRecord> records,
        IReadOnlyCollection<ServerEmojiBlob> blobs)
    {
        Dictionary<string, ServerEmojiBlob> byHash = new(StringComparer.Ordinal);
        int totalGifFrames = 0;
        foreach (ServerEmojiBlob blob in blobs)
        {
            if (blob.Data.Length != blob.Record.Length)
            {
                throw new InvalidDataException(
                    $"Media source '{blob.Record.Name}' changed while its catalog was being published.");
            }
            if ((blob.Record.Kind == EmojiFileKind.Gif &&
                 (blob.GifFrameCount <= 0 || blob.GifFrameCount > MaximumGifFrames)) ||
                (blob.Record.Kind == EmojiFileKind.Png && blob.GifFrameCount != 0))
            {
                throw new InvalidDataException(
                    $"Media source '{blob.Record.Name}' has invalid frame metadata.");
            }
            if (blob.Record.Kind == EmojiFileKind.Gif)
            {
                if (blob.GifFrameCount > MaximumTotalGifFrames - totalGifFrames)
                {
                    throw new InvalidDataException(
                        $"Clan media catalog exceeds its {MaximumTotalGifFrames} total GIF frame budget.");
                }
                totalGifFrames += blob.GifFrameCount;
            }

            if (byHash.TryGetValue(blob.Record.Hash, out ServerEmojiBlob existing))
            {
                if (existing.Record.Length != blob.Record.Length ||
                    !StringComparer.Ordinal.Equals(existing.Record.Extension, blob.Record.Extension) ||
                    existing.GifFrameCount != blob.GifFrameCount)
                {
                    throw new InvalidDataException("A media content hash maps to conflicting files.");
                }
                continue;
            }

            byHash.Add(blob.Record.Hash, blob);
        }

        _serverEmojiCatalog = new ServerEmojiCatalog(
            manifest,
            ComputeSha256(Encoding.UTF8.GetBytes(manifest)),
            records.ToArray(),
            byHash);
        EmojiPeerBudgets.Clear();
        EmojiGlobalTransferBudget.Reset();
    }

    private static void ClearEmojiServerCatalog()
    {
        _serverEmojiCatalog = null;
        EmojiPeerBudgets.Clear();
        EmojiGlobalTransferBudget.Reset();
    }

    private static void BeginEmojiClientCatalog(
        string manifest,
        IReadOnlyList<ManifestRecord> records)
    {
        _clientEmojiCatalog = new ClientEmojiCatalog(
            ZNet.instance,
            manifest,
            ComputeSha256(Encoding.UTF8.GetBytes(manifest)),
            records);
    }

    private static void CancelEmojiClientCatalog()
    {
        _clientEmojiCatalog = null;
    }

    private static bool IsEmojiClientCatalogReady(string manifest)
    {
        ClientEmojiCatalog? catalog = _clientEmojiCatalog;
        return catalog != null &&
               catalog.Ready &&
               ReferenceEquals(catalog.Session, ZNet.instance) &&
               StringComparer.Ordinal.Equals(catalog.Manifest, manifest);
    }

    private static void TickEmojiSync()
    {
        ClientEmojiCatalog? catalog = _clientEmojiCatalog;
        if (catalog == null || catalog.Ready)
        {
            return;
        }
        if (!ReferenceEquals(catalog.Session, ZNet.instance))
        {
            _clientEmojiCatalog = null;
            return;
        }

        if (catalog.Failed)
        {
            if (catalog.CatalogRetryCount == 0 &&
                Time.realtimeSinceStartup >= catalog.CatalogRetryAt)
            {
                catalog.CatalogRetryCount++;
                catalog.Failed = false;
                catalog.Download = null;
                catalog.NextRecordIndex = 0;
                catalog.CacheHits = 0;
                catalog.Downloads = 0;
                ClanPlugin.ClanLogger.LogInfo(
                    "Retrying the synchronized Clan media catalog after a transient failure.");
            }
            return;
        }

        ClientEmojiDownload? download = catalog.Download;
        if (download != null)
        {
            TickEmojiDownload(catalog, download);
            return;
        }

        if (catalog.NextRecordIndex >= catalog.Records.Count)
        {
            catalog.Ready = true;
            ClanPlugin.ClanLogger.LogInfo(
                $"Clan media catalog is ready: {catalog.CacheHits} cache hits, " +
                $"{catalog.Downloads} downloaded files.");
            return;
        }

        ManifestRecord record = catalog.Records[catalog.NextRecordIndex];
        if (TryReadSyncedEmojiFile(record, out _))
        {
            catalog.CacheHits++;
            catalog.NextRecordIndex++;
            return;
        }

        if (ZNet.instance?.IsServer() == true)
        {
            FailEmojiClientCatalog(
                catalog,
                $"The listen server catalog does not contain '{record.Name}'.");
            return;
        }

        catalog.Download = new ClientEmojiDownload(record);
    }

    private static void TickEmojiDownload(
        ClientEmojiCatalog catalog,
        ClientEmojiDownload download)
    {
        float now = Time.realtimeSinceStartup;
        if (download.Completed)
        {
            try
            {
                string hash = ComputeSha256(download.Buffer);
                if (!StringComparer.Ordinal.Equals(hash, download.Record.Hash))
                {
                    throw new InvalidDataException("Downloaded SHA-256 does not match the manifest.");
                }

                WriteEmojiCacheAtomically(download.Record, download.Buffer);
                catalog.Download = null;
                catalog.NextRecordIndex++;
                catalog.Downloads++;
                PruneEmojiCache(catalog.Records);
            }
            catch (Exception ex)
            {
                FailEmojiClientCatalog(
                    catalog,
                    $"Downloaded media file '{download.Record.Name}' could not be cached: {ex.Message}");
            }
            return;
        }

        for (int chunkIndex = 0; chunkIndex < download.ChunkCount; chunkIndex++)
        {
            if (download.IsResponseTimedOut(chunkIndex, now))
            {
                RetryEmojiChunk(
                    catalog,
                    download,
                    chunkIndex,
                    "response timed out",
                    0.25f);
                if (catalog.Failed)
                {
                    return;
                }
            }
        }

        ZRpc? serverRpc = ZNet.instance?.GetServerRPC();
        if (serverRpc == null || serverRpc.GetSocket()?.IsConnected() != true)
        {
            return;
        }

        while (download.InFlightCount < MaximumParallelEmojiChunks &&
               download.TryGetNextRequestChunk(now, out int chunkIndex))
        {
            int offset = chunkIndex * EmojiChunkBytes;
            int requestId = download.MarkRequested(
                chunkIndex,
                now + ChunkResponseTimeoutSeconds);
            ZPackage request = new();
            request.Write(EmojiFileProtocolVersion);
            request.Write(catalog.CatalogId);
            request.Write(download.Record.Hash);
            request.Write(offset);
            request.Write(requestId);

            try
            {
                serverRpc.Invoke(EmojiFileRequestRpc, request);
            }
            catch (Exception ex)
            {
                RetryEmojiChunk(
                    catalog,
                    download,
                    chunkIndex,
                    ex.Message,
                    0.25f);
                if (catalog.Failed)
                {
                    return;
                }
            }
        }
    }

    private static void HandleEmojiFileRequest(
        ZNetPeer peer,
        ZRpc rpc,
        ZPackage package)
    {
        try
        {
            if (ZNet.instance?.IsServer() != true ||
                !ReferenceEquals(peer.m_rpc, rpc) ||
                !peer.IsReady() ||
                package.Size() <= 0 ||
                package.Size() > MaximumEmojiRequestBytes)
            {
                return;
            }
            if (!ConsumeEmojiPeerRequest(rpc))
            {
                return;
            }

            package.SetPos(0);
            int version = package.ReadInt();
            string catalogId = package.ReadString();
            string hash = package.ReadString();
            int offset = package.ReadInt();
            int requestId = package.ReadInt();
            RequireEmojiPackageConsumed(package);

            if (version != EmojiFileProtocolVersion ||
                !IsSha256(catalogId) ||
                !IsSha256(hash) ||
                offset < 0 ||
                offset % EmojiChunkBytes != 0 ||
                requestId <= 0)
            {
                SendEmojiFileResponse(
                    rpc,
                    EmojiFileResponseStatus.Rejected,
                    catalogId,
                    hash,
                    0,
                    offset,
                    requestId,
                    null);
                return;
            }

            ServerEmojiCatalog? catalog = _serverEmojiCatalog;
            if (catalog == null ||
                !StringComparer.Ordinal.Equals(catalog.CatalogId, catalogId))
            {
                SendEmojiFileResponse(
                    rpc,
                    EmojiFileResponseStatus.StaleCatalog,
                    catalogId,
                    hash,
                    0,
                    offset,
                    requestId,
                    null);
                return;
            }

            if (!catalog.Files.TryGetValue(hash, out ServerEmojiBlob blob) ||
                offset >= blob.Data.Length)
            {
                SendEmojiFileResponse(
                    rpc,
                    EmojiFileResponseStatus.Unavailable,
                    catalogId,
                    hash,
                    0,
                    offset,
                    requestId,
                    null);
                return;
            }

            int length = Math.Min(EmojiChunkBytes, blob.Data.Length - offset);
            int sendQueueSize = rpc.GetSocket()?.GetSendQueueSize() ?? int.MaxValue;
            if (sendQueueSize > MaximumMediaSendQueueBytes - length ||
                !ConsumeEmojiTransferBytes(rpc, length))
            {
                SendEmojiFileResponse(
                    rpc,
                    EmojiFileResponseStatus.Busy,
                    catalogId,
                    hash,
                    blob.Data.Length,
                    offset,
                    requestId,
                    null);
                return;
            }

            byte[] chunk = new byte[length];
            Buffer.BlockCopy(blob.Data, offset, chunk, 0, length);
            SendEmojiFileResponse(
                rpc,
                EmojiFileResponseStatus.Data,
                catalogId,
                hash,
                blob.Data.Length,
                offset,
                requestId,
                chunk);
        }
        catch (Exception ex)
        {
            if (ShouldLogMalformedEmojiRequest(rpc))
            {
                ClanPlugin.ClanLogger.LogWarning(
                    $"Rejected malformed Clan media file request: {ex.Message}");
            }
        }
    }

    private static void HandleEmojiFileResponse(ZRpc rpc, ZPackage package)
    {
        try
        {
            if (!ReferenceEquals(rpc, ZNet.instance?.GetServerRPC()) ||
                package.Size() <= 0 ||
                package.Size() > MaximumEmojiResponseBytes)
            {
                return;
            }

            package.SetPos(0);
            int version = package.ReadInt();
            EmojiFileResponseStatus status = (EmojiFileResponseStatus)package.ReadByte();
            string catalogId = package.ReadString();
            string hash = package.ReadString();
            int totalLength = package.ReadInt();
            int offset = package.ReadInt();
            int requestId = package.ReadInt();

            if (version != EmojiFileProtocolVersion ||
                !Enum.IsDefined(typeof(EmojiFileResponseStatus), status) ||
                !IsSha256(catalogId) ||
                !IsSha256(hash) ||
                totalLength < 0 ||
                offset < 0 ||
                offset % EmojiChunkBytes != 0 ||
                requestId <= 0)
            {
                throw new InvalidDataException("Emoji response header is invalid.");
            }

            byte[]? chunk = null;
            if (status == EmojiFileResponseStatus.Data)
            {
                int chunkLength = package.ReadInt();
                int remaining = package.Size() - package.GetPos();
                if (chunkLength <= 0 ||
                    chunkLength > EmojiChunkBytes ||
                    remaining != chunkLength)
                {
                    throw new InvalidDataException("Emoji response chunk length is invalid.");
                }
                chunk = package.ReadByteArray(chunkLength);
            }
            RequireEmojiPackageConsumed(package);

            ClientEmojiCatalog? catalog = _clientEmojiCatalog;
            ClientEmojiDownload? download = catalog?.Download;
            if (catalog == null ||
                download == null ||
                catalog.Failed ||
                !ReferenceEquals(catalog.Session, ZNet.instance) ||
                !StringComparer.Ordinal.Equals(catalog.CatalogId, catalogId) ||
                !StringComparer.Ordinal.Equals(download.Record.Hash, hash) ||
                !download.TryGetChunkIndex(offset, out int chunkIndex) ||
                !download.HasIssuedRequest(chunkIndex, requestId))
            {
                return;
            }

            if (download.IsChunkReceived(chunkIndex))
            {
                return;
            }
            bool carriesTotalLength = status == EmojiFileResponseStatus.Data ||
                                      status == EmojiFileResponseStatus.Busy;
            if (carriesTotalLength
                    ? totalLength != download.Record.Length
                    : totalLength != 0)
            {
                throw new InvalidDataException("Emoji response length is invalid for its status.");
            }

            switch (status)
            {
                case EmojiFileResponseStatus.Data:
                    if (chunk == null ||
                        totalLength != download.Record.Length ||
                        chunk.Length != Math.Min(
                            EmojiChunkBytes,
                            download.Record.Length - offset))
                    {
                        throw new InvalidDataException("Emoji response does not match the manifest.");
                    }

                    Buffer.BlockCopy(
                        chunk,
                        0,
                        download.Buffer,
                        offset,
                        chunk.Length);
                    download.MarkReceived(chunkIndex, chunk.Length);
                    break;
                case EmojiFileResponseStatus.Busy:
                    if (download.IsCurrentRequest(chunkIndex, requestId))
                    {
                        download.DeferChunk(
                            chunkIndex,
                            Time.realtimeSinceStartup + 0.1f);
                    }
                    break;
                case EmojiFileResponseStatus.StaleCatalog:
                    if (download.IsCurrentRequest(chunkIndex, requestId))
                    {
                        FailEmojiClientCatalog(
                            catalog,
                            "The server replaced the media catalog during download.");
                    }
                    break;
                case EmojiFileResponseStatus.Unavailable:
                case EmojiFileResponseStatus.Rejected:
                    if (download.IsCurrentRequest(chunkIndex, requestId))
                    {
                        RetryEmojiChunk(
                            catalog,
                            download,
                            chunkIndex,
                            status.ToString(),
                            0.25f);
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            ClientEmojiCatalog? catalog = _clientEmojiCatalog;
            if (catalog != null)
            {
                FailEmojiClientCatalog(
                    catalog,
                    $"Malformed Clan media response was rejected: {ex.Message}");
            }
        }
    }

    private static void SendEmojiFileResponse(
        ZRpc rpc,
        EmojiFileResponseStatus status,
        string catalogId,
        string hash,
        int totalLength,
        int offset,
        int requestId,
        byte[]? chunk)
    {
        ZPackage response = new();
        response.Write(EmojiFileProtocolVersion);
        response.Write((byte)status);
        response.Write(catalogId);
        response.Write(hash);
        response.Write(totalLength);
        response.Write(offset);
        response.Write(requestId);
        if (status == EmojiFileResponseStatus.Data)
        {
            response.Write(chunk ?? throw new ArgumentNullException(nameof(chunk)));
        }
        rpc.Invoke(EmojiFileResponseRpc, response);
    }

    private static bool ConsumeEmojiPeerRequest(ZRpc rpc)
    {
        PeerTransferBudget budget = GetEmojiPeerBudget(rpc);
        return budget.TryConsumeRequest(Time.realtimeSinceStartup);
    }

    private static bool ConsumeEmojiTransferBytes(ZRpc rpc, int bytes)
    {
        if (bytes <= 0 || bytes > EmojiChunkBytes)
        {
            return false;
        }

        float now = Time.realtimeSinceStartup;
        PeerTransferBudget budget = GetEmojiPeerBudget(rpc);
        budget.Refill(now);
        EmojiGlobalTransferBudget.Refill(now);
        if (budget.ByteTokens < bytes ||
            EmojiGlobalTransferBudget.ByteTokens < bytes)
        {
            return false;
        }

        budget.ByteTokens -= bytes;
        EmojiGlobalTransferBudget.ByteTokens -= bytes;
        return true;
    }

    private static bool ShouldLogMalformedEmojiRequest(ZRpc rpc)
    {
        PeerTransferBudget budget = GetEmojiPeerBudget(rpc);
        return budget.TryConsumeMalformedLog(Time.realtimeSinceStartup);
    }

    private static PeerTransferBudget GetEmojiPeerBudget(ZRpc rpc)
    {
        float now = Time.realtimeSinceStartup;
        if (!EmojiPeerBudgets.TryGetValue(rpc, out PeerTransferBudget budget))
        {
            budget = new PeerTransferBudget(now);
            EmojiPeerBudgets[rpc] = budget;
        }
        return budget;
    }

    internal static void ForgetEmojiPeer(ZRpc? rpc)
    {
        if (rpc != null)
        {
            EmojiPeerBudgets.Remove(rpc);
        }
    }

    private static void RetryEmojiChunk(
        ClientEmojiCatalog catalog,
        ClientEmojiDownload download,
        int chunkIndex,
        string reason,
        float delay)
    {
        int retryCount = download.IncrementRetry(chunkIndex);
        if (retryCount > MaximumChunkRetries)
        {
            FailEmojiClientCatalog(
                catalog,
                $"Emoji '{download.Record.Name}' could not be downloaded: {reason}.");
            return;
        }

        download.DeferChunk(
            chunkIndex,
            Time.realtimeSinceStartup + delay * (1 << (retryCount - 1)));
    }

    private static void FailEmojiClientCatalog(ClientEmojiCatalog catalog, string message)
    {
        if (catalog.Failed)
        {
            return;
        }

        catalog.Failed = true;
        catalog.Download = null;
        if (catalog.CatalogRetryCount == 0)
        {
            catalog.CatalogRetryAt = Time.realtimeSinceStartup + 30f;
        }
        ClanPlugin.ClanLogger.LogWarning(
            message + " The previous Clan media set remains active." +
            (catalog.CatalogRetryCount == 0
                ? " The catalog will be retried once."
                : ""));
    }

    private static byte[] ReadSyncedEmojiFile(ManifestRecord record)
    {
        if (!TryReadSyncedEmojiFile(record, out byte[] data))
        {
            throw new FileNotFoundException(
                $"Synchronized file '{record.Name + record.Extension}' is not available.");
        }
        return data;
    }

    private static bool TryReadSyncedEmojiFile(
        ManifestRecord record,
        out byte[] data)
    {
        ServerEmojiCatalog? serverCatalog = _serverEmojiCatalog;
        if (ZNet.instance?.IsServer() == true &&
            serverCatalog != null &&
            serverCatalog.Files.TryGetValue(record.Hash, out ServerEmojiBlob serverBlob) &&
            serverBlob.Data.Length == record.Length &&
            StringComparer.Ordinal.Equals(serverBlob.Record.Extension, record.Extension))
        {
            data = serverBlob.Data;
            return true;
        }

        string path = EmojiCachePath(record);
        try
        {
            FileInfo file = new(path);
            if (!file.Exists || file.Length != record.Length)
            {
                TryDeleteFile(path);
                data = Array.Empty<byte>();
                return false;
            }

            data = ReadStableFile(path, record.Length);
            if (!StringComparer.Ordinal.Equals(ComputeSha256(data), record.Hash))
            {
                TryDeleteFile(path);
                data = Array.Empty<byte>();
                return false;
            }

            try
            {
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
            }
            catch
            {
                // Cache recency is advisory only.
            }
            return true;
        }
        catch
        {
            TryDeleteFile(path);
            data = Array.Empty<byte>();
            return false;
        }
    }

    private static void WriteEmojiCacheAtomically(ManifestRecord record, byte[] data)
    {
        Directory.CreateDirectory(EmojiCacheDirectory);
        string destination = EmojiCachePath(record);
        if (TryReadSyncedEmojiFile(record, out _))
        {
            return;
        }

        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using (FileStream stream = new(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None))
            {
                stream.Write(data, 0, data.Length);
                stream.Flush(true);
            }

            TryDeleteFile(destination);
            File.Move(temporary, destination);
        }
        finally
        {
            TryDeleteFile(temporary);
        }
    }

    private static string EmojiCachePath(ManifestRecord record)
    {
        return Path.Combine(EmojiCacheDirectory, record.Hash + record.Extension);
    }

    private static void PruneEmojiCache(IReadOnlyList<ManifestRecord> activeRecords)
    {
        try
        {
            DirectoryInfo directory = new(EmojiCacheDirectory);
            if (!directory.Exists)
            {
                return;
            }

            List<FileInfo> files = directory
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .Where(file =>
                    StringComparer.Ordinal.Equals(file.Extension, ".png") ||
                    StringComparer.Ordinal.Equals(file.Extension, ".gif"))
                .ToList();
            long totalBytes = files.Sum(file => file.Length);
            if (totalBytes <= EmojiCacheMaximumBytes)
            {
                return;
            }

            HashSet<string> active = _activeRecords
                .Concat(activeRecords)
                .Select(record => record.Hash + record.Extension)
                .ToHashSet(StringComparer.Ordinal);
            foreach (FileInfo file in files
                         .Where(file => !active.Contains(file.Name))
                         .OrderBy(file => file.LastWriteTimeUtc))
            {
                long length = file.Length;
                TryDeleteFile(file.FullName);
                if (!File.Exists(file.FullName))
                {
                    totalBytes -= length;
                }
                if (totalBytes <= EmojiCacheTrimmedBytes)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan media cache cleanup failed: {ex.Message}");
        }
    }

    private static void DeletePartialEmojiCacheFiles()
    {
        if (!Directory.Exists(EmojiCacheDirectory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(
                     EmojiCacheDirectory,
                     "*.part",
                     SearchOption.TopDirectoryOnly))
        {
            TryDeleteFile(path);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // A cache entry may be retried on the next session.
        }
    }

    private static void CreateEmojiFileWatcher()
    {
        FileSystemWatcher? emojiWatcher = null;
        FileSystemWatcher? emblemWatcher = null;
        try
        {
            Directory.CreateDirectory(EmojiDirectory);
            Directory.CreateDirectory(EmblemDirectory);
            emojiWatcher = CreateMediaFileWatcher(EmojiDirectory);
            emblemWatcher = CreateMediaFileWatcher(EmblemDirectory);
            _emojiFileWatcher = emojiWatcher;
            _emblemFileWatcher = emblemWatcher;
            Interlocked.Exchange(ref _emojiWatcherRestartFailures, 0);
        }
        catch (Exception ex)
        {
            DisposeMediaFileWatcher(emojiWatcher);
            DisposeMediaFileWatcher(emblemWatcher);
            _emojiFileWatcher = null;
            _emblemFileWatcher = null;
            ClanPlugin.ClanLogger.LogWarning(
                $"Clan media hot reload watchers could not be started: {ex.Message}");
            if (Interlocked.Increment(ref _emojiWatcherRestartFailures) <= MaximumReloadRetries)
            {
                Interlocked.Exchange(ref _emojiWatcherNeedsRestart, 1);
                Interlocked.Exchange(
                    ref _emojiReloadRequestedAtTicks,
                    DateTime.UtcNow.Ticks);
            }
        }
    }

    private static FileSystemWatcher CreateMediaFileWatcher(string directory)
    {
        FileSystemWatcher watcher = new(directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName |
                           NotifyFilters.LastWrite |
                           NotifyFilters.Size |
                           NotifyFilters.CreationTime
        };
        watcher.Changed += OnEmojiFileChanged;
        watcher.Created += OnEmojiFileChanged;
        watcher.Deleted += OnEmojiFileChanged;
        watcher.Renamed += OnEmojiFileRenamed;
        watcher.Error += OnEmojiWatcherError;
        watcher.EnableRaisingEvents = true;
        return watcher;
    }

    private static void DisposeEmojiFileWatcher()
    {
        FileSystemWatcher? emojiWatcher = _emojiFileWatcher;
        FileSystemWatcher? emblemWatcher = _emblemFileWatcher;
        _emojiFileWatcher = null;
        _emblemFileWatcher = null;
        DisposeMediaFileWatcher(emojiWatcher);
        DisposeMediaFileWatcher(emblemWatcher);
    }

    private static void DisposeMediaFileWatcher(FileSystemWatcher? watcher)
    {
        if (watcher == null)
        {
            return;
        }
        watcher.EnableRaisingEvents = false;
        watcher.Changed -= OnEmojiFileChanged;
        watcher.Created -= OnEmojiFileChanged;
        watcher.Deleted -= OnEmojiFileChanged;
        watcher.Renamed -= OnEmojiFileRenamed;
        watcher.Error -= OnEmojiWatcherError;
        watcher.Dispose();
    }

    private static void OnEmojiFileChanged(object sender, FileSystemEventArgs args)
    {
        if (IsSupportedEmojiPath(args.FullPath))
        {
            MarkEmojiServerFileDirty(args.FullPath);
        }
    }

    private static void OnEmojiFileRenamed(object sender, RenamedEventArgs args)
    {
        if (IsSupportedEmojiPath(args.OldFullPath))
        {
            MarkEmojiServerFileDirty(args.OldFullPath);
        }
        if (IsSupportedEmojiPath(args.FullPath))
        {
            MarkEmojiServerFileDirty(args.FullPath);
        }
    }

    private static void OnEmojiWatcherError(object sender, ErrorEventArgs args)
    {
        Interlocked.Exchange(ref _emojiWatcherRestartFailures, 0);
        Interlocked.Exchange(ref _emojiWatcherNeedsRestart, 1);
        MarkAllEmojiServerFilesDirty();
    }

    private static void MarkAllEmojiServerFilesDirty()
    {
        try
        {
            foreach (string directory in new[] { EmojiDirectory, EmblemDirectory })
            {
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                foreach (string path in Directory.EnumerateFiles(
                             directory,
                             "*",
                             SearchOption.TopDirectoryOnly))
                {
                    if (IsSupportedEmojiPath(path))
                    {
                        MarkEmojiServerFileDirty(path);
                    }
                }
            }
        }
        catch
        {
            // The normal metadata scan still detects ordinary file changes.
        }

        MarkEmojiServerFilesDirty();
    }

    private static bool IsSupportedEmojiPath(string path)
    {
        string extension = Path.GetExtension(path);
        return StringComparer.Ordinal.Equals(extension, ".png") ||
               StringComparer.Ordinal.Equals(extension, ".gif");
    }

    private static void MarkEmojiServerFilesDirty()
    {
        Interlocked.Exchange(ref _emojiReloadFailures, 0);
        Interlocked.Exchange(
            ref _emojiReloadRequestedAtTicks,
            DateTime.UtcNow.Ticks);
    }

    private static bool ConsumeEmojiServerReloadRequest()
    {
        long requestedAt = Interlocked.Read(ref _emojiReloadRequestedAtTicks);
        if (requestedAt == 0L ||
            DateTime.UtcNow.Ticks - requestedAt < EmojiReloadDebounceTicks)
        {
            return false;
        }

        if (Interlocked.CompareExchange(
                ref _emojiReloadRequestedAtTicks,
                0L,
                requestedAt) != requestedAt)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _emojiWatcherNeedsRestart, 0) != 0)
        {
            DisposeEmojiFileWatcher();
            CreateEmojiFileWatcher();
        }
        return true;
    }

    private static void ResetEmojiServerReloadFailures()
    {
        Interlocked.Exchange(ref _emojiReloadFailures, 0);
    }

    private static void ScheduleEmojiServerReloadRetry(Exception error)
    {
        if ((error is not IOException && error is not InvalidDataException) ||
            Interlocked.Increment(ref _emojiReloadFailures) > MaximumReloadRetries)
        {
            return;
        }

        Interlocked.Exchange(
            ref _emojiReloadRequestedAtTicks,
            DateTime.UtcNow.Ticks);
    }

    private static void RequireEmojiPackageConsumed(ZPackage package)
    {
        if (package.GetPos() != package.Size())
        {
            throw new InvalidDataException("Emoji RPC contains unexpected trailing data.");
        }
    }

    private enum EmojiFileResponseStatus : byte
    {
        Data = 0,
        Busy = 1,
        StaleCatalog = 2,
        Unavailable = 3,
        Rejected = 4
    }

    private sealed class ServerEmojiBlob
    {
        public readonly ManifestRecord Record;
        public readonly byte[] Data;
        public readonly int GifFrameCount;

        public ServerEmojiBlob(
            ManifestRecord record,
            byte[] data,
            int gifFrameCount)
        {
            Record = record;
            Data = data;
            GifFrameCount = gifFrameCount;
        }
    }

    private sealed class ServerEmojiCatalog
    {
        public readonly string Manifest;
        public readonly string CatalogId;
        public readonly IReadOnlyList<ManifestRecord> Records;
        public readonly IReadOnlyDictionary<string, ServerEmojiBlob> Files;

        public ServerEmojiCatalog(
            string manifest,
            string catalogId,
            IReadOnlyList<ManifestRecord> records,
            IReadOnlyDictionary<string, ServerEmojiBlob> files)
        {
            Manifest = manifest;
            CatalogId = catalogId;
            Records = records;
            Files = files;
        }
    }

    private sealed class ClientEmojiCatalog
    {
        public readonly ZNet? Session;
        public readonly string Manifest;
        public readonly string CatalogId;
        public readonly IReadOnlyList<ManifestRecord> Records;
        public int NextRecordIndex;
        public int CacheHits;
        public int Downloads;
        public ClientEmojiDownload? Download;
        public bool Ready;
        public bool Failed;
        public int CatalogRetryCount;
        public float CatalogRetryAt;

        public ClientEmojiCatalog(
            ZNet? session,
            string manifest,
            string catalogId,
            IReadOnlyList<ManifestRecord> records)
        {
            Session = session;
            Manifest = manifest;
            CatalogId = catalogId;
            Records = records;
        }
    }

    private sealed class ClientEmojiDownload
    {
        private const byte Missing = 0;
        private const byte InFlight = 1;
        private const byte Received = 2;

        public readonly ManifestRecord Record;
        public readonly byte[] Buffer;
        private readonly byte[] _chunkStates;
        private readonly int[] _requestIds;
        private readonly int[] _retryCounts;
        private readonly float[] _nextRequestAt;
        private readonly float[] _responseDeadlines;
        private int _nextChunkCursor;
        private int _receivedBytes;

        public int ChunkCount => _chunkStates.Length;
        public int InFlightCount { get; private set; }
        public bool Completed => _receivedBytes == Buffer.Length;

        public ClientEmojiDownload(ManifestRecord record)
        {
            Record = record;
            Buffer = new byte[record.Length];
            int chunkCount = (record.Length + EmojiChunkBytes - 1) / EmojiChunkBytes;
            _chunkStates = new byte[chunkCount];
            _requestIds = new int[chunkCount];
            _retryCounts = new int[chunkCount];
            _nextRequestAt = new float[chunkCount];
            _responseDeadlines = new float[chunkCount];
        }

        public bool TryGetChunkIndex(int offset, out int chunkIndex)
        {
            chunkIndex = offset / EmojiChunkBytes;
            return offset >= 0 &&
                   offset % EmojiChunkBytes == 0 &&
                   offset < Buffer.Length &&
                   chunkIndex >= 0 &&
                   chunkIndex < ChunkCount;
        }

        public bool HasIssuedRequest(int chunkIndex, int requestId)
        {
            return requestId > 0 && requestId <= _requestIds[chunkIndex];
        }

        public bool IsCurrentRequest(int chunkIndex, int requestId)
        {
            return _chunkStates[chunkIndex] == InFlight &&
                   _requestIds[chunkIndex] == requestId;
        }

        public bool IsChunkReceived(int chunkIndex)
        {
            return _chunkStates[chunkIndex] == Received;
        }

        public bool IsResponseTimedOut(int chunkIndex, float now)
        {
            return _chunkStates[chunkIndex] == InFlight &&
                   now >= _responseDeadlines[chunkIndex];
        }

        public bool TryGetNextRequestChunk(float now, out int chunkIndex)
        {
            for (int scanned = 0; scanned < ChunkCount; scanned++)
            {
                int candidate = (_nextChunkCursor + scanned) % ChunkCount;
                if (_chunkStates[candidate] != Missing ||
                    now < _nextRequestAt[candidate])
                {
                    continue;
                }

                _nextChunkCursor = (candidate + 1) % ChunkCount;
                chunkIndex = candidate;
                return true;
            }

            chunkIndex = -1;
            return false;
        }

        public int MarkRequested(int chunkIndex, float responseDeadline)
        {
            if (_chunkStates[chunkIndex] != Missing)
            {
                throw new InvalidOperationException("Media chunk is already in flight.");
            }

            int requestId = ++_requestIds[chunkIndex];
            _chunkStates[chunkIndex] = InFlight;
            _responseDeadlines[chunkIndex] = responseDeadline;
            InFlightCount++;
            return requestId;
        }

        public void MarkReceived(int chunkIndex, int length)
        {
            if (_chunkStates[chunkIndex] == Received)
            {
                return;
            }
            if (_chunkStates[chunkIndex] == InFlight)
            {
                InFlightCount--;
            }

            _chunkStates[chunkIndex] = Received;
            _retryCounts[chunkIndex] = 0;
            _receivedBytes += length;
            if (_receivedBytes > Buffer.Length)
            {
                throw new InvalidDataException("Received media chunks exceed the manifest length.");
            }
        }

        public int IncrementRetry(int chunkIndex)
        {
            return ++_retryCounts[chunkIndex];
        }

        public void DeferChunk(int chunkIndex, float nextRequestAt)
        {
            if (_chunkStates[chunkIndex] == Received)
            {
                return;
            }
            if (_chunkStates[chunkIndex] == InFlight)
            {
                InFlightCount--;
            }

            _chunkStates[chunkIndex] = Missing;
            _nextRequestAt[chunkIndex] = Math.Max(
                _nextRequestAt[chunkIndex],
                nextRequestAt);
        }
    }

    private sealed class PeerTransferBudget
    {
        private float _lastRefillAt;
        private float _malformedWindowStartedAt;
        private double _requestTokens;
        public double ByteTokens;
        public int MalformedLogs;

        public PeerTransferBudget(float now)
        {
            _lastRefillAt = now;
            _malformedWindowStartedAt = now;
            _requestTokens = PeerRequestTokenCapacity;
            ByteTokens = PeerTransferByteCapacity;
        }

        public void Refill(float now)
        {
            float elapsed = Math.Max(0f, now - _lastRefillAt);
            _lastRefillAt = now;
            _requestTokens = Math.Min(
                PeerRequestTokenCapacity,
                _requestTokens + elapsed * PeerRequestTokensPerSecond);
            ByteTokens = Math.Min(
                PeerTransferByteCapacity,
                ByteTokens + elapsed * PeerTransferBytesPerSecond);
        }

        public bool TryConsumeRequest(float now)
        {
            Refill(now);
            if (_requestTokens < 1d)
            {
                return false;
            }

            _requestTokens -= 1d;
            return true;
        }

        public bool TryConsumeMalformedLog(float now)
        {
            if (now < _malformedWindowStartedAt ||
                now - _malformedWindowStartedAt >= MalformedLogWindowSeconds)
            {
                _malformedWindowStartedAt = now;
                MalformedLogs = 0;
            }
            if (MalformedLogs >= 3)
            {
                return false;
            }

            MalformedLogs++;
            return true;
        }
    }

    private sealed class GlobalTransferBudget
    {
        private float _lastRefillAt = -1f;
        public double ByteTokens { get; set; }

        public GlobalTransferBudget()
        {
            Reset();
        }

        public void Reset()
        {
            _lastRefillAt = -1f;
            ByteTokens = GlobalTransferByteCapacity;
        }

        public void Refill(float now)
        {
            if (_lastRefillAt < 0f || now < _lastRefillAt)
            {
                _lastRefillAt = now;
                return;
            }

            float elapsed = now - _lastRefillAt;
            _lastRefillAt = now;
            ByteTokens = Math.Min(
                GlobalTransferByteCapacity,
                ByteTokens + elapsed * GlobalTransferBytesPerSecond);
        }
    }
}
