using LibBSP;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BSPConvert.Lib.Vis
{
	// How the visibility of maps compiled without it is computed
	public enum VisMode
	{
		// Full, unless the fast visibility shows the full one would take vvis too long
		Auto,
		// vvis's portal flow: the least drawn, but it takes hours on maps with large open areas
		Full,
		// vvis -fast: whatever the portals could possibly see through each other, so more is drawn, but it's quick
		Fast,
		// None, so the whole map is drawn everywhere
		None,
	}

	// The visibility lump vvis computed, and how many clusters it found the clusters see in all
	public readonly record struct VvisResult(byte[] VisData, long VisibleClusters);

	// Computes a BSP's PVS with Momentum Mod's vvis, from the portals between its clusters, on a copy of the BSP in a
	// work folder deleted on Dispose
	public sealed class VvisCompiler : IDisposable
	{
		private readonly string vvisPath;
		private readonly ILogger logger;
		private readonly ProgressTracker progress;
		private readonly string workDir;
		private readonly string bspPath;

		public VvisCompiler(string vvisPath, ILogger logger, ProgressTracker progress, SourceBspBuilder builder, string mapName,
			int numClusters, IReadOnlyList<ClusterPortal> portals)
		{
			this.vvisPath = vvisPath;
			this.logger = logger;
			this.progress = progress;

			workDir = Path.Combine(Path.GetTempPath(), "BSPConvert_vis_" + Path.GetRandomFileName());
			Directory.CreateDirectory(workDir);

			// vvis's file system needs a game to start
			File.WriteAllText(Path.Combine(workDir, "gameinfo.txt"),
				"\"GameInfo\"\n{\n\tgame \"BSPConvert\"\n\tFileSystem\n\t{\n\t\tSearchPaths\n\t\t{\n\t\t\tgame |gameinfo_path|.\n\t\t}\n\t}\n}\n");

			bspPath = Path.Combine(workDir, mapName + ".bsp");
			builder.Write(bspPath, false);
			TreePortals.WritePortalFile(Path.ChangeExtension(bspPath, ".prt"), numClusters, portals);
		}

		// Runs vvis, or returns null if it failed or took longer than timeLimit. Its passes are reported as the
		// current stage's items from firstItem on, out of totalItems: 10 for each pass, and the full visibility has two.
		public VvisResult? Run(bool fast, TimeSpan? timeLimit, int firstItem, int totalItems)
		{
			var startInfo = new ProcessStartInfo(vvisPath)
			{
				WorkingDirectory = workDir,
				UseShellExecute = false,
				CreateNoWindow = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};
			if (fast)
				startInfo.ArgumentList.Add("-fast");
			startInfo.ArgumentList.Add("-low");
			startInfo.ArgumentList.Add("-game");
			startInfo.ArgumentList.Add(workDir);
			startInfo.ArgumentList.Add(bspPath);

			using var process = Process.Start(startInfo);
			if (process == null)
			{
				logger.Log($"Warning: Couldn't start {vvisPath}");
				return null;
			}

			var output = new StringBuilder();
			var ticks = 0;
			var stdout = process.StandardOutput;
			var readTask = Task.Run(() =>
			{
				// vvis counts through each pass as "0...1...2...3...4...5...6...7...8...9...10"
				int c;
				while ((c = stdout.Read()) >= 0)
				{
					lock (output)
					{
						output.Append((char)c);
						var length = output.Length;
						if (length >= 4 && output[length - 1] == '.' && output[length - 2] == '.' && output[length - 3] == '.' && char.IsDigit(output[length - 4]))
							ticks++;
					}
				}
			});
			var errorTask = process.StandardError.ReadToEndAsync();

			var timer = Stopwatch.StartNew();
			try
			{
				while (!process.WaitForExit(250))
				{
					if (timer.Elapsed > timeLimit)
					{
						process.Kill(true);
						process.WaitForExit();
						return null;
					}

					int done;
					lock (output)
						done = ticks;
					progress.Item(Math.Min(firstItem + done, totalItems - 1), totalItems);
				}
			}
			catch (OperationCanceledException)
			{
				process.Kill(true);
				throw;
			}

			readTask.Wait();
			var text = output + errorTask.Result;
			if (process.ExitCode != 0)
			{
				var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
				var errorLines = lines.Where(line => line.Contains("Error", StringComparison.OrdinalIgnoreCase)).ToList();
				logger.Log($"Warning: vvis failed: {string.Join(" ", errorLines.Count > 0 ? errorLines : lines.TakeLast(3))}");
				return null;
			}

			var visible = Regex.Match(text, @"Total clusters visible: (\d+)");
			return new VvisResult(new BSP(new FileInfo(bspPath)).Visibility.Data, visible.Success ? long.Parse(visible.Groups[1].Value, CultureInfo.InvariantCulture) : 0);
		}

		public void Dispose()
		{
			try
			{
				Directory.Delete(workDir, true);
			}
			catch (IOException)
			{
			}
			catch (UnauthorizedAccessException)
			{
			}
		}
	}
}
