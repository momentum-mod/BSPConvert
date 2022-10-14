using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;

namespace BSPConversionLib
{
	public class TextureConverter
	{
		private string pk3Dir;
		private string outputDir;

		private HashSet<string> validExtensions = new HashSet<string>()
		{
			".bmp",
			".dds",
			".gif",
			".jpg",
			".png",
			".tga"
		};

		public TextureConverter(string pk3Dir, string outputDir)
		{
			this.pk3Dir = pk3Dir;
			this.outputDir = outputDir;
		}

		public void Convert()
		{
			var textures = FindTextures();
			var sb = new StringBuilder();
			foreach (var texture in textures)
				sb.Append($"-file \"{texture}\" ");

			var startInfo = new ProcessStartInfo();
			startInfo.FileName = "VTFCmd.exe";
			startInfo.Arguments = $"{sb} -output {outputDir}";

			Process.Start(startInfo);
		}

		private List<string> FindTextures()
		{
			var textures = new List<string>();

			foreach (var file in Directory.EnumerateFiles(pk3Dir))
			{
				if (validExtensions.Contains(file))
					textures.Add(file);
			}

			return textures;
		}
	}
}
