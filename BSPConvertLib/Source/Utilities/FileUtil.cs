using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConvertLib
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
