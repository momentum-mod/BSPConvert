using LibBSP;
using SharpCompress.Archives;

namespace BSPConvert.Lib.Source
{
	public class SoundConverter
	{
		private string pk3SoundDir;
		private BSP bsp;
		private string outputDir;
		private Entities sourceEntities;

		public SoundConverter(string pk3Dir, BSP bsp, Entities sourceEntities)
		{
			pk3SoundDir = Path.Combine(pk3Dir, "sound");
			this.bsp = bsp;
			this.sourceEntities = sourceEntities;
		}

		public SoundConverter(string pk3Dir, string outputDir, Entities sourceEntities)
		{
			pk3SoundDir = Path.Combine(pk3Dir, "sound");
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

			var soundFiles = Directory.GetFiles(pk3SoundDir, "*.wav", SearchOption.AllDirectories);
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
				}
			}

			return soundHashSet.ToList();
		}

		private void MoveToPk3SoundDir(string sound)
		{
			var q3ContentDir = ContentManager.GetQ3ContentDir();
			var launchSoundPath = Path.Combine(q3ContentDir, "sound", sound);
			
			var newPath = Path.Combine(pk3SoundDir, sound);
			Directory.CreateDirectory(Path.GetDirectoryName(newPath));

			File.Copy(launchSoundPath, newPath, true);
		}

		private void EmbedFiles(string[] soundFiles)
		{
			var archive = bsp.PakFile.GetZipArchive();
			foreach (var file in soundFiles)
			{
				var newPath = file.Replace(pk3SoundDir, "sound");
				archive.AddEntry(newPath, new FileInfo(file));
			}

			bsp.PakFile.SetZipArchive(archive, true);
		}

		private void MoveFilesToOutputDir(string[] soundFiles)
		{
			foreach (var file in soundFiles)
			{
				var outputSoundDir = Path.Combine(outputDir, "sound");
				var newPath = file.Replace(pk3SoundDir, outputSoundDir);
				FileUtil.MoveFile(file, newPath);
			}
		}
	}
}
