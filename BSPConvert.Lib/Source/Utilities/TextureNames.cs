using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace BSPConvert.Lib
{
	public static class TextureNames
	{
		// A quoted VMT value, or an unquoted one
		private static readonly Regex vmtToken = new Regex("\"([^\"\\r\\n]*)\"|[^\\s\"{}]+", RegexOptions.Compiled);

		// Strata takes everything after the last dot of a texture's file name for an extension and strips it
		// (NormalizeTextureName), so a VMT can't use a texture called textures/x/glow.blend: it loads textures/x/glow.vtf
		// instead and draws the error texture. Quake 3 maps name many images that way (glows like *.blend.tga). Renames
		// the VTFs in a materials folder whose names have dots (glow.blend.vtf to glow_blend.vtf) and points its VMTs at
		// the new names. Returns how many it renamed.
		public static int FixDottedTextureNames(string materialsDir)
		{
			var renamed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (var vtfPath in Directory.GetFiles(materialsDir, "*.vtf", SearchOption.AllDirectories))
			{
				var name = Path.GetFileNameWithoutExtension(vtfPath);
				if (!name.Contains('.', StringComparison.Ordinal))
					continue;

				var dir = Path.GetDirectoryName(vtfPath)!;
				var newName = name.Replace('.', '_');
				for (var i = 1; File.Exists(Path.Combine(dir, newName + ".vtf")); i++)
					newName = $"{name.Replace('.', '_')}_{i}";

				var newPath = Path.Combine(dir, newName + ".vtf");
				File.Move(vtfPath, newPath);
				renamed[GetTextureName(materialsDir, vtfPath)] = GetTextureName(materialsDir, newPath);
			}

			if (renamed.Count == 0)
				return 0;

			foreach (var vmtPath in Directory.GetFiles(materialsDir, "*.vmt", SearchOption.AllDirectories))
			{
				var vmt = File.ReadAllText(vmtPath);
				var fixedVmt = vmtToken.Replace(vmt, match =>
				{
					var value = match.Groups[1].Success ? match.Groups[1].Value : match.Value;
					if (!renamed.TryGetValue(NormalizeReference(value), out var newName))
						return match.Value;

					return match.Groups[1].Success ? $"\"{newName}\"" : newName;
				});

				if (fixedVmt != vmt)
					File.WriteAllText(vmtPath, fixedVmt);
			}

			return renamed.Count;
		}

		// A VTF's texture name, as VMTs reference it: relative to the materials folder, without .vtf, with forward slashes
		private static string GetTextureName(string materialsDir, string vtfPath)
		{
			var relative = Path.GetRelativePath(materialsDir, vtfPath).Replace('\\', '/');
			return relative[..^".vtf".Length];
		}

		// A texture VMTs reference, the way GetTextureName names it: a .vtf extension or backslashes are allowed
		private static string NormalizeReference(string value)
		{
			var name = value.Trim().Replace('\\', '/');
			return name.EndsWith(".vtf", StringComparison.OrdinalIgnoreCase) ? name[..^".vtf".Length] : name;
		}
	}
}
