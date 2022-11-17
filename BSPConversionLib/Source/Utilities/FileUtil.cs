using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib
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
	}
}
