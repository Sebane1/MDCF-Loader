﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MareSynchronos.Managers;
using MareSynchronos.Utils;
using MareSynchronos.WebAPI;
using Microsoft.EntityFrameworkCore;

namespace MareSynchronos.FileCacheDB;

public class PeriodicFileScanner : IDisposable
{
    private readonly IpcManager _ipcManager;
    private readonly Configuration _pluginConfiguration;
    private readonly FileDbManager _fileDbManager;
    private readonly ApiController _apiController;
    private CancellationTokenSource? _scanCancellationTokenSource;
    private Task? _fileScannerTask = null;
    public PeriodicFileScanner(IpcManager ipcManager, Configuration pluginConfiguration, FileDbManager fileDbManager, ApiController apiController)
    {
        Logger.Verbose("Creating " + nameof(PeriodicFileScanner));

        _ipcManager = ipcManager;
        _pluginConfiguration = pluginConfiguration;
        _fileDbManager = fileDbManager;
        _apiController = apiController;
        _ipcManager.PenumbraInitialized += StartScan;
        if (!string.IsNullOrEmpty(_ipcManager.PenumbraModDirectory()))
        {
            StartScan();
        }
        _apiController.DownloadStarted += _apiController_DownloadStarted;
        _apiController.DownloadFinished += _apiController_DownloadFinished;
    }

    private void _apiController_DownloadFinished()
    {
        if (fileScanWasRunning)
        {
            fileScanWasRunning = false;
            InvokeScan(true);
        }
    }

    private void _apiController_DownloadStarted()
    {
        if (IsScanRunning)
        {
            _scanCancellationTokenSource?.Cancel();
            fileScanWasRunning = true;
        }
    }

    private bool fileScanWasRunning = false;
    private long currentFileProgress = 0;
    public long CurrentFileProgress => currentFileProgress;

    public long FileCacheSize { get; set; }

    public bool IsScanRunning => CurrentFileProgress > 0 || TotalFiles > 0;

    public long TotalFiles { get; private set; }

    public string TimeUntilNextScan => _timeUntilNextScan.ToString(@"mm\:ss");
    private TimeSpan _timeUntilNextScan = TimeSpan.Zero;
    private int timeBetweenScans => _pluginConfiguration.TimeSpanBetweenScansInSeconds;

    public void Dispose()
    {
        Logger.Verbose("Disposing " + nameof(PeriodicFileScanner));

        _ipcManager.PenumbraInitialized -= StartScan;
        _apiController.DownloadStarted -= _apiController_DownloadStarted;
        _apiController.DownloadFinished -= _apiController_DownloadFinished;
        _scanCancellationTokenSource?.Cancel();
    }

    public void InvokeScan(bool forced = false)
    {
        bool isForced = forced;
        TotalFiles = 0;
        currentFileProgress = 0;
        _scanCancellationTokenSource?.Cancel();
        _scanCancellationTokenSource = new CancellationTokenSource();
        var token = _scanCancellationTokenSource.Token;
        _fileScannerTask = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                isForced |= RecalculateFileCacheSize();
                if (!_pluginConfiguration.FileScanPaused || isForced)
                {
                    isForced = false;
                    TotalFiles = 0;
                    currentFileProgress = 0;
                    PeriodicFileScan(token);
                }
                _timeUntilNextScan = TimeSpan.FromSeconds(timeBetweenScans);
                while (_timeUntilNextScan.TotalSeconds >= 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                    _timeUntilNextScan -= TimeSpan.FromSeconds(1);
                }
            }
        }, token);
    }

    internal void StartWatchers()
    {
        InvokeScan();
    }

    public bool RecalculateFileCacheSize()
    {
        FileCacheSize = Directory.EnumerateFiles(_pluginConfiguration.CacheFolder).Sum(f =>
        {
            try
            {
                return new FileInfo(f).Length;
            }
            catch
            {
                return 0;
            }
        });

        if (FileCacheSize < (long)_pluginConfiguration.MaxLocalCacheInGiB * 1024 * 1024 * 1024) return false;

        var allFiles = Directory.EnumerateFiles(_pluginConfiguration.CacheFolder)
            .Select(f => new FileInfo(f)).OrderBy(f => f.LastAccessTime).ToList();
        while (FileCacheSize > (long)_pluginConfiguration.MaxLocalCacheInGiB * 1024 * 1024 * 1024)
        {
            var oldestFile = allFiles.First();
            FileCacheSize -= oldestFile.Length;
            File.Delete(oldestFile.FullName);
            allFiles.Remove(oldestFile);
        }

        return true;
    }

    private void PeriodicFileScan(CancellationToken ct)
    {
        TotalFiles = 1;
        var penumbraDir = _ipcManager.PenumbraModDirectory();
        bool penDirExists = true;
        bool cacheDirExists = true;
        if (string.IsNullOrEmpty(penumbraDir) || !Directory.Exists(penumbraDir))
        {
            penDirExists = false;
            Logger.Warn("Penumbra directory is not set or does not exist.");
        }
        if (string.IsNullOrEmpty(_pluginConfiguration.CacheFolder) || !Directory.Exists(_pluginConfiguration.CacheFolder))
        {
            cacheDirExists = false;
            Logger.Warn("Mare Cache directory is not set or does not exist.");
        }
        if (!penDirExists || !cacheDirExists)
        {
            return;
        }

        Logger.Debug("Getting files from " + penumbraDir + " and " + _pluginConfiguration.CacheFolder);
        string[] ext = { ".mdl", ".tex", ".mtrl", ".tmb", ".pap", ".avfx", ".atex", ".sklb", ".eid", ".phyb", ".scd", ".skp" };

        var scannedFiles = Directory.EnumerateFiles(penumbraDir, "*.*", SearchOption.AllDirectories)
                            .Select(s => s.ToLowerInvariant())
                            .Where(f => ext.Any(e => f.EndsWith(e)) && !f.Contains(@"\bg\") && !f.Contains(@"\bgcommon\") && !f.Contains(@"\ui\"))
                            .Concat(Directory.EnumerateFiles(_pluginConfiguration.CacheFolder, "*.*", SearchOption.TopDirectoryOnly)
                                .Where(f => new FileInfo(f).Name.Length == 40)
                                .Select(s => s.ToLowerInvariant()).ToList())
                            .ToDictionary(c => c, c => false);

        TotalFiles = scannedFiles.Count;

        // scan files from database
        var cpuCount = (int)(Environment.ProcessorCount / 2.0f);
        Task[] dbTasks = Enumerable.Range(0, cpuCount).Select(c => Task.CompletedTask).ToArray();

        ConcurrentBag<Tuple<string, string>> entitiesToRemove = new();
        try
        {
            using var ctx = new FileCacheContext();
            Logger.Debug("Database contains " + ctx.FileCaches.Count() + " files, local system contains " + TotalFiles);
            using var cmd = ctx.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT Hash, FilePath, LastModifiedDate FROM FileCache";
            ctx.Database.OpenConnection();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var hash = reader["Hash"].ToString();
                var filePath = reader["FilePath"].ToString();
                var date = reader["LastModifiedDate"].ToString();
                var idx = Task.WaitAny(dbTasks, ct);
                dbTasks[idx] = Task.Run(() =>
                {
                    try
                    {
                        var file = _fileDbManager.ValidateFileCacheEntity(hash, filePath, date);
                        if (file != null)
                        {
                            scannedFiles[file.Filepath] = true;
                        }
                        else
                        {
                            entitiesToRemove.Add(new Tuple<string, string>(hash, filePath));
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn("Failed validating " + filePath);
                        Logger.Warn(ex.Message);
                        Logger.Warn(ex.StackTrace);
                        entitiesToRemove.Add(new Tuple<string, string>(hash, filePath));
                    }

                    Interlocked.Increment(ref currentFileProgress);
                    Thread.Sleep(1);
                }, ct);

                if (ct.IsCancellationRequested) return;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Error during enumerating FileCaches: " + ex.Message);
        }

        using (var db = new FileCacheContext())
        {
            try
            {
                if (entitiesToRemove.Any())
                {
                    foreach (var entry in entitiesToRemove)
                    {
                        Logger.Debug("Removing " + entry.Item2);
                        var toRemove = db.FileCaches.First(f => f.Filepath == entry.Item2 && f.Hash == entry.Item1);
                        db.FileCaches.Remove(toRemove);
                    }

                    db.SaveChanges();
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex.Message);
            }
        }

        Task.WaitAll(dbTasks);

        Logger.Debug("Scanner validated existing db files");

        if (ct.IsCancellationRequested) return;

        // scan new files
        foreach (var c in scannedFiles.Where(c => c.Value == false))
        {
            var idx = Task.WaitAny(dbTasks, ct);
            dbTasks[idx] = Task.Run(() =>
            {
                try
                {
                    var entry = _fileDbManager.CreateFileEntry(c.Key);
                    if (entry == null) _ = _fileDbManager.CreateCacheEntry(c.Key);
                }
                catch (Exception ex)
                {
                    Logger.Warn("Failed adding " + c.Key);
                    Logger.Warn(ex.Message);
                }

                Interlocked.Increment(ref currentFileProgress);
                Thread.Sleep(1);
            }, ct);

            if (ct.IsCancellationRequested) return;
        }

        Task.WaitAll(dbTasks);

        Logger.Debug("Scanner added new files to db");

        Logger.Debug("Scan complete");
        TotalFiles = 0;
        currentFileProgress = 0;
        entitiesToRemove.Clear();
        scannedFiles.Clear();
        dbTasks = Array.Empty<Task>();

        if (!_pluginConfiguration.InitialScanComplete)
        {
            _pluginConfiguration.InitialScanComplete = true;
            _pluginConfiguration.Save();
        }
    }

    private void StartScan()
    {
        if (!_ipcManager.Initialized || !_pluginConfiguration.HasValidSetup()) return;
        Logger.Verbose("Penumbra is active, configuration is valid, starting watchers and scan");
        InvokeScan(true);
    }
}