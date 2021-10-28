﻿using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reactive;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using DTOs;
using GameFinder.StoreHandlers.Steam;
using MessageBox.Avalonia;
using Octodiff.Core;
using Octodiff.Diagnostics;
using ReactiveUI;
using ReactiveUI.Fody.Helpers;
using Wabbajack.DTOs;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Patcher.ViewModels
{
    public class MainWindowViewModel : ViewModelBase, IActivatableViewModel
    {
        public ViewModelActivator Activator { get; }
        
        [Reactive]
        public AbsolutePath GamePath { get; set; }
        
        [Reactive]
        public ReactiveCommand<Unit, Unit> StartPatching { get; set; }

        [Reactive] public string[] LogLines { get; set; } = Array.Empty<string>();


        private void Log(string line)
        {
            LogLines = LogLines.Append(line).ToArray();
        }
        
        public MainWindowViewModel()
        {
            Activator = new ViewModelActivator();
            
            var tsk = LocateAndSetGame(Game.SkyrimSpecialEdition);
            StartPatching = ReactiveCommand.CreateFromTask(() => Start());
        }

        public async Task LocateAndSetGame(Game game)
        {
            try
            {
                Log($"Looking for {game.MetaData().HumanFriendlyGameName}");
                var handler = new SteamHandler();
                handler.FindAllGames();

                var steamGame = handler.Games.First(g => game.MetaData().SteamIDs.Contains(g.ID));

                var path = steamGame.Path.ToAbsolutePath();
                GamePath = path;
                Log($"Found at {path}");
            }
            catch (Exception ex)
            {
                var msg = MessageBoxManager.GetMessageBoxStandardWindow("Error",
                    $"Couldn't locate {game.MetaData().HumanFriendlyGameName} via Steam, exiting");
                await msg.Show();
                Environment.Exit(1);
                return;
            }
        }


        private async Task Start()
        {
            try
            {
                Log("Running instructions");
                var zipStream =
                    typeof(MainWindowViewModel).Assembly.GetManifestResourceStream("Patcher.Resources.patch.zip");

                using var archive = new ZipArchive(zipStream!, ZipArchiveMode.Read);
                var instructions =
                    await JsonSerializer.DeserializeAsync<Instruction[]>(archive.GetEntry("instructions.json")!.Open());

                foreach (var file in instructions)
                {
                    var fullPath = file.Path.ToRelativePath().RelativeTo(GamePath);
                    switch (file.Method)
                    {
                        case ResultType.Identical:
                            break;
                        case ResultType.Deleted:
                            if (fullPath.FileExists())
                            {
                                Log($"Deleting {file.Path}");
                                fullPath.Delete();
                            }
                            else
                            {
                                Log($"File already deleted {file.Path}");
                            }

                            break;
                        case ResultType.Patched:
                            await PatchFile(file, fullPath, archive);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }

                Log("Finished Patching, enjoy your game!");
            }
            catch (Exception ex)
            {
                Log($"Error! {ex.Message}");
                Log(ex.ToString());
            }

        }

        private async Task PatchFile(Instruction file, AbsolutePath fullPath, ZipArchive archive)
        {
            Log($"Patching {file.Path}");
            var oldData = await fullPath.ReadAllBytesAsync();
            var oldHash = await oldData.Hash();
            if (oldHash != Hash.FromLong(file.SrcHash))
            {
                if (oldHash != Hash.FromLong(file.DestHash))
                {
                    throw new Exception("Can't patch file, it doesn't match the expected format");
                }
                else
                {
                    Log($"Already patched {file.Path}");
                    return;
                }
            }
            Log("Pre-check passed, patching file");

            var patchEntry = archive.GetEntry(file.PatchFile);
            await using var ed = patchEntry!.Open();
            var ms = new MemoryStream();
            await ed.CopyToAsync(ms);
            ms.Position = 0;

            var deltaApplier = new DeltaApplier();
            var os = new MemoryStream();
            deltaApplier.Apply(new MemoryStream(oldData), new BinaryDeltaReader(ms, new NullProgressReporter()), os);

            Log("Verifying file");
            os.Position = 0;
            var finalHash = await os.HashingCopy(Stream.Null, CancellationToken.None);

            if (finalHash != Hash.FromLong(file.DestHash))
                throw new Exception("Not patching file, result was not valid!");
            
            Log("File verifed, writing file");

            await fullPath.WriteAllBytesAsync(os.ToArray());
            
            Log("File patched");
        }
    }
}
