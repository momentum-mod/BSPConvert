using LibBSP;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConvert.Lib
{
	public class ContentManager : IDisposable
	{
		private string contentDir;
		public string ContentDir
		{
			get { return contentDir; }
		}

		private BSP[] bspFiles;
		public BSP[] BSPFiles
		{
			get { return bspFiles; }
		}
		
		private static string Q3CONTENT_FOLDER = "Q3Content";

		public ContentManager(string inputFile)
		{
			if (!File.Exists(inputFile))
				throw new FileNotFoundException(inputFile);

			CreateContentDir(inputFile);
			LoadBSPFiles(inputFile);
		}

		// Create a temp directory used for converting assets across engines
		private void CreateContentDir(string inputFile)
		{
			var fileName = Path.GetFileNameWithoutExtension(inputFile);
			contentDir = Path.Combine(Path.GetTempPath(), fileName);

			// Delete any pre-existing temp content directory
			if (Directory.Exists(contentDir))
				Directory.Delete(contentDir, true);

			Directory.CreateDirectory(contentDir);
		}

		private void LoadBSPFiles(string inputFile)
		{
			var ext = Path.GetExtension(inputFile);
			if (ext == ".bsp")
				bspFiles = new BSP[] { new BSP(new FileInfo(inputFile)) };
			else if (ext == ".pk3")
			{
				// Extract bsp's from pk3 archive
				ZipFile.ExtractToDirectory(inputFile, contentDir);

				var files = Directory.GetFiles(ContentDir, "*.bsp", SearchOption.AllDirectories);
				bspFiles = new BSP[files.Length];
				for (var i = 0; i < files.Length; i++)
					bspFiles[i] = new BSP(new FileInfo(files[i]));
			}
			else
				throw new Exception("Invalid input file extension: " + ext);
		}

		public static string GetQ3ContentDir()
		{
			return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, Q3CONTENT_FOLDER);
		}

		public void Dispose()
		{
			// Delete temp content directory
			if (Directory.Exists(contentDir))
				Directory.Delete(contentDir, true);
		}
	}
}
