using System.Globalization;
using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Trickplay;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;
using Nick.Plugin.Jellyscrub.Api;
using Nick.Plugin.Jellyscrub.Drawing;

namespace Nick.Plugin.Jellyscrub.Conversion;

/// <summary>
/// Shared task for conversion and deletion of BIF files. 
/// </summary>
public class ConversionTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly IFileSystem _fileSystem;
    private readonly ITrickplayManager _trickplayManager;
    private readonly IApplicationPaths _appPaths;
    private readonly IServerConfigurationManager _configManager;
    private readonly ILogger _logger;

    private readonly PrettyLittleLogger _convertLogger = new PrettyLittleLogger();
    private readonly PrettyLittleLogger _deleteLogger = new PrettyLittleLogger();

    private bool _busy = false;
    private readonly object _lock = new object();

    public ConversionTask(
        ILibraryManager libraryManager,
        IFileSystem fileSystem,
        ITrickplayManager trickplayManager,
        IApplicationPaths appPaths,
        IServerConfigurationManager configManager,
        ILogger<ConversionTask> logger
        )
    {
        _libraryManager = libraryManager;
        _fileSystem = fileSystem;
        _trickplayManager = trickplayManager;
        _appPaths = appPaths;
        _configManager = configManager;
        _logger = logger;
    }

    /*
     * 
     * Conversion
     * 
     */
    public async Task ConvertAll(ConvertOptions options)
    {
        if (!CheckAndSetBusy(_convertLogger)) return;

        _convertLogger.ClearSynchronized();

        var convertInfos = await GetConvertInfo().ConfigureAwait(false); 
        int total = convertInfos.Count;
        int current = 0;
        int attempted = 0;
        int completed = 0;

        foreach (var convertInfo in convertInfos)
        {
            try
            {
                current++;

                // Check that it doesn't already exist
                var globalTOptions = _configManager.Configuration.TrickplayOptions;
                var libraryOptions = _libraryManager.GetLibraryOptions(convertInfo.Item);
                var tileWidth = globalTOptions.TileWidth;
                var tileHeight = globalTOptions.TileHeight;
                var saveWithMedia = libraryOptions is null ? false : libraryOptions.SaveTrickplayWithMedia;
                var tilesMetaDir = _trickplayManager.GetTrickplayDirectory(convertInfo.Item, tileWidth, tileHeight, convertInfo.Width, saveWithMedia);

                var itemId = convertInfo.Item.Id;
                var width = convertInfo.Width;
                var bifPath = convertInfo.Path;

                if (!options.ForceConvert && Directory.Exists(tilesMetaDir) && (await _trickplayManager.GetTrickplayResolutions(itemId).ConfigureAwait(false)).ContainsKey(width))
                {
                    _convertLogger.LogSynchronized($"Found existing trickplay files for {bifPath}, use force re-convert if necessary. [{current}/{total}]", PrettyLittleLogger.LogColor.Info);
                    continue;
                }

                // Extract images
                attempted++;
                _convertLogger.LogSynchronized($"Converting {bifPath} [{current}/{total}]", PrettyLittleLogger.LogColor.Info);

                var imgTempDir = Path.Combine(_appPaths.TempDirectory, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(imgTempDir);
                var (interval, images) = await ExtractImages(bifPath, imgTempDir);

                if (images.Count == 0)
                {
                    _convertLogger.LogSynchronized($"Image extration for {bifPath} returned an empty list. Skipping...", PrettyLittleLogger.LogColor.Error);
                    continue;
                }

                // Create tiles
                var localTOptions = new TrickplayOptions();
                localTOptions.Interval = interval;
                localTOptions.TileWidth = tileWidth;
                localTOptions.TileHeight = tileHeight;
                localTOptions.JpegQuality = globalTOptions.JpegQuality;

                Directory.CreateDirectory(tilesMetaDir);
                TrickplayInfo trickplayInfo = _trickplayManager.CreateTiles(images, convertInfo.Width, localTOptions, tilesMetaDir);

                // Save trickplay info
                trickplayInfo.ItemId = itemId;
                await _trickplayManager.SaveTrickplayInfo(trickplayInfo).ConfigureAwait(false);

                // Delete temp folder
                Directory.Delete(imgTempDir, true);

                _convertLogger.LogSynchronized($"Finished converting {bifPath}", PrettyLittleLogger.LogColor.Sucess);
                completed++;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error converting BIF file {0}", convertInfo.Path);
                _convertLogger.LogSynchronized($"Encountered error while converting {convertInfo.Path}, please check the console.", PrettyLittleLogger.LogColor.Error);
            }
        }

        if (attempted > 0)
        {
            _convertLogger.LogSynchronized($"Successfully converted {completed}/{attempted} attempted .BIF files!", PrettyLittleLogger.LogColor.Info);
        }
        else
        {
            _convertLogger.LogSynchronized("Task completed without attempting to convert any .BIF files.", PrettyLittleLogger.LogColor.Info);
        }
            

        _busy = false;
    }

    private async Task<(int, List<string>)> ExtractImages(string bifPath, string outputDir)
    {
        List<string> images = new();
        List<UInt32> imageOffsets = new();

        using var bifStream = File.OpenRead(bifPath);
        using var bifReader = new BinaryReader(bifStream);

        // Get interval
        bifStream.Seek(16, SeekOrigin.Begin);
        UInt32 interval = bifReader.ReadUInt32();
        interval = interval == 0 ? 1000 : interval;

        // Skip to index section
        bifStream.Seek(64, SeekOrigin.Begin);
        UInt32 index = 0;
        while (index < UInt32.MaxValue)
        {
            var timestamp = bifReader.ReadUInt32();
            var offset = bifReader.ReadUInt32();
            imageOffsets.Add(offset);

            if (timestamp == UInt32.MaxValue)
                break;
        }

        // Bif files must be adjacent, so only seek once to first image
        if (imageOffsets.Count > 1)
            bifStream.Seek(imageOffsets[0], SeekOrigin.Begin);

        // Extract images
        _logger.LogInformation("Extracting BIF images to {0}", outputDir);
        for (int i = 0; i < imageOffsets.Count - 1; i++)
        {
            var length = imageOffsets[i + 1] - imageOffsets[i];

            var imgPath = Path.Combine(outputDir, i.ToString(CultureInfo.InvariantCulture) + ".jpg");
            using var imgStream = File.Create(imgPath);
            byte[] imgBytes = new byte[length];
            await bifStream.ReadExactlyAsync(imgBytes, 0, (int)length).ConfigureAwait(false);
            await imgStream.WriteAsync(imgBytes).ConfigureAwait(false);

            images.Add(imgPath);
        }

        return ((int)interval, images);
    }

    /*
     * 
     * Deletion
     * 
     */
    private static readonly string[] allowedExtensions = { ".json", ".ignore" };
    public async Task DeleteAll(Api.DeleteOptions options)
    {
        if (!CheckAndSetBusy(_deleteLogger)) return;

        _deleteLogger.ClearSynchronized();

        int attempted = 0;
        int completed = 0;
        foreach (var convertInfo in await GetConvertInfo(true).ConfigureAwait(false))
        {
            try
            {
                // Don't delete if matching native trickplay not found
                var globalTOptions = _configManager.Configuration.TrickplayOptions;
                var libraryOptions = _libraryManager.GetLibraryOptions(convertInfo.Item);
                var tileWidth = globalTOptions.TileWidth;
                var tileHeight = globalTOptions.TileHeight;
                var saveWithMedia = libraryOptions is null ? false : libraryOptions.SaveTrickplayWithMedia;

                var tilesMetaDir = _trickplayManager.GetTrickplayDirectory(convertInfo.Item, tileWidth, tileHeight, convertInfo.Width, saveWithMedia);
                var itemId = convertInfo.Item.Id;
                var width = convertInfo.Width;
                var bifPath = convertInfo.Path;

                attempted++;    // Skipping still counts as an attempt for deletion, since having left over files is an "error"
                if (!options.ForceDelete && !(Directory.Exists(tilesMetaDir) && (await _trickplayManager.GetTrickplayResolutions(itemId).ConfigureAwait(false)).ContainsKey(width)))
                {
                    _deleteLogger.LogSynchronized($"Couldn't find native trickplay data for {bifPath}, use force delete if necessary.", PrettyLittleLogger.LogColor.Error);
                    continue;
                }

                // Delete .bif file
                // Allow non-existant paths in case parent folder wasn't deleted and task is re-run
                if (File.Exists(bifPath))
                {
                    if (!options.ForceDelete && !Path.GetExtension(bifPath).ToLower().Equals(".bif"))
                        throw new InvalidOperationException($"Path to bif file has incorrect file extension {bifPath}");
                    File.Delete(bifPath);
                    _deleteLogger.LogSynchronized($"Deleted {bifPath}", PrettyLittleLogger.LogColor.Sucess);
                    _logger.LogInformation("Deleted file {0}", bifPath);
                    completed++;
                }
                else
                {
                    attempted--;
                }


                // Delete folder if only files left are in allowed list (or empty subdirectories)
                var trickplayFolder = Directory.GetParent(bifPath);
                if (trickplayFolder is null || (!options.ForceDelete && !trickplayFolder.Name.ToLower().Equals("trickplay")))
                    throw new InvalidOperationException($"BIF parent folder is null or has invalid name {trickplayFolder?.FullName}");

                if (options.DeleteNonEmpty)
                {
                    Directory.Delete(trickplayFolder.FullName, true);
                }
                else
                {
                    // There could technically be dangling .bif files that will never be deleted, so user should be alerted.
                    //if (trickplayFolder.GetFiles("*.bif", SearchOption.AllDirectories).Length > 0)
                    //    continue;

                    bool canDelete = true;
                    bool containsBifs = false;
                    foreach (var file in trickplayFolder.GetFiles("*", SearchOption.AllDirectories))
                    {
                        var extension = Path.GetExtension(file.FullName).ToLower();
                        if (!allowedExtensions.Contains(extension))
                        {
                            canDelete = false;

                            if (extension.Equals(".bif"))
                            {
                                containsBifs = true;
                                _deleteLogger.LogSynchronized($"-> {file.FullName}", PrettyLittleLogger.LogColor.Info);
                            }
                            else
                            {
                                _deleteLogger.LogSynchronized($"-> {file.FullName}", PrettyLittleLogger.LogColor.Error);
                            }
                        }
                    }

                    if (canDelete)
                    {
                        Directory.Delete(trickplayFolder.FullName, true);
                    }
                    else if (containsBifs)
                    {
                        continue;
                    }
                    else
                    {
                        _deleteLogger.LogSynchronized($"Couldn't delete folder {trickplayFolder.FullName} as it is non-empty. Use delete non-empty folders if necessary.", PrettyLittleLogger.LogColor.Error);
                        continue;
                    }
                }
                _deleteLogger.LogSynchronized($"Deleted folder {trickplayFolder.FullName}", PrettyLittleLogger.LogColor.Sucess);
                _logger.LogInformation("Deleted folder {0}", trickplayFolder.FullName);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error deleting BIF file {0}", convertInfo.Path);
                _deleteLogger.LogSynchronized($"Encountered error while deleting {convertInfo.Path}, please check the console.", PrettyLittleLogger.LogColor.Error);
            }
        }

        if (attempted > 0)
        {
            _deleteLogger.LogSynchronized($"Successfully deleted {completed}/{attempted} .BIF files!", PrettyLittleLogger.LogColor.Info);

        }
        else
        {
            _deleteLogger.LogSynchronized("Task completed without attempting to delete any .BIF files.", PrettyLittleLogger.LogColor.Info);
        }

        _busy = false;
    }

    /*
     * 
     * Util
     * 
     */
    private async Task<List<ConvertInfo>> GetConvertInfo(bool allowNonExistant = false)
    {
        List<ConvertInfo> bifFiles = new();

        // Get all items
        var items = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
            IsVirtualItem = false,
            Recursive = true

        }).OfType<Video>().ToList();

        // Get all BIF files and widths
        foreach (var item in items)
        {
            try
            {
                Manifest? manifest = await VideoProcessor.GetManifest(item);
                if (manifest?.WidthResolutions == null) continue;

                foreach (var width in manifest.WidthResolutions)
                {
                    var path = allowNonExistant ? VideoProcessor.GetNewBifPath(item, width) : VideoProcessor.GetExistingBifPath(item, width);
                    if (path != null)
                    {
                        bifFiles.Add(new ConvertInfo { Item = item, Path = path, Width = width });
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error reading manifest for item \"{0}\" ({1})", item.Name, item.Id);
            }
        }

        return bifFiles;
    }

    public string GetConvertLog()
    {
        return _convertLogger.ReadSynchronized();
    }

    public string GetDeleteLog()
    {
        return _deleteLogger.ReadSynchronized();
    }

    private bool CheckAndSetBusy(PrettyLittleLogger logger)
    {
        lock (_lock)
        {
            if (_busy)
            {
                logger.LogSynchronized("[!] Already busy running a task.", PrettyLittleLogger.LogColor.Error);
                return false;
            }
            _busy = true;
            return true;
        }
    }
}
