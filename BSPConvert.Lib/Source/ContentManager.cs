using LibBSP;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConvert.Lib
{
	// Loads the input file's BSP(s) and owns the temp content directory conversion works in. Archive inputs (pk3)
	// are extracted there, and converted assets are staged there before being embedded or exported.
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
			var ext = Path.GetExtension(inputFile).ToLowerInvariant();
			if (ext == ".bsp")
				bspFiles = new BSP[] { new BSP(new FileInfo(inputFile)) };
			else if (ext == ".pk3" || ext == ".zip")
			{
				// Extract bsp's from the archive. Quake 3 pk3s are zips, and GoldSrc map downloads are usually zips laid
				// out like the mod directory (maps/, gfx/env/, sound/, ...).
				ZipFile.ExtractToDirectory(inputFile, contentDir);

				var files = Directory.GetFiles(ContentDir, "*.bsp", SearchOption.AllDirectories);
				bspFiles = new BSP[files.Length];
				for (var i = 0; i < files.Length; i++)
					bspFiles[i] = new BSP(new FileInfo(files[i]));
			}
			else
				throw new Exception("Invalid input file extension: " + ext);
		}

		public void Dispose()
		{
			// Delete temp content directory
			if (Directory.Exists(contentDir))
				Directory.Delete(contentDir, true);
		}
	}
}
