using LibBSP;
using SharpCompress.Archives;

namespace BSPConvert.Lib.Source
{
	public class SoundConverter
	{
		private string pk3Dir;
		private AssetSearchPath externalContent;
		private BSP bsp;
		private string outputDir;
		private Entities sourceEntities;
		private ILogger logger;

		// externalContent is searched for sounds the map's entities use that aren't bundled in pk3Dir
		public SoundConverter(string pk3Dir, AssetSearchPath externalContent, BSP bsp, Entities sourceEntities, ILogger logger)
		{
			this.pk3Dir = pk3Dir;
			this.externalContent = externalContent;
			this.bsp = bsp;
			this.sourceEntities = sourceEntities;
			this.logger = logger;
		}

		public SoundConverter(string pk3Dir, AssetSearchPath externalContent, string outputDir, Entities sourceEntities, ILogger logger)
		{
			this.pk3Dir = pk3Dir;
			this.externalContent = externalContent;
			this.outputDir = outputDir;
			this.sourceEntities = sourceEntities;
			this.logger = logger;
		}

		public void Convert()
		{
			var customSounds = FindCustomSounds();
			if (!customSounds.Any())
				return;

			foreach (var sound in customSounds)
				MoveToPk3SoundDir(sound);

			var soundFiles = Directory.GetFiles(pk3Dir, "*.wav", SearchOption.AllDirectories);
			FixSoundPaths(soundFiles);
			LogFoundSounds(customSounds, soundFiles);

			if (bsp != null)
				EmbedFiles(soundFiles);
			else
				MoveFilesToOutputDir(soundFiles);
		}

		private List<string> FindCustomSounds()
		{
			var soundHashSet = new HashSet<string>();
			foreach (var entity in sourceEntities)
			{
				switch (entity.ClassName)
				{
					case "trigger_jumppad":
						soundHashSet.Add(entity["launchsound"].Replace('/', Path.DirectorySeparatorChar));
						break;
					case "func_button":
						soundHashSet.Add(entity["customsound"].Replace('/', Path.DirectorySeparatorChar));
						break;
					case "ambient_generic":
						soundHashSet.Add(entity["message"].Replace('/', Path.DirectorySeparatorChar));
						break;
					case "func_door":
						soundHashSet.Add(entity["noise1"].Replace('/', Path.DirectorySeparatorChar));
						soundHashSet.Add(entity["noise2"].Replace('/', Path.DirectorySeparatorChar));
						break;
				}
			}

			// Doors without sounds and so on leave their key empty
			soundHashSet.RemoveWhere(string.IsNullOrWhiteSpace);
			return soundHashSet.ToList();
		}

		// Logs how many of the sounds entities play were found, and warns about the ones neither the map nor the
		// external content has. Sounds starting with * are the player model's, which the game finds itself.
		private void LogFoundSounds(List<string> customSounds, string[] soundFiles)
		{
			var soundDir = Path.Combine(pk3Dir, "sound");
			var files = new HashSet<string>(soundFiles.Select(file => Path.GetRelativePath(soundDir, file)), StringComparer.OrdinalIgnoreCase);
			var sounds = customSounds.Where(sound => !sound.StartsWith('*')).ToList();
			var missing = sounds.Where(sound => !files.Contains(sound)).Order(StringComparer.OrdinalIgnoreCase).ToList();
			if (sounds.Count > 0)
				logger.Log($"Found {sounds.Count - missing.Count}/{sounds.Count} sounds");
			if (missing.Count > 0)
				logger.Log($"Warning: Sounds not found (add them to the CustomContent folder): {string.Join(", ", missing.Select(sound => sound.Replace(Path.DirectorySeparatorChar, '/')))}");
		}

		private void MoveToPk3SoundDir(string sound)
		{
			var soundPath = externalContent.FindFile(Path.Combine("sound", sound));
			if (soundPath == null)
				return;

			var newPath = Path.Combine(pk3Dir, "sound", sound);
			Directory.CreateDirectory(Path.GetDirectoryName(newPath));

			File.Copy(soundPath, newPath, true);
		}

		// Move sound files that are not in the "sound" folder (music, custom sounds)
		private void FixSoundPaths(string[] soundFiles)
		{
			for (var i = 0; i < soundFiles.Length; i++)
			{
				var file = soundFiles[i];
				var relativePath = Path.GetRelativePath(pk3Dir, file);
				if (!relativePath.StartsWith("sound" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) // Sound file is not in "sound" folder
				{
					var newPath = Path.Combine(pk3Dir, "sound", relativePath);
					Directory.CreateDirectory(Path.GetDirectoryName(newPath));

					File.Move(file, newPath, true);
					soundFiles[i] = newPath;
				}
			}
		}

		private void EmbedFiles(string[] soundFiles)
		{
			using (var archive = bsp.PakFile.GetZipArchive())
			{
				foreach (var file in soundFiles)
				{
					var newPath = Path.GetRelativePath(pk3Dir, file);
					archive.AddEntry(newPath, new FileInfo(file));
				}

				bsp.PakFile.SetZipArchive(archive, true);
			}
		}

		private void MoveFilesToOutputDir(string[] soundFiles)
		{
			foreach (var file in soundFiles)
			{
				var relativePath = Path.GetRelativePath(pk3Dir, file);
                var newPath = Path.Combine(outputDir, relativePath);
                FileUtil.MoveFile(file, newPath);
			}
		}
	}
}
