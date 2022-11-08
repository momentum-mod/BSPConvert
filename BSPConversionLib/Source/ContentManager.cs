using LibBSP;
using System;
using System.Collections.Generic;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace BSPConversionLib
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
			LoadQ3Content();
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

		// Copies content from base Q3 content into temp content directory
		private void LoadQ3Content()
		{
			var q3ContentDict = GetQ3ContentDictionary();
			CopyMissingQ3Content(q3ContentDict);
		}

		// Create a dictionary that maps texture names to the file paths in the Q3Content folder
		private Dictionary<string, string> GetQ3ContentDictionary()
		{
			var q3ContentTextures = new Dictionary<string, string>();
			
			var q3ContentDir = GetQ3ContentDir();
			foreach (var file in Directory.GetFiles(q3ContentDir, "*.*", SearchOption.AllDirectories))
			{
				var ext = Path.GetExtension(file);
				if (ext == ".tga" || ext == ".jpg")
				{
					var texturePath = file.Replace(q3ContentDir + Path.DirectorySeparatorChar, "")
						.Replace(Path.DirectorySeparatorChar, '/').Replace(ext, "");
					q3ContentTextures.Add(texturePath, file);
				}
			}

			return q3ContentTextures;
		}

		private void CopyMissingQ3Content(Dictionary<string, string> q3ContentDict)
		{
			var q3ContentDir = GetQ3ContentDir();
			foreach (var bsp in BSPFiles)
			{
				foreach (var texture in bsp.Textures)
				{
					if (q3ContentDict.TryGetValue(texture.Name, out var texturePath))
					{
						// Copy q3 content textures into pk3Dir
						// TODO: It would be more efficient to run the textures through VTFLib without copying
						var newPath = texturePath.Replace(q3ContentDir, contentDir);

						Directory.CreateDirectory(Path.GetDirectoryName(newPath));

						File.Copy(texturePath, newPath, true);
					}
				}
			}
		}

		private string GetQ3ContentDir()
		{
			return Path.Combine(Environment.CurrentDirectory, Q3CONTENT_FOLDER);
		}

		public void Dispose()
		{
			// Delete temp content directory
			if (Directory.Exists(contentDir))
				Directory.Delete(contentDir, true);
		}
	}
}
