using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConvert.Lib
{
	public static class FileUtil
	{
		/// <summary>
		/// Moves a file and creates the destination directory if it doesn't exist.
		/// </summary>
		public static void MoveFile(string sourceFileName, string destFileName)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(destFileName));
			File.Move(sourceFileName, destFileName, true);
		}

		/// <summary>
		/// Copies a file and creates the destination directory if it doesn't exist.
		/// </summary>
		public static void CopyFile(string sourceFileName, string destFileName)
		{
			Directory.CreateDirectory(Path.GetDirectoryName(destFileName));
			File.Copy(sourceFileName, destFileName, true);
		}

		/// <summary>
		/// Copies a bundled asset shipped under the app's Assets/materials folder to destDir, preserving its
		/// relative path. Used to drop pre-made tools VMT/VTFs (invisible displacement, env-map placeholder) into
		/// the output. Warns and does nothing if the asset is missing.
		/// </summary>
		public static void CopyBuiltinMaterialAsset(string relativePath, string destDir)
		{
			var sourcePath = Path.Combine(AppContext.BaseDirectory, "Assets", "materials", relativePath);
			if (!File.Exists(sourcePath))
			{
				Console.WriteLine($"Missing bundled material asset: {sourcePath}");
				return;
			}

			CopyFile(sourcePath, Path.Combine(destDir, relativePath));
		}

		/// <summary>
		/// Deserializes a file using the specified deserialization function.
		/// </summary>
		public static T DeserializeFromFile<T>(string path, Func<BinaryReader, T> deserializeFunc)
		{
			using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
			using (var reader = new BinaryReader(stream))
			{
				return deserializeFunc(reader);
			}
		}
	}
}
