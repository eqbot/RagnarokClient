using Dalamud.Utility;
using K4os.Compression.LZ4.Streams;
using MareSynchronos.API.Data;
using MareSynchronos.API.Dto.Files;
using MareSynchronos.API.Routes;
using MareSynchronos.FileCache;
using MareSynchronos.PlayerData.Handlers;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Utils;
using MareSynchronos.WebAPI.Files.Models;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;

namespace MareSynchronos.WebAPI.Files;

public partial class FileDownloadManager : DisposableMediatorSubscriberBase
{
    private readonly Dictionary<string, FileDownloadStatus> _downloadStatus;
    private readonly FileCompactor _fileCompactor;
    private readonly FileCacheManager _fileDbManager;
    private readonly FileTransferOrchestrator _orchestrator;
    private readonly List<ThrottledStream> _activeDownloadStreams;
    private readonly IpfsManager _ipfsManager;

    public FileDownloadManager(ILogger<FileDownloadManager> logger, MareMediator mediator,
        FileTransferOrchestrator orchestrator,
        FileCacheManager fileCacheManager, FileCompactor fileCompactor, IpfsManager ipfsManager) : base(logger, mediator)
    {
        _downloadStatus = new Dictionary<string, FileDownloadStatus>(StringComparer.Ordinal);
        _orchestrator = orchestrator;
        _fileDbManager = fileCacheManager;
        _fileCompactor = fileCompactor;
        _ipfsManager = ipfsManager;
        _activeDownloadStreams = [];

        Mediator.Subscribe<DownloadLimitChangedMessage>(this, (msg) =>
        {
            if (!_activeDownloadStreams.Any()) return;
            var newLimit = _orchestrator.DownloadLimitPerSlot();
            Logger.LogTrace("Setting new Download Speed Limit to {newLimit}", newLimit);
            foreach (var stream in _activeDownloadStreams)
            {
                stream.BandwidthLimit = newLimit;
            }
        });
    }

    public List<DownloadFileTransfer> CurrentDownloads { get; private set; } = [];

    public List<FileTransfer> ForbiddenTransfers => _orchestrator.ForbiddenTransfers;

    public bool IsDownloading => !CurrentDownloads.Any();

    public void ClearDownload()
    {
        CurrentDownloads.Clear();
        _downloadStatus.Clear();
    }

    public async Task DownloadFiles(GameObjectHandler gameObject, List<FileReplacementData> fileReplacementDto, CancellationToken ct)
    {
        Mediator.Publish(new HaltScanMessage(nameof(DownloadFiles)));
        try
        {
            await DownloadFilesInternalIPFS(gameObject, fileReplacementDto, ct).ConfigureAwait(false);
        }
        catch
        {
            ClearDownload();
        }
        finally
        {
            Mediator.Publish(new DownloadFinishedMessage(gameObject));
            Mediator.Publish(new ResumeScanMessage(nameof(DownloadFiles)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        ClearDownload();
        foreach (var stream in _activeDownloadStreams.ToList())
        {
            try
            {
                stream.Dispose();
            }
            catch
            {
                // do nothing
                //
            }
        }
        base.Dispose(disposing);
    }

    public async Task<List<DownloadFileTransfer>> InitiateDownloadList(GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacement, CancellationToken ct)
    {
        Logger.LogDebug("Download start: {id}", gameObjectHandler.Name);

        List<DownloadFileDto> downloadFileInfoFromService =
        [
            .. await FilesGetSizes(fileReplacement.Select(f => f.Hash).Distinct(StringComparer.Ordinal).ToList(), ct).ConfigureAwait(false),
        ];

        Logger.LogDebug("Files with size 0 or less: {files}", string.Join(", ", downloadFileInfoFromService.Where(f => f.Size <= 0).Select(f => f.Hash)));

        foreach (var dto in downloadFileInfoFromService.Where(c => c.IsForbidden))
        {
            if (!_orchestrator.ForbiddenTransfers.Exists(f => string.Equals(f.Hash, dto.Hash, StringComparison.Ordinal)))
            {
                _orchestrator.ForbiddenTransfers.Add(new DownloadFileTransfer(dto));
            }
        }

        CurrentDownloads = downloadFileInfoFromService.Distinct().Select(d => new DownloadFileTransfer(d))
            .Where(d => d.CanBeTransferred).ToList();

        return CurrentDownloads;
    }

    /// <summary>
    /// Pulls each file from IPFS, using the hash (CID) in the file replacements
    /// </summary>
    /// <param name="gameObjectHandler"></param>
    /// <param name="fileReplacements"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    private async Task DownloadFilesInternalIPFS(GameObjectHandler gameObjectHandler, List<FileReplacementData> fileReplacements, CancellationToken ct)
    {
        foreach (var fileReplacement in fileReplacements)
        {
            _downloadStatus[fileReplacement.Hash] = new FileDownloadStatus()
            {
                DownloadStatus = DownloadStatus.Initializing,
                TotalBytes = CurrentDownloads.Where(f => f.Hash.Equals(fileReplacement.Hash)).Sum(d => d.Total),
                TotalFiles = 1,
                TransferredBytes = 0,
                TransferredFiles = 0
            };
        }

        Mediator.Publish(new DownloadStartedMessage(gameObjectHandler, _downloadStatus));

        await Parallel.ForEachAsync(fileReplacements, new ParallelOptions()
        {
            MaxDegreeOfParallelism = 8, //Dunno how much parallelism we want for this, but filereplacements.count feels wrong
            CancellationToken = ct,
        },
        async (fileReplacement, token) =>
        {
            //feels like i must be missing something replacing such a complex download function with something so simple but YOLO
            Logger.LogDebug("Beginning IPFS download for {hash}", fileReplacement.Hash);
            var fileStream = await _ipfsManager.DownloadFile(fileReplacement.Hash).ConfigureAwait(false);

            if (_downloadStatus.TryGetValue(fileReplacement.Hash, out var status))
            {
                status.TransferredFiles = 1;
                status.DownloadStatus = DownloadStatus.Decompressing;
            }


            var fileExtension = fileReplacement.GamePaths[0].Split(".")[^1];
            var tmpPath = _fileDbManager.GetCacheFilePath(Guid.NewGuid().ToString(), "tmp");
            var filePath = _fileDbManager.GetCacheFilePath(fileReplacement.Hash, fileExtension);
            try
            {
                using var tmpFileStream = new FileStream(tmpPath, new FileStreamOptions()
                {
                    Mode = FileMode.CreateNew,
                    Access = FileAccess.Write,
                    Share = FileShare.None
                });
                await fileStream.CopyToAsync(tmpFileStream, CancellationToken.None).ConfigureAwait(false);
                tmpFileStream.Close();
                _fileCompactor.RenameAndCompact(filePath, tmpPath);
                PersistFileToStorage(fileReplacement.Hash, filePath, fileStream.Length);
            }
            catch (EndOfStreamException)
            {
                Logger.LogWarning("Failure to extract file {fileHash}, stream ended prematurely", fileReplacement.Hash);
            }
            catch (Exception e)
            {
                Logger.LogWarning(e, "Error during decompression of {hash}", fileReplacement.Hash);
                Logger.LogWarning(" - {h}: {x}", fileReplacement.Hash, fileReplacement.GamePaths[0]);
            }
            finally
            {
                if (File.Exists(tmpPath))
                    File.Delete(tmpPath);
            }

        }
        ).ConfigureAwait(false);

        Logger.LogDebug("Download end: {id}", gameObjectHandler);

        ClearDownload();
    }

    private async Task<List<DownloadFileDto>> FilesGetSizes(List<string> hashes, CancellationToken ct)
    {
        List<DownloadFileDto> fileList = new List<DownloadFileDto>();
        foreach (var cid in hashes)
        {
            DownloadFileDto entry = new DownloadFileDto()
            {
                Size = await _ipfsManager.GetSize(cid).ConfigureAwait(false),
                Url = cid,
                Hash = cid,
                FileExists = true,
                IsForbidden = false,
            };
            fileList.Add(entry);
        }
        return fileList;
    }

    private void PersistFileToStorage(string fileHash, string filePath, long? compressedSize = null)
    {
        try
        {
            var entry = _fileDbManager.CreateCacheEntry(filePath, fileHash);
            if (entry != null && !string.Equals(entry.Hash, fileHash, StringComparison.OrdinalIgnoreCase))
            {
                _fileDbManager.RemoveHashedFile(entry.Hash, entry.PrefixedFilePath);
                entry = null;
            }
            if (entry != null)
                entry.CompressedSize = compressedSize;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "Error creating cache entry");
        }
    }
}
