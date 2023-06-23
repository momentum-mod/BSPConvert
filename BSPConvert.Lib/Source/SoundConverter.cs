using LibBSP;
using SharpCompress.Archives;

namespace BSPConvert.Lib.Source
{
	public class SoundConverter
	{
		private string pk3Dir;
		private BSP bsp;
		private string outputDir;
		private Entities sourceEntities;

		public SoundConverter(string pk3Dir, BSP bsp, Entities sourceEntities)
		{
			this.pk3Dir = pk3Dir;
			this.bsp = bsp;
			this.sourceEntities = sourceEntities;
		}

		public SoundConverter(string pk3Dir, string outputDir, Entities sourceEntities)
		{
			this.pk3Dir = pk3Dir;
			this.outputDir = outputDir;
			this.sourceEntities = sourceEntities;
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
				}
			}

			return soundHashSet.ToList();
		}

		private void MoveToPk3SoundDir(string sound)
		{
			var q3ContentDir = ContentManager.GetQ3ContentDir();
			var soundPath = Path.Combine(q3ContentDir, "sound", sound);
			if (!File.Exists(soundPath))
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
				var relativePath = file.Replace(pk3Dir + Path.DirectorySeparatorChar, "");
				if (!relativePath.StartsWith("sound" + Path.DirectorySeparatorChar)) // Sound file is not in "sound" folder
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
					var newPath = file.Replace(pk3Dir + Path.DirectorySeparatorChar, "");
					archive.AddEntry(newPath, new FileInfo(file));
				}

				bsp.PakFile.SetZipArchive(archive, true);
			}
		}

		private void MoveFilesToOutputDir(string[] soundFiles)
		{
			foreach (var file in soundFiles)
			{
				var newPath = file.Replace(pk3Dir, outputDir);
				FileUtil.MoveFile(file, newPath);
			}
		}
	}
}
