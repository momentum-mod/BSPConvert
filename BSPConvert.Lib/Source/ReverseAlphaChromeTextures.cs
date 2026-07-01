using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace BSPConvert.Lib
{
	// Identifies the diffuse textures of Q3 reverse-alpha chrome shaders: an opaque "tcGen environment"
	// reflection with a diffuse drawn over it via "blendFunc GL_ONE_MINUS_SRC_ALPHA GL_SRC_ALPHA" (reflection =
	// refl*alpha). Their VTF alpha must be inverted so the stock $basealphaenvmapmask (which masks by 1 - alpha)
	// reproduces that refl*alpha weighting. Shared by MaterialConverter (which names the $basetexture) and
	// TextureConverter (which bakes the VTF) so both agree on which textures are inverted and how they're named -
	// a single detection instead of two hand-kept-in-sync copies.
	//
	// A texture used only as a chrome base is inverted in place (its own VTF holds the inverted alpha); one also
	// referenced non-inverted elsewhere keeps its normal VTF and gets a separate suffixed inverted copy, so the
	// shared image is never clobbered.
	internal class ReverseAlphaChromeTextures
	{
		public const string InvertedAlphaSuffix = "_invalpha";

		private readonly HashSet<string> invertedBases;    // reverse-alpha chrome diffuse textures (need inversion)
		private readonly HashSet<string> sharedWithNormal; // ...that are also referenced non-inverted elsewhere

		private ReverseAlphaChromeTextures(HashSet<string> invertedBases, HashSet<string> sharedWithNormal)
		{
			this.invertedBases = invertedBases;
			this.sharedWithNormal = sharedWithNormal;
		}

		public static ReverseAlphaChromeTextures Analyze(IEnumerable<Shader> shaders)
		{
			var invertedBases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var normalRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

			foreach (var shader in shaders)
			{
				if (shader.stages == null)
					continue;

				var hasOpaqueEnv = shader.stages.Any(s =>
					s.bundles[0].tcGen == TexCoordGen.TCGEN_ENVIRONMENT_MAPPED && ShaderStageUtils.IsOpaqueBlend(s));

				foreach (var stage in shader.stages)
				{
					var image = NormalizeImage(stage.bundles[0].images[0]);
					if (image == null)
						continue;

					if (hasOpaqueEnv && ShaderStageUtils.IsReverseAlphaBlend(stage))
						invertedBases.Add(image);
					else
						normalRefs.Add(image); // any other stage usage keeps the texture's normal VTF alive
				}
			}

			var sharedWithNormal = new HashSet<string>(invertedBases.Where(normalRefs.Contains), StringComparer.OrdinalIgnoreCase);
			return new ReverseAlphaChromeTextures(invertedBases, sharedWithNormal);
		}

		// True when this texture is a reverse-alpha chrome diffuse whose VTF must carry inverted alpha.
		public bool NeedsInversion(string texturePath) => invertedBases.Contains(Key(texturePath));

		// True when an inverted texture is also used non-inverted, so it needs its normal VTF plus a separate
		// suffixed inverted copy rather than being inverted in place.
		public bool IsSharedWithNormalUse(string texturePath) => sharedWithNormal.Contains(Key(texturePath));

		// The name a material should reference for a reverse-alpha chrome diffuse: the suffixed inverted copy when
		// the texture is also used normally, otherwise the base name (whose own VTF is inverted in place).
		public string BaseTextureName(string texturePath) =>
			IsSharedWithNormalUse(texturePath) ? texturePath + InvertedAlphaSuffix : texturePath;

		private static string Key(string texturePath) => texturePath.Replace('\\', '/');

		private static string? NormalizeImage(string image) =>
			string.IsNullOrEmpty(image) || image.StartsWith('$') ? null : Key(Path.ChangeExtension(image, null));
	}
}
