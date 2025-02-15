namespace BSPConvert.Lib.Source;
using LibBSP;
using SharpCompress.Archives;

public class SoundConverter
	{
		private readonly string pk3Dir;
		private readonly BSP bsp;
		private readonly string outputDir;
		private readonly Entities sourceEntities;

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
        List<string> customSounds = FindCustomSounds();
			if (customSounds.Count == 0)
				return;

			foreach (string sound in customSounds)
				MoveToPk3SoundDir(sound);

        string[] soundFiles = Directory.GetFiles(pk3Dir, "*.wav", SearchOption.AllDirectories);
			FixSoundPaths(soundFiles);

			if (bsp != null)
				EmbedFiles(soundFiles);
			else
				MoveFilesToOutputDir(soundFiles);
		}

		private List<string> FindCustomSounds()
		{
			var soundHashSet = new HashSet<string>();
			foreach (Entity? entity in sourceEntities)
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
        string q3ContentDir = ContentManager.GetQ3ContentDir();
        string soundPath = Path.Combine(q3ContentDir, "sound", sound);
			if (!File.Exists(soundPath))
				return;

        string newPath = Path.Combine(pk3Dir, "sound", sound);
			Directory.CreateDirectory(Path.GetDirectoryName(newPath));

			File.Copy(soundPath, newPath, true);
		}

		// Move sound files that are not in the "sound" folder (music, custom sounds)
		private void FixSoundPaths(string[] soundFiles)
		{
			for (int i = 0; i < soundFiles.Length; i++)
			{
            string file = soundFiles[i];
            string relativePath = file.Replace(pk3Dir + Path.DirectorySeparatorChar, "");
				if (!relativePath.StartsWith("sound" + Path.DirectorySeparatorChar)) // Sound file is not in "sound" folder
				{
                string newPath = Path.Combine(pk3Dir, "sound", relativePath);
					Directory.CreateDirectory(Path.GetDirectoryName(newPath));

					File.Move(file, newPath, true);
					soundFiles[i] = newPath;
				}
			}
		}

		private void EmbedFiles(string[] soundFiles)
		{
			using (SharpCompress.Archives.Zip.ZipArchive archive = bsp.PakFile.GetZipArchive())
			{
				foreach (string file in soundFiles)
				{
                string newPath = file.Replace(pk3Dir + Path.DirectorySeparatorChar, "");
					archive.AddEntry(newPath, new FileInfo(file));
				}

				bsp.PakFile.SetZipArchive(archive, true);
			}
		}

		private void MoveFilesToOutputDir(string[] soundFiles)
		{
			foreach (string file in soundFiles)
			{
            string newPath = file.Replace(pk3Dir, outputDir);
				FileUtil.MoveFile(file, newPath);
			}
		}
	}
