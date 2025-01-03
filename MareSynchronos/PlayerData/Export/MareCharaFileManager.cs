﻿using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using LZ4;
using MareSynchronos.API.Data.Enum;
using MareSynchronos.FileCache;
using MareSynchronos.Interop.Ipc;
using MareSynchronos.MareConfiguration;
using MareSynchronos.PlayerData.Factories;
using MareSynchronos.PlayerData.Handlers;
using MareSynchronos.Services;
using MareSynchronos.Services.Mediator;
using MareSynchronos.Utils;
using McdfDataImporter;
using Microsoft.Extensions.Logging;
using System.Text;
using CharacterData = MareSynchronos.API.Data.CharacterData;

namespace MareSynchronos.PlayerData.Export;

public class MareCharaFileManager : DisposableMediatorSubscriberBase
{
    private readonly MareConfigService _configService;
    private readonly DalamudUtilService _dalamudUtil;
    private readonly MareCharaFileDataFactory _factory;
    private readonly GameObjectHandlerFactory _gameObjectHandlerFactory;
    private readonly IpcManager _ipcManager;
    private readonly IPluginLog _Logger;
    private readonly FileCacheManager _manager;
    private int _globalFileCounter = 0;
    private bool _isInGpose = true;
    private CharacterData _characterData;
    public event EventHandler<Tuple<IGameObject, long, MareCharaFileHeader>> OnMcdfFailed;
    Dictionary<string, Tuple<Guid, Guid>> pastCollections = new Dictionary<string, Tuple<Guid, Guid>>();
    public MareCharaFileManager(GameObjectHandlerFactory gameObjectHandlerFactory,
        FileCacheManager manager, IpcManager ipcManager, MareConfigService configService, DalamudUtilService dalamudUtil,
        McdfMediator mediator) : base(mediator)
    {
        _factory = new(manager);
        _Logger = EntryPoint.PluginLog;
        _gameObjectHandlerFactory = gameObjectHandlerFactory;
        _manager = manager;
        _ipcManager = ipcManager;
        _configService = configService;
        _dalamudUtil = dalamudUtil;
        Mediator.Subscribe<CharacterDataCreatedMessage>(this, (x) => PlayerManagerOnPlayerHasChanged(x.CharacterData));
        Mediator.Subscribe<GposeStartMessage>(this, _ => _isInGpose = true);
        Mediator.Subscribe<GposeEndMessage>(this, async _ =>
        {
        });
    }

    private void PlayerManagerOnPlayerHasChanged(CharacterData characterData)
    {
        _characterData = characterData;
    }

    public bool CurrentlyWorking { get; private set; } = false;

    public async Task ApplyMareCharaFile(IGameObject? charaTarget, long expectedLength, MareCharaFileHeader loadedCharaFile)
    {
        if (charaTarget == null) return;
        Dictionary<string, string> extractedFiles = new(StringComparer.Ordinal);
        CurrentlyWorking = true;
        try
        {
            if (loadedCharaFile == null || !File.Exists(loadedCharaFile.FilePath)) return;
            var unwrapped = File.OpenRead(loadedCharaFile.FilePath);
            await using (unwrapped.ConfigureAwait(false))
            {
                CancellationTokenSource disposeCts = new();
                using var lz4Stream = new LZ4Stream(unwrapped, LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
                using var reader = new BinaryReader(lz4Stream);
                MareCharaFileHeader.AdvanceReaderToData(reader);
                _Logger.Debug("Applying to {chara}, expected length of contents: {exp}, stream length: {len}", charaTarget.Name.TextValue, expectedLength, reader.BaseStream.Length);
                extractedFiles = ExtractFilesFromCharaFile(charaTarget.Name.TextValue, loadedCharaFile, reader, expectedLength);
                Dictionary<string, string> fileSwaps = new(StringComparer.Ordinal);
                foreach (var fileSwap in loadedCharaFile.CharaFileData.FileSwaps)
                {
                    foreach (var path in fileSwap.GamePaths)
                    {
                        fileSwaps.Add(path, fileSwap.FileSwapPath);
                    }
                }
                var applicationId = Guid.NewGuid();

                if (pastCollections.ContainsKey(charaTarget.Name.TextValue))
                {
                    try
                    {
                        var data = pastCollections[charaTarget.Name.TextValue];
                        await _ipcManager.Penumbra.RemoveTemporaryCollectionAsync(data.Item1, data.Item2).ConfigureAwait(false);
                    }
                    catch
                    {

                    }
                }

                var coll = await _ipcManager.Penumbra.CreateTemporaryCollectionAsync(charaTarget.Name.TextValue).ConfigureAwait(false);
                await _ipcManager.Penumbra.AssignTemporaryCollectionAsync(coll, charaTarget.ObjectIndex).ConfigureAwait(false);
                await _ipcManager.Penumbra.SetTemporaryModsAsync(applicationId, coll, extractedFiles.Union(fileSwaps).ToDictionary(d => d.Key, d => d.Value, StringComparer.Ordinal)).ConfigureAwait(false);
                await _ipcManager.Penumbra.SetManipulationDataAsync(applicationId, coll, loadedCharaFile.CharaFileData.ManipulationData).ConfigureAwait(false);

                GameObjectHandler tempHandler = await _gameObjectHandlerFactory.Create(ObjectKind.Player,
                    () => _dalamudUtil.GetCharacterFromObjectTableByName(charaTarget.Name.ToString())?.Address ?? IntPtr.Zero, isWatched: false).ConfigureAwait(false);

                await _ipcManager.Glamourer.ApplyAllAsync(charaTarget, tempHandler, loadedCharaFile.CharaFileData.GlamourerData, applicationId, disposeCts.Token).ConfigureAwait(false);
                await _ipcManager.Penumbra.RedrawAsync(tempHandler, applicationId, disposeCts.Token).ConfigureAwait(false);
                _dalamudUtil.WaitWhileGposeCharacterIsDrawing(charaTarget.Address, 30000);
                if (!string.IsNullOrEmpty(loadedCharaFile.CharaFileData.CustomizePlusData))
                {
                    var id = await _ipcManager.CustomizePlus.SetBodyScaleAsync(tempHandler.Address, loadedCharaFile.CharaFileData.CustomizePlusData).ConfigureAwait(false);
                }
                else
                {
                    var id = await _ipcManager.CustomizePlus.SetBodyScaleAsync(tempHandler.Address, Convert.ToBase64String(Encoding.UTF8.GetBytes("{}"))).ConfigureAwait(false);
                }
                pastCollections[charaTarget.Name.TextValue] = new Tuple<Guid, Guid>(applicationId, coll);
            }
        }
        catch (Exception ex)
        {
            _Logger.Warning(ex, "Failure to read MCDF");
            OnMcdfFailed?.Invoke(this, new Tuple<IGameObject, long, MareCharaFileHeader>(charaTarget, expectedLength, loadedCharaFile));
        }
        finally
        {
            _Logger.Debug("Clearing local files");
            ////foreach (var file in Directory.EnumerateFiles(CachePath.CacheLocation, "*.tmp"))
            ////{
            ////    File.Delete(file);
            ////}
        }
        CurrentlyWorking = false;
    }

    public Tuple<long, MareCharaFileHeader> LoadMareCharaFile(string filePath)
    {
        CurrentlyWorking = true;
        try
        {
            using var unwrapped = File.OpenRead(filePath);
            using var lz4Stream = new LZ4Stream(unwrapped, LZ4StreamMode.Decompress, LZ4StreamFlags.HighCompression);
            using var reader = new BinaryReader(lz4Stream);
            var loadedCharaFile = MareCharaFileHeader.FromBinaryReader(filePath, reader);

            _Logger.Information("Read Mare Chara File");
            _Logger.Information("Version: {ver}", (loadedCharaFile?.Version ?? -1));
            long expectedLength = 0;
            if (loadedCharaFile != null)
            {
                _Logger.Debug("Data");
                foreach (var item in loadedCharaFile.CharaFileData.FileSwaps)
                {
                    foreach (var gamePath in item.GamePaths)
                    {
                        _Logger.Debug("Swap: {gamePath} => {fileSwapPath}", gamePath, item.FileSwapPath);
                    }
                }

                var itemNr = 0;
                foreach (var item in loadedCharaFile.CharaFileData.Files)
                {
                    itemNr++;
                    expectedLength += item.Length;
                    foreach (var gamePath in item.GamePaths)
                    {
                        _Logger.Debug("File {itemNr}: {gamePath} = {len}", itemNr, gamePath, item.Length.ToByteString());
                    }
                }

                _Logger.Information("Expected length: {expected}", expectedLength.ToByteString());
            }
            return new Tuple<long, MareCharaFileHeader>(expectedLength, loadedCharaFile);
        }
        finally { CurrentlyWorking = false; }
    }

    public void SaveMareCharaFile(string description, string filePath)
    {
        CurrentlyWorking = true;
        var tempFilePath = filePath + ".tmp";

        try
        {
            if (_characterData == null) return;

            var mareCharaFileData = _factory.Create(description, _characterData);
            MareCharaFileHeader output = new(MareCharaFileHeader.CurrentVersion, mareCharaFileData);

            using var fs = new FileStream(tempFilePath, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
            using var lz4 = new LZ4Stream(fs, LZ4StreamMode.Compress, LZ4StreamFlags.HighCompression);
            using var writer = new BinaryWriter(lz4);
            output.WriteToStream(writer);

            foreach (var item in output.CharaFileData.Files)
            {
                var file = _manager.GetFileCacheByHash(item.Hash)!;
                _Logger.Debug("Saving to MCDF: {hash}:{file}", item.Hash, file.ResolvedFilepath);
                _Logger.Debug("\tAssociated GamePaths:");
                foreach (var path in item.GamePaths)
                {
                    _Logger.Debug("\t{path}", path);
                }
                using var fsRead = File.OpenRead(file.ResolvedFilepath);
                using var br = new BinaryReader(fsRead);
                byte[] buffer = new byte[item.Length];
                br.Read(buffer, 0, item.Length);
                writer.Write(buffer);
            }
            writer.Flush();
            lz4.Flush();
            fs.Flush();
            fs.Close();
            File.Move(tempFilePath, filePath, true);
        }
        catch (Exception ex)
        {
            _Logger.Error(ex, "Failure Saving Mare Chara File, deleting output");
            File.Delete(tempFilePath);
        }
        finally { CurrentlyWorking = false; }
    }

    private Dictionary<string, string> ExtractFilesFromCharaFile(string fileId, MareCharaFileHeader charaFileHeader, BinaryReader reader, long expectedLength)
    {
        long totalRead = 0;
        Dictionary<string, string> gamePathToFilePath = new(StringComparer.Ordinal);
        foreach (var fileData in charaFileHeader.CharaFileData.Files)
        {
            var fileName = Path.Combine(McdfAccessUtils.CacheLocation, fileId + "_mcdf_" + _globalFileCounter++ + ".tmp");
            var length = fileData.Length;
            //if (!File.Exists(fileName))
            //{
            var bufferSize = length;
            using var fs = File.OpenWrite(fileName);
            using var wr = new BinaryWriter(fs);
            _Logger.Debug("Reading {length} of {fileName}", length.ToByteString(), fileName);
            var buffer = reader.ReadBytes(bufferSize);
            wr.Write(buffer);
            wr.Flush();
            wr.Close();
            if (buffer.Length == 0) throw new EndOfStreamException("Unexpected EOF");
            foreach (var path in fileData.GamePaths)
            {
                gamePathToFilePath[path] = fileName;
                _Logger.Debug("{path} => {fileName} [{hash}]", path, fileName, fileData.Hash);
            }
            //}
            totalRead += length;
            _Logger.Debug("Read {read}/{expected} bytes", totalRead.ToByteString(), expectedLength.ToByteString());
        }

        return gamePathToFilePath;
    }
}