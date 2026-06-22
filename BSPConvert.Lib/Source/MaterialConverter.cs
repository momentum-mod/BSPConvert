using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;

namespace BSPConvert.Lib
{
	public class MaterialConverter
	{
		private string pk3Dir;
		private Dictionary<string, Shader> shaderDict;
		private Dictionary<string, string> pk3ImageDict;
		private Dictionary<string, string> q3ImageDict;
		private Dictionary<string, string> customImageDict;
		private bool noEnvMap;
		private FlipbookConverter flipbookConverter;
		private CloudSkyboxBaker cloudSkyboxBaker;

		private string[] skySuffixes =
		{
			"bk",
			"dn",
			"ft",
			"lf",
			"rt",
			"up"
		};

		public MaterialConverter(string pk3Dir, Dictionary<string, Shader> shaderDict, bool noEnvMap = false, FlipbookOptions? flipbookOptions = null)
		{
			this.pk3Dir = pk3Dir;
			this.shaderDict = shaderDict;
			this.noEnvMap = noEnvMap;
			pk3ImageDict = GetImageLookupDictionary(pk3Dir);
			q3ImageDict = GetImageLookupDictionary(ContentManager.GetQ3ContentDir());
			customImageDict = GetImageLookupDictionary(ContentManager.GetCustomContentDir());
			flipbookConverter = new FlipbookConverter(pk3Dir, flipbookOptions ?? new FlipbookOptions(), ResolveImagePath);
			cloudSkyboxBaker = new CloudSkyboxBaker(pk3Dir, ResolveImagePath);
		}

		// Resolves a shader-relative texture path (no extension) to a source image file on disk, searching
		// the map's own content first, then the Q3 base content, then the user CustomContent folder.
		private string? ResolveImagePath(string texturePath)
		{
			var key = texturePath.ToLower(CultureInfo.InvariantCulture);
			if (pk3ImageDict.TryGetValue(key, out var path) ||
				q3ImageDict.TryGetValue(key, out path) ||
				customImageDict.TryGetValue(key, out path))
				return path;

			return null;
		}

		// Create a dictionary that maps relative texture paths to the full file paths in the content folder
		private Dictionary<string, string> GetImageLookupDictionary(string contentDir)
		{
			var imageDict = new Dictionary<string, string>();

			if (!Directory.Exists(contentDir))
				return imageDict;

			foreach (var file in Directory.GetFiles(contentDir, "*.*", SearchOption.AllDirectories))
			{
				var ext = Path.GetExtension(file);
				if (ext == ".tga" || ext == ".jpg")
				{
					var texturePath = file
						.Replace(contentDir + Path.DirectorySeparatorChar, "", StringComparison.OrdinalIgnoreCase)
						.Replace(Path.DirectorySeparatorChar, '/')
						.Replace(ext, "", StringComparison.OrdinalIgnoreCase)
						.ToLower(CultureInfo.InvariantCulture);

					if (!imageDict.ContainsKey(texturePath))
						imageDict.Add(texturePath, file);
				}
			}

			return imageDict;
		}

		public void Convert(string texture)
		{
			if (shaderDict.TryGetValue(texture, out var shader))
				CreateShaderVMT(texture, shader);
			else
				CreateDefaultVMT(texture);
		}

		private void CreateShaderVMT(string texture, Shader shader)
		{
			/*if (shader.fogParms != null)
				CreateFogVMT(texture, shader);
			else */if (flipbookConverter.TryConvert(texture, shader))
				return; // multi-pass scrolling shader baked into an animated flipbook VTF + VMT
			else if (shader.skyParms != null && shader.skyParms.HasImageBox)
				CreateSkyboxVMT(shader);
			else if (CloudSkyboxBaker.IsCloudSkyShader(shader) && cloudSkyboxBaker.TryConvert(texture, shader))
				return; // dynamic Q3 cloud sky baked into a static 6-sided Source skybox
			else if (shader.GetImageStages().Any(x => !string.IsNullOrEmpty(x.bundles[0].images[0])))
				CreateBaseShaderVMT(texture, shader);
		}

		private void CreateFogVMT(string texture, Shader shader)
		{
			var fogVmt = GenerateFogVMT(shader);
			WriteVMT(texture, fogVmt);
		}

		private string GenerateFogVMT(Shader shader)
		{
			var fogParms = shader.fogParms;
			var fogColor = $"{fogParms.color.X * 255} {fogParms.color.Y * 255} {fogParms.color.Z * 255}";

			return $$"""
					Water
					{
						$forceexpensive 1

						%tooltexture "dev/water_normal"

						$refracttexture "_rt_WaterRefraction"
						$refractamount 0

						$scale "[1 1]"

						$bottommaterial "dev/dev_water3_beneath"

						$normalmap "dev/bump_normal"

						%compilewater 1
						$surfaceprop "water"

						$fogenable 1
						$fogcolor "{{fogColor}}"

						$fogstart 0
						$fogend {{fogParms.depthForOpaque}}

						$abovewater 1
					}
					""";
		}

		private void CreateSkyboxVMT(Shader shader)
		{
			foreach (var suffix in skySuffixes)
			{
				var skyTexture = $"{shader.skyParms.outerBox}_{suffix}";
				if (!PrepareSkyboxImage(skyTexture))
					continue;

				var baseTexture = $"skybox/{shader.skyParms.outerBox}{suffix}";
				var skyboxVmt = GenerateSkyboxVMT(baseTexture);
				WriteVMT(baseTexture, skyboxVmt);
			}
		}

		// Try to find the sky image file and move it to skybox folder in order for Source engine to detect it properly
		private bool PrepareSkyboxImage(string skyTexture)
		{
			skyTexture = skyTexture.ToLower(CultureInfo.InvariantCulture);

			var skyboxDir = Path.Combine(pk3Dir, "skybox");
			if (pk3ImageDict.TryGetValue(skyTexture, out var pk3Path))
			{
				var newPath = pk3Path.Replace(pk3Dir, skyboxDir, StringComparison.OrdinalIgnoreCase);
				var destFile = newPath.Remove(newPath.LastIndexOf('_'), 1); // Remove underscore from skybox suffix

				FileUtil.MoveFile(pk3Path, destFile);

				return true;
			}

			// Search external content (Q3 base first, then user-managed CustomContent) for the sky image.
			return TryCopyExternalSky(q3ImageDict, ContentManager.GetQ3ContentDir(), skyTexture, skyboxDir)
				|| TryCopyExternalSky(customImageDict, ContentManager.GetCustomContentDir(), skyTexture, skyboxDir);
		}

		private bool TryCopyExternalSky(Dictionary<string, string> imageDict, string contentDir, string skyTexture, string skyboxDir)
		{
			if (!imageDict.TryGetValue(skyTexture, out var sourcePath))
				return false; // No sky image found

			var newPath = sourcePath.Replace(contentDir, skyboxDir, StringComparison.OrdinalIgnoreCase);
			var destFile = newPath.Remove(newPath.LastIndexOf('_'), 1); // Remove underscore from skybox suffix

			FileUtil.CopyFile(sourcePath, destFile);

			return true;
		}

		private void CreateBaseShaderVMT(string texture, Shader shader)
		{
			var images = shader.GetImageStages().SelectMany(x => x.bundles[0].images);
			foreach (var image in images)
			{
				if (string.IsNullOrEmpty(image))
					continue;

				var baseTexture = Path.ChangeExtension(image, null);
				TryCopyQ3Content(baseTexture);
			}

			var shaderVmt = GenerateVMT(shader);
			WriteVMT(texture, shaderVmt);
		}

		private string GenerateVMT(Shader shader)
		{
			var sb = new StringBuilder();
			sb.AppendLine(GetShaderType(shader));
			sb.AppendLine("{");

			AppendShaderParameters(sb, shader);

			sb.AppendLine("}");

			return sb.ToString();
		}

		private string GetShaderType(Shader shader)
		{
			// "blendfunc filter" (GL_DST_COLOR GL_ZERO) is a multiply/modulate blend that no generic Source
			// shader can express. The dedicated Modulate shader multiplies the texture into the framebuffer
			// (white texels vanish, black texels darken), exactly matching Q3's filter blend.
			if (IsModulateBlend(shader))
				return "Modulate";

			if (shader.surfaceFlags.HasFlag(Q3SurfaceFlags.SURF_NOLIGHTMAP))
				return "UnlitGeneric";

			return "LightmappedGeneric";
		}

		// "blendfunc filter", written either as "GL_DST_COLOR GL_ZERO" or the equivalent "GL_ZERO GL_SRC_COLOR".
		private bool IsModulateBlend(Shader shader)
		{
			var blendStage = GetTextureStage(shader.GetImageStages());
			if (blendStage == null)
				return false;

			var srcBlend = blendStage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = blendStage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			var isFilterBlend = (srcBlend == ShaderStageFlags.GLS_SRCBLEND_DST_COLOR && dstBlend == ShaderStageFlags.GLS_DSTBLEND_ZERO) ||
				(srcBlend == ShaderStageFlags.GLS_SRCBLEND_ZERO && dstBlend == ShaderStageFlags.GLS_DSTBLEND_SRC_COLOR);
			if (!isFilterBlend)
				return false;

			// A filter blend alongside a lightmap stage is the classic Q3 lit-surface idiom (the diffuse is
			// multiplied against the lightmap), not a screen-multiply overlay. Since GetImageStages() drops
			// the $lightmap stage, that diffuse stage would otherwise be misread as a standalone modulate.
			// Source's Modulate shader multiplies onto the world behind the surface, so only treat a filter
			// blend as Modulate when no lightmap stage is feeding it.
			var hasLightmapStage = shader.stages.Any(s =>
				s.bundles[0].tcGen == TexCoordGen.TCGEN_LIGHTMAP || s.bundles[0].images[0] == "$lightmap");

			return !hasLightmapStage;
		}

		private void WriteVMT(string texture, string vmt)
		{
			var vmtPath = Path.Combine(pk3Dir, $"{texture}.vmt");
			Directory.CreateDirectory(Path.GetDirectoryName(vmtPath));

			File.WriteAllText(vmtPath, vmt);
		}

		// Copies content from the Q3Content folder, falling back to the user-managed CustomContent
		// folder for assets the map depends on but doesn't bundle.
		private void TryCopyQ3Content(string texturePath)
		{
			// Q3 base content takes precedence (matches existing behavior).
			if (TryCopyExternalImage(q3ImageDict, ContentManager.GetQ3ContentDir(), texturePath))
				return;

			// CustomContent only fills gaps - never overwrite the map's own bundled textures.
			if (!pk3ImageDict.ContainsKey(texturePath))
				TryCopyExternalImage(customImageDict, ContentManager.GetCustomContentDir(), texturePath);
		}

		private bool TryCopyExternalImage(Dictionary<string, string> imageDict, string contentDir, string texturePath)
		{
			if (!imageDict.TryGetValue(texturePath, out var sourcePath))
				return false;

			var newPath = sourcePath.Replace(contentDir, pk3Dir, StringComparison.OrdinalIgnoreCase);
			FileUtil.CopyFile(sourcePath, newPath);
			return true;
		}

		// Selects the stage that carries the visible texture. Prefers a plain texture stage (not env-mapped
		// or lightmap) that isn't a depth-priming stage, then relaxes each condition so a stage is still
		// found when every candidate is degenerate - env-map-only shaders (e.g. glass using "tcGen
		// environment") have no plain texture stage, and an UnlitGeneric/LightmappedGeneric without a
		// $basetexture renders as solid white.
		private static ShaderStage? GetTextureStage(IEnumerable<ShaderStage> stages)
		{
			bool IsTextureStage(ShaderStage x) => x.bundles[0].tcGen != TexCoordGen.TCGEN_ENVIRONMENT_MAPPED && x.bundles[0].tcGen != TexCoordGen.TCGEN_LIGHTMAP;

			// Pick the most surface-like stage, relaxing the criteria in priority order:
			//  1. A static opaque base (the ideal: a plain wall/floor under animated overlays).
			//  2. Any static non-alpha texture - a static texture drawn additively still reads as the surface
			//     (e.g. a glowing decal), while the animated stages around it are effects we drop.
			//  3. An animated opaque base (e.g. scrolling lava with a static alpha overlay on top).
			//  4. Looser fallbacks so $basetexture is always emitted.
			// Alpha-blended stages are excluded from (2) because a static alpha layer is translucent detail
			// sitting on a base, so the base (reached at 3) should win instead of the detail.
			bool IsBaseCandidate(ShaderStage x) => IsTextureStage(x) && !IsDepthPrimingStage(x) && !IsOverlayBlend(x);
			return stages.FirstOrDefault(x => IsBaseCandidate(x) && !IsAnimatedStage(x))
				?? stages.FirstOrDefault(x => IsTextureStage(x) && !IsDepthPrimingStage(x) && !IsAnimatedStage(x) && !IsAlphaBlendStage(x))
				?? stages.FirstOrDefault(IsBaseCandidate)
				?? stages.FirstOrDefault(x => IsTextureStage(x) && !IsDepthPrimingStage(x))
				?? stages.FirstOrDefault(IsTextureStage)
				?? stages.FirstOrDefault(x => !IsDepthPrimingStage(x))
				?? stages.FirstOrDefault();
		}

		// Alpha-blended overlay ("GL_src_alpha GL_one_minus_src_alpha"), e.g. a translucent detail/decal layer.
		private static bool IsAlphaBlendStage(ShaderStage stage)
		{
			var srcBlend = stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = stage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			return srcBlend == ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA &&
				dstBlend == ShaderStageFlags.GLS_DSTBLEND_ONE_MINUS_SRC_ALPHA;
		}

		// A stage that animates over time - a multi-frame animMap, or texcoords that move
		// (scroll/rotate/stretch/turbulence) - i.e. an effect layer rather than a static base surface.
		private static bool IsAnimatedStage(ShaderStage stage)
		{
			return stage.bundles[0].numImageAnimations > 1 ||
				stage.bundles[0].texMods.Any(t =>
					t.type == TexMod.TMOD_SCROLL || t.type == TexMod.TMOD_ROTATE ||
					t.type == TexMod.TMOD_STRETCH || t.type == TexMod.TMOD_TURBULENT);
		}

		// A transparent overlay blend - additive ("GL_one GL_one"/"GL_src_alpha GL_one") or alpha
		// ("GL_src_alpha GL_one_minus_src_alpha") - as opposed to an opaque base ("GL_one GL_zero" or no blend).
		private static bool IsOverlayBlend(ShaderStage stage)
		{
			var srcBlend = stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = stage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;

			var isAdditive = dstBlend == ShaderStageFlags.GLS_DSTBLEND_ONE &&
				(srcBlend == ShaderStageFlags.GLS_SRCBLEND_ONE || srcBlend == ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA);
			var isAlphaBlend = srcBlend == ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA &&
				dstBlend == ShaderStageFlags.GLS_DSTBLEND_ONE_MINUS_SRC_ALPHA;

			return isAdditive || isAlphaBlend;
		}

		// An opaque stage writes solid color to the framebuffer ("GL_one GL_zero" or no blendFunc). Any shader
		// containing one is opaque overall: later additive/alpha stages only lighten or tint that base, so the
		// world behind the surface is never visible.
		private static bool IsOpaqueStage(ShaderStage stage)
		{
			var srcBlend = stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = stage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;

			var noBlend = srcBlend == 0 && dstBlend == 0;
			var isOneZero = srcBlend == ShaderStageFlags.GLS_SRCBLEND_ONE && dstBlend == ShaderStageFlags.GLS_DSTBLEND_ZERO;

			return noBlend || isOneZero;
		}

		private void AppendShaderParameters(StringBuilder sb, Shader shader)
		{
			var stages = shader.GetImageStages();
			var textureStage = GetTextureStage(stages);
			if (textureStage != null)
			{
				var texture = Path.ChangeExtension(textureStage.bundles[0].images[0], null);
				sb.AppendLine(CultureInfo.InvariantCulture, $"\t$basetexture \"{texture}\"");

				if (textureStage.rgbGen.HasFlag(ColorGen.CGEN_CONST))
				{
					var color = textureStage.constantColor;
					var colorStr = $"{color[0]} {color[1]} {color[2]}";
					sb.AppendLine("\t$color \"{" + colorStr + "}\"");
				}

				if (textureStage.alphaGen.HasFlag(AlphaGen.AGEN_CONST))
				{
					var alpha = (float)textureStage.constantColor[3] / 255;
					sb.AppendLine(CultureInfo.InvariantCulture, $"\t$alpha {alpha}");
				}
			}

			var envMapStage = noEnvMap ? null : stages.FirstOrDefault(x => x.bundles[0].tcGen == TexCoordGen.TCGEN_ENVIRONMENT_MAPPED);
			if (envMapStage != null)
			{
				sb.AppendLine($"\t$envmap \"engine/defaultcubemap\"");

				if (envMapStage.alphaGen == AlphaGen.AGEN_CONST)
				{
					var alpha = (float)envMapStage.constantColor[3] / 255;
					sb.AppendLine(CultureInfo.InvariantCulture, $"\t$envmaptint \"[{alpha} {alpha} {alpha}]\"");
				}
				else if (envMapStage.rgbGen == ColorGen.CGEN_WAVEFORM)
				{
					var alpha = envMapStage.rgbWave.base_;
					sb.AppendLine(CultureInfo.InvariantCulture, $"\t$envmaptint \"[{alpha} {alpha} {alpha}]\"");
				}
			}

			if (shader.cullType == CullType.TWO_SIDED)
				sb.AppendLine("\t$nocull 1");

			// Classify the visible stage's blend mode (textureStage already skips depth-priming stages).
			var blendStage = textureStage ?? stages.FirstOrDefault();
			var isAdditive = false;
			var isAlphaBlend = false;
			if (blendStage != null)
			{
				// Blend factors are multi-bit values within a bitfield, so mask them out before comparing
				var srcBlend = blendStage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
				var dstBlend = blendStage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;

				// Additive blend (e.g. "GL_ONE GL_ONE" or "GL_SRC_ALPHA GL_ONE") - black pixels become transparent
				isAdditive = dstBlend == ShaderStageFlags.GLS_DSTBLEND_ONE &&
					(srcBlend == ShaderStageFlags.GLS_SRCBLEND_ONE || srcBlend == ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA);

				// Alpha blend (e.g. "GL_SRC_ALPHA GL_ONE_MINUS_SRC_ALPHA")
				isAlphaBlend = srcBlend == ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA && dstBlend == ShaderStageFlags.GLS_DSTBLEND_ONE_MINUS_SRC_ALPHA;
			}

			// When a multi-stage shader has been reduced to its base surface, the chosen stage's own
			// additive/alpha blend was only meaningful as one layer of that composite, not against the
			// framebuffer. On its own as the $basetexture it should read as a solid opaque surface, so drop
			// the inherited transparency. This applies when:
			//   - the discarded layers were animated effects sitting on top of a static base, or
			//   - an opaque stage sits beneath the chosen one and already fills the surface (e.g. a shiny
			//     metal whose environment reflection is drawn as the GL_ONE GL_ZERO base, with the metal
			//     texture added on top - that metal becomes the $basetexture but is not the bottom layer).
			if (textureStage != null && (isAdditive || isAlphaBlend))
			{
				var droppedAnimatedLayer = !IsAnimatedStage(textureStage) && stages.Any(IsAnimatedStage);
				var hasOpaqueBaseBeneath = stages.Any(s => s != textureStage && IsOpaqueStage(s) && !IsDepthPrimingStage(s));
				if (droppedAnimatedLayer || hasOpaqueBaseBeneath)
				{
					isAdditive = false;
					isAlphaBlend = false;
				}
			}

			var flags = (textureStage?.flags ?? 0) | (envMapStage?.flags ?? 0);
			var isAlphaTest = flags.HasFlag(ShaderStageFlags.GLS_ATEST_GE_80);

			// Q3 polygonOffset surfaces are coplanar overlays (decals, grates, signs) sitting on a wall.
			// They need TWO things to render correctly in Source:
			//   $decal       - the slope-scaled depth bias (Source's equivalent of polygonOffset) that
			//                  stops the overlay from z-fighting with the coplanar wall.
			//   $translucent - $decal also disables depth writes, so while it's opaque-sorted a wall
			//                  drawn afterward overpaints it, and that draw order flips across visleaf
			//                  boundaries (the overlay blinks). Marking it translucent moves it to the
			//                  post-opaque pass, drawn after every wall, so it stays stable. (Additive/
			//                  alpha-blend overlays already sort post-opaque, so they just need $decal
			//                  plus their own blend, added below.)
			if (shader.polygonOffset)
				sb.AppendLine("\t$decal 1");

			if (shader.polygonOffset && !isAdditive && !isAlphaBlend)
				sb.AppendLine("\t$translucent 1");
			else if (!shader.polygonOffset && isAlphaTest)
			{
				sb.AppendLine("\t$alphatest 1");
				sb.AppendLine("\t$alphatestreference 0.5");
			}

			if (isAdditive)
				sb.AppendLine("\t$additive 1");
			else if (isAlphaBlend)
				sb.AppendLine("\t$translucent 1");

			if (textureStage != null && textureStage.bundles[0].texMods.Any(y => y.type == TexMod.TMOD_SCROLL || y.type == TexMod.TMOD_ROTATE ||
				y.type == TexMod.TMOD_STRETCH || y.type == TexMod.TMOD_SCALE))
				ConvertTexMods(sb, textureStage);
		}

		// Detects Q3 depth-priming stages: a "tcmod scale 0 0" collapses the stage's texcoords to a single
		// texel, so it carries no visible texture and exists only to prime depth (common in decal shaders).
		private static bool IsDepthPrimingStage(ShaderStage stage)
		{
			return stage.bundles[0].texMods.Any(t => t.type == TexMod.TMOD_SCALE && t.scale[0] == 0f && t.scale[1] == 0f);
		}

		private void ConvertTexMods(StringBuilder sb, ShaderStage texModStage)
		{
			AppendProxyVars(sb, texModStage);

			foreach (var texModInfo in texModStage.bundles[0].texMods)
			{
				if (texModInfo.type == TexMod.TMOD_ROTATE)
					ConvertTexModRotate(sb, texModInfo);
				else if (texModInfo.type == TexMod.TMOD_SCROLL)
					ConvertTexModScroll(sb, texModInfo);
				else if (texModInfo.type == TexMod.TMOD_STRETCH)
					ConvertTexModStretch(sb, texModInfo);
			}

			// A stage's tcmods collapse into one $basetexturetransform, so emit a single TextureTransform after
			// the value proxies (LinearRamp/Sine) that feed it.
			if (texModStage.bundles[0].texMods.Any(t => t.type == TexMod.TMOD_ROTATE || t.type == TexMod.TMOD_SCROLL ||
				t.type == TexMod.TMOD_STRETCH || t.type == TexMod.TMOD_SCALE))
				AppendTextureTransform(sb, texModStage);

			sb.AppendLine("\t}");
		}

		private static void AppendTextureTransform(StringBuilder sb, ShaderStage texModStage)
		{
			sb.AppendLine("\t\tTextureTransform");
			sb.AppendLine("\t\t{");

			foreach (var texModInfo in texModStage.bundles[0].texMods)
			{
				if (texModInfo.type == TexMod.TMOD_ROTATE)
				{
					sb.AppendLine("\t\t\trotateVar $angle");
					sb.AppendLine("\t\t\tcenterVar $center");
				}
				else if (texModInfo.type == TexMod.TMOD_SCROLL)
					sb.AppendLine("\t\t\ttranslateVar $translate");
				else if (texModInfo.type == TexMod.TMOD_STRETCH || texModInfo.type == TexMod.TMOD_SCALE)
					sb.AppendLine("\t\t\tscaleVar $scale");
			}

			sb.AppendLine("\t\t\tinitialValue 0");
			sb.AppendLine("\t\t\tresultVar $basetexturetransform");
			sb.AppendLine("\t\t}");
		}

		private static void AppendProxyVars(StringBuilder sb, ShaderStage texModStage)
		{
			foreach (var texModInfo in texModStage.bundles[0].texMods)
			{
				if (texModInfo.type == TexMod.TMOD_ROTATE)
				{
					sb.AppendLine("\t$angle 0.0");
					sb.AppendLine("\t$center \"[0.5 0.5]\"");
				}
				else if (texModInfo.type == TexMod.TMOD_SCROLL)
					sb.AppendLine("\t$translate \"[0.0 0.0]\"");
				else if (texModInfo.type == TexMod.TMOD_SCALE)
					sb.AppendLine(CultureInfo.InvariantCulture, $"\t$scale \"[{texModInfo.scale[0]} {texModInfo.scale[1]}]\"");
				else if (texModInfo.type == TexMod.TMOD_STRETCH)
					sb.AppendLine("\t$scale 1");

				if (texModInfo.wave.func == GenFunc.GF_SQUARE)
				{
					sb.AppendLine(CultureInfo.InvariantCulture, $"\t$min {texModInfo.wave.base_}");
					sb.AppendLine(CultureInfo.InvariantCulture, $"\t$max {texModInfo.wave.amplitude}");
					sb.AppendLine(CultureInfo.InvariantCulture, $"\t$mid {(texModInfo.wave.amplitude + texModInfo.wave.base_) / 2}");
				}
			}

			sb.AppendLine("\tProxies");
			sb.AppendLine("\t{");
		}

		// TODO: Convert other waveforms
		private static void ConvertTexModStretch(StringBuilder sb, TexModInfo texModInfo)
		{
			switch (texModInfo.wave.func)
			{
				case GenFunc.GF_SIN:
					ConvertSineWaveStretch(sb, texModInfo);
					break;
				case GenFunc.GF_SQUARE:
					ConvertSquareWaveStretch(sb, texModInfo);
					break;
				case GenFunc.GF_SAWTOOTH:
				case GenFunc.GF_INVERSE_SAWTOOTH:
					break;
			}
		}

		private static void ConvertSineWaveStretch(StringBuilder sb, TexModInfo texModInfo)
		{
			sb.AppendLine("\t\tSine");
			sb.AppendLine("\t\t{");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\tsinemin {texModInfo.wave.base_}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\tsinemax {texModInfo.wave.amplitude}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\tsineperiod {1 / texModInfo.wave.frequency}");
			sb.AppendLine("\t\t\tinitialValue 0.0");
			sb.AppendLine("\t\t\tresultVar $scale");
			sb.AppendLine("\t\t}");
		}

		private static void ConvertSquareWaveStretch(StringBuilder sb, TexModInfo texModInfo)
		{
			sb.AppendLine("\t\tSine");
			sb.AppendLine("\t\t{");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\tsinemin {texModInfo.wave.base_}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\tsinemax {texModInfo.wave.amplitude}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\tsineperiod {1 / texModInfo.wave.frequency}");
			sb.AppendLine("\t\t\tinitialValue 0.0");
			sb.AppendLine("\t\t\tresultVar $sineOutput");
			sb.AppendLine("\t\t}");

			sb.AppendLine("\t\tLessOrEqual");
			sb.AppendLine("\t\t{");
			sb.AppendLine($"\t\t\tlessEqualVar $min");
			sb.AppendLine($"\t\t\tgreaterVar $max");
			sb.AppendLine($"\t\t\tsrcVar1 $sineOutput");
			sb.AppendLine($"\t\t\tsrcVar2 $mid");
			sb.AppendLine($"\t\t\tresultVar $scale");
			sb.AppendLine("\t\t}");
		}

		private static void ConvertTexModScroll(StringBuilder sb, TexModInfo texModInfo)
		{
			sb.AppendLine("\t\tLinearRamp");
			sb.AppendLine("\t\t{");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\trate {texModInfo.scroll[0]}");
			sb.AppendLine("\t\t\tinitialValue 0.0");
			sb.AppendLine("\t\t\tresultVar \"$translate[0]\"");
			sb.AppendLine("\t\t}");

			sb.AppendLine("\t\tLinearRamp");
			sb.AppendLine("\t\t{");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\trate {texModInfo.scroll[1]}");
			sb.AppendLine("\t\t\tinitialValue 0.0");
			sb.AppendLine("\t\t\tresultVar \"$translate[1]\"");
			sb.AppendLine("\t\t}");
		}

		private static void ConvertTexModRotate(StringBuilder sb, TexModInfo texModInfo)
		{
			sb.AppendLine("\t\tLinearRamp");
			sb.AppendLine("\t\t{");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\trate {texModInfo.rotateSpeed}");
			sb.AppendLine("\t\t\tinitialValue 0.0");
			sb.AppendLine("\t\t\tresultVar $angle");
			sb.AppendLine("\t\t}");
		}

		private string GenerateSkyboxVMT(string baseTexture)
		{
			var sb = new StringBuilder();
			sb.AppendLine("UnlitGeneric");
			sb.AppendLine("{");

			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\"$basetexture\" \"{baseTexture}\"");
			sb.AppendLine("\t\"$nofog\" 1");
			sb.AppendLine("\t\"$ignorez\" 1");

			sb.AppendLine("}");

			return sb.ToString();
		}

		private void CreateDefaultVMT(string texture)
		{
			TryCopyQ3Content(texture);

			var vmt = GenerateDefaultLitVMT(texture);
			WriteVMT(texture, vmt);
		}

		private string GenerateDefaultLitVMT(string texture)
		{
			return $$"""
				LightmappedGeneric
				{
					$basetexture "{{texture}}"
				}
				""";
		}
	}
}
