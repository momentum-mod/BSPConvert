using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Globalization;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace BSPConvert.Lib
{
	public class MaterialConverter
	{
		// Shared placeholder $basetexture for env-map-only shaders (relative material path, no extension).
		// Backed by the pre-made Assets/materials/tools/envmapinvisible.vtf (a transparent tools texture).
		private const string InvisibleBaseTexture = "tools/envmapinvisible";

		private string pk3Dir;
		private Dictionary<string, Shader> shaderDict;
		private Dictionary<string, string> pk3ImageDict;
		private Dictionary<string, string> q3ImageDict;
		private Dictionary<string, string> customImageDict;
		private bool noEnvMap;
		private bool invisibleBaseTextureCreated;
		private ReverseAlphaChromeTextures reverseAlphaChrome;
		// When true (the default, non-obb fog path), Q3 fog shaders are emitted as Fog VMTs carrying the fog
		// appearance ($fogcolor/$fogdepthforopaque), which the engine reads off the CONTENTS_FOG
		// brush to composite the fog-volume overlay. Left off for the obb_volumefog path, which renders fog via entities.
		private bool generateFogMaterials;
		private FlipbookConverter flipbookConverter;
		private DetailMaterialConverter detailMaterialConverter;
		private CloudSkyboxBaker cloudSkyboxBaker;
		// Relative texture paths (no extension, forward slashes) that the generated materials actually
		// reference. TextureConverter uses this to skip converting/embedding unused pk3 images - lightmaps
		// (baked into Source lightmaps instead), levelshots, and source frames already baked into flipbooks.
		// Case-insensitive so it matches on-disk filename casing.
		private readonly HashSet<string> referencedTextures = new(StringComparer.OrdinalIgnoreCase);
		// Mean alpha per texture path, cached so a texture shared by several shaders is only decoded once.
		private readonly Dictionary<string, float> averageAlphaCache = new(StringComparer.OrdinalIgnoreCase);

		private string[] skySuffixes =
		{
			"bk",
			"dn",
			"ft",
			"lf",
			"rt",
			"up"
		};

		public MaterialConverter(string pk3Dir, Dictionary<string, Shader> shaderDict, bool noEnvMap = false, FlipbookOptions? flipbookOptions = null, bool generateFogMaterials = false)
		{
			this.pk3Dir = pk3Dir;
			this.shaderDict = shaderDict;
			this.noEnvMap = noEnvMap;
			this.generateFogMaterials = generateFogMaterials;
			pk3ImageDict = GetImageLookupDictionary(pk3Dir);
			q3ImageDict = GetImageLookupDictionary(ContentManager.GetQ3ContentDir());
			customImageDict = GetImageLookupDictionary(ContentManager.GetCustomContentDir());
			reverseAlphaChrome = ReverseAlphaChromeTextures.Analyze(shaderDict.Values);
			var resolvedFlipbookOptions = flipbookOptions ?? new FlipbookOptions();
			flipbookConverter = new FlipbookConverter(pk3Dir, resolvedFlipbookOptions, ResolveImagePath, TryCopyQ3Content, noEnvMap);
			detailMaterialConverter = new DetailMaterialConverter(pk3Dir, resolvedFlipbookOptions, ResolveImagePath, noEnvMap, TryCopyQ3Content);
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

		// Texture paths (no extension, forward slashes) referenced by the materials generated so far, so the
		// texture pass only converts/embeds images the map actually uses. See referencedTextures.
		public IReadOnlySet<string> ReferencedTextures => referencedTextures;

		// Records a texture path (any casing/separators) as referenced by a generated material.
		private void RecordReferencedTexture(string texturePath)
		{
			if (!string.IsNullOrEmpty(texturePath))
				referencedTextures.Add(texturePath.Replace('\\', '/'));
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
			if (shader.fogParms != null && generateFogMaterials)
			{
				CreateFogVMT(texture, shader); // Fog appearance material for the CONTENTS_FOG overlay

				// A Q3 fog shader can also carry visible texture stages (e.g. the scrolling cloud layers on
				// textures/sfx/hellfog) that Q3 draws over the fog boundary. The fog texture name is reserved for
				// the Fog appearance VMT, so emit those stages as a sibling overlay material that the converted
				// fog faces reference (see BSPConverter.TryCreateFogOverlayFace). The overlay face is coplanar with
				// the fog boundary, so $decal gives it the decal depth bias that stops it z-fighting the fog volume,
				// and $translucent keeps it in the post-opaque pass for stable draw order (same reasoning as the
				// polygonOffset overlays handled in AppendShaderParameters).
				if (FogShaderHasOverlay(shader))
					CreateBaseShaderVMT(GetFogOverlayTextureName(texture), shader, "$decal 1", "$translucent 1");
			}
			// A sky shader's stages (e.g. scrolling cloud/flare layers) describe the Q3 dynamic sky overlay,
			// not a liquid/flipbook material - guard both converters below so a skyParms shader always falls
			// through to the sky-specific branches instead of being misread as a scrolling detail material.
			else if (shader.skyParms == null && detailMaterialConverter.TryConvert(texture, shader))
				return; // scroll-only liquid converted to a live $basetexture+$detail material
			else if (shader.skyParms == null && flipbookConverter.TryConvert(texture, shader))
				return; // multi-pass scrolling shader baked into an animated flipbook VTF + VMT
			else if (shader.skyParms != null && shader.skyParms.HasImageBox)
			{
				// Q3 draws the outerbox first and then projects the shader's own stages onto the cloud dome
				// over it (RB_StageIteratorSky), so an outerbox shader that also has cloud/flare stages needs
				// those baked in too - the plain box copy below would otherwise silently drop them.
				if (CloudSkyboxBaker.HasBakeableCloudStages(shader) && cloudSkyboxBaker.TryConvert(texture, shader))
					return;
				CreateSkyboxVMT(shader);
			}
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

		// Suffix for the visible overlay material derived from a fog shader that also carries texture stages
		// (the fog texture name itself is taken by the Fog appearance VMT).
		private const string FogOverlaySuffix = "_fogoverlay";

		// A Q3 fog shader whose stages include a real (non-$lightmap) texture - e.g. the scrolling
		// kc_fogcloud3 layers on textures/sfx/hellfog - is drawn by Q3 as those stages over the fog boundary.
		// Such shaders get a sibling overlay material in addition to the Fog appearance material.
		public static bool FogShaderHasOverlay(Shader shader)
		{
			return shader.fogParms != null && shader.GetImageStages().Any();
		}

		// Name of the visible overlay material derived from a fog shader (see FogShaderHasOverlay).
		public static string GetFogOverlayTextureName(string fogTextureName)
		{
			return fogTextureName + FogOverlaySuffix;
		}

		private string GenerateFogVMT(Shader shader)
		{
			var fogParms = shader.fogParms;
			var fogColor = $"[{fogParms.color.X} {fogParms.color.Y} {fogParms.color.Z}]";

			return $$"""
					Fog
					{
						%compileFog 1
						$fogcolor "{{fogColor}}"
						$fogdepthforopaque {{fogParms.depthForOpaque}}
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
				RecordReferencedTexture(baseTexture); // moved under skybox/, so not seen by TryCopyQ3Content
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

		private void CreateBaseShaderVMT(string texture, Shader shader, params string[] extraParams)
		{
			// Skip external-lightmap stages ("tcGen lightmap" with a real image, e.g. maps/<map>/lm_0000): they're
			// baked into the Source lightmap by ExternalLightmapLoader/ConvertLightmaps, never referenced as a VMT
			// texture, so they must not be copied in or marked for VTF conversion.
			var images = shader.GetImageStages()
				.Where(x => x.bundles[0].tcGen != TexCoordGen.TCGEN_LIGHTMAP)
				.SelectMany(x => x.bundles[0].images);
			foreach (var image in images)
			{
				if (string.IsNullOrEmpty(image))
					continue;

				var baseTexture = Path.ChangeExtension(image, null);
				TryCopyQ3Content(baseTexture);
			}

			var shaderVmt = GenerateVMT(shader, extraParams);
			WriteVMT(texture, shaderVmt);
		}

		// extraParams are appended verbatim (tab-indented) inside the material block, for callers that need
		// parameters not derived from the Q3 shader itself (e.g. the fog overlay's decal depth bias).
		private string GenerateVMT(Shader shader, params string[] extraParams)
		{
			var sb = new StringBuilder();
			sb.AppendLine(GetShaderType(shader));
			sb.AppendLine("{");

			AppendShaderParameters(sb, shader);

			foreach (var param in extraParams)
				sb.AppendLine(CultureInfo.InvariantCulture, $"\t{param}");

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
			return !ShaderStageUtils.HasLightmapStage(shader);
		}

		private void WriteVMT(string texture, string vmt)
		{
			var vmtPath = Path.Combine(pk3Dir, $"{texture}.vmt");
			Directory.CreateDirectory(Path.GetDirectoryName(vmtPath));

			File.WriteAllText(vmtPath, vmt);
		}

		// Returns the relative material path of the shared invisible placeholder texture, copying its pre-made
		// VTF into pk3Dir on first use. The texture pass (TextureConverter) later relocates/embeds it like any
		// other VTF.
		private string GetInvisibleBaseTexture()
		{
			if (!invisibleBaseTextureCreated)
			{
				CopyInvisibleBaseTexture();
				invisibleBaseTextureCreated = true;
			}

			return InvisibleBaseTexture;
		}

		// Copies the pre-made transparent placeholder VTF (Assets/materials/tools/envmapinvisible.vtf) into
		// pk3Dir so an env-map-only material's $basetexture resolves.
		private void CopyInvisibleBaseTexture()
		{
			var relativePath = InvisibleBaseTexture.Replace('/', Path.DirectorySeparatorChar) + ".vtf";
			FileUtil.CopyBuiltinMaterialAsset(relativePath, pk3Dir);
		}

		// Copies content from the Q3Content folder, falling back to the user-managed CustomContent
		// folder for assets the map depends on but doesn't bundle.
		private void TryCopyQ3Content(string texturePath)
		{
			// Every image routed through here is referenced by a material (shader base/detail/spheremap stages,
			// default lit textures, live liquid/detail layers), so mark it needed for the texture pass. This is
			// also the copyExternalContent callback the flipbook/detail converters use, so their live-referenced
			// source images are captured too.
			RecordReferencedTexture(texturePath);

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
			bool IsBaseCandidate(ShaderStage x) => IsTextureStage(x) && !IsDepthPrimingStage(x) && !ShaderStageUtils.IsOverlayBlend(x);
			return stages.FirstOrDefault(x => IsBaseCandidate(x) && !IsAnimatedStage(x))
				?? stages.FirstOrDefault(x => IsTextureStage(x) && !IsDepthPrimingStage(x) && !IsAnimatedStage(x) && !ShaderStageUtils.IsAlphaBlend(x))
				?? stages.FirstOrDefault(IsBaseCandidate)
				?? stages.FirstOrDefault(x => IsTextureStage(x) && !IsDepthPrimingStage(x))
				?? stages.FirstOrDefault(IsTextureStage)
				?? stages.FirstOrDefault(x => !IsDepthPrimingStage(x))
				?? stages.FirstOrDefault();
		}

		// A stage that animates over time - a multi-frame animMap, texcoords that move
		// (scroll/rotate/stretch/turbulence), or a color/alpha driven by a waveform (e.g. a pulsing/
		// flickering glow via "rgbGen wave") - i.e. an effect layer rather than a static base surface.
		private static bool IsAnimatedStage(ShaderStage stage)
		{
			return stage.bundles[0].numImageAnimations > 1 ||
				stage.bundles[0].texMods.Any(t =>
					t.type == TexMod.TMOD_SCROLL || t.type == TexMod.TMOD_ROTATE ||
					t.type == TexMod.TMOD_STRETCH || t.type == TexMod.TMOD_TURBULENT) ||
				(stage.rgbGen == ColorGen.CGEN_WAVEFORM && stage.rgbWave.func != GenFunc.GF_NONE) ||
				(stage.alphaGen == AlphaGen.AGEN_WAVEFORM && stage.alphaWave.func != GenFunc.GF_NONE);
		}

		// Finds a static, unlit additive overlay ("blendfunc GL_ONE GL_ONE") sitting on an opaque base - the Q3 glow-
		// map idiom (a self-illuminated overlay added over the lightmapped surface). Returns that overlay stage so it
		// can be emitted as a post-lighting $detail (see AppendShaderParameters). Only fires when the chosen base is
		// opaque and exactly one such overlay exists, since $detail has a single slot; animated glows are left to the
		// flipbook baker.
		private static ShaderStage? GetSelfIllumOverlayStage(IEnumerable<ShaderStage> stages, ShaderStage? baseStage)
		{
			if (baseStage == null || !ShaderStageUtils.IsOpaqueBlend(baseStage))
				return null;

			var overlays = stages
				.Where(s => s != baseStage &&
					s.bundles[0].tcGen != TexCoordGen.TCGEN_ENVIRONMENT_MAPPED &&
					!string.IsNullOrEmpty(s.bundles[0].images[0]) &&
					ShaderStageUtils.IsAdditiveBlend(s) &&
					!IsAnimatedStage(s))
				.ToList();

			return overlays.Count == 1 ? overlays[0] : null;
		}

		private void AppendShaderParameters(StringBuilder sb, Shader shader)
		{
			var stages = shader.GetImageStages();
			var textureStage = GetTextureStage(stages);

			// An "env-map-only" shader (e.g. Q3 chrome/glass) has no plain diffuse stage, so GetTextureStage
			// falls back to the tcGen-environment stage. Reusing that reflection texture as the diffuse
			// $basetexture would double-draw it. A VMT still needs a $basetexture to load, and our spheremap
			// shader path wants a black (non-contributing) albedo, so point it at a tiny fully-transparent
			// texture - sampled without $translucent its rgb reads as black, letting $spheremap supply the
			// only visible color.
			var isEnvMapOnly = textureStage != null && textureStage.bundles[0].tcGen == TexCoordGen.TCGEN_ENVIRONMENT_MAPPED;

			// Reverse-alpha Q3 chrome (diffuse drawn "blendFunc GL_ONE_MINUS_SRC_ALPHA GL_SRC_ALPHA" over an
			// opaque reflection) needs the base texture's alpha inverted so $basealphaenvmapmask (which masks
			// by 1 - alpha) yields the refl*alpha weighting. TextureConverter bakes the inverted alpha into this
			// texture's VTF; ReverseAlphaChromeTextures.BaseTextureName gives the name to reference - the texture
			// itself (inverted in place) unless it's also used non-inverted elsewhere, where it gets a suffixed copy.
			var isReverseAlphaChrome = !noEnvMap && textureStage != null && !isEnvMapOnly &&
				ShaderStageUtils.IsReverseAlphaBlend(textureStage) &&
				stages.Any(s => s.bundles[0].tcGen == TexCoordGen.TCGEN_ENVIRONMENT_MAPPED && ShaderStageUtils.IsOpaqueBlend(s));

			if (textureStage != null)
			{
				var texture = isEnvMapOnly ? GetInvisibleBaseTexture() : Path.ChangeExtension(textureStage.bundles[0].images[0], null);
				if (isReverseAlphaChrome)
					texture = reverseAlphaChrome.BaseTextureName(texture);
				sb.AppendLine(CultureInfo.InvariantCulture, $"\t$basetexture \"{texture}\"");

				if (!isEnvMapOnly && textureStage.rgbGen.HasFlag(ColorGen.CGEN_CONST))
				{
					var color = textureStage.constantColor;
					var colorStr = $"{color[0]} {color[1]} {color[2]}";
					sb.AppendLine("\t$color \"{" + colorStr + "}\"");
				}

				if (!isEnvMapOnly && textureStage.alphaGen.HasFlag(AlphaGen.AGEN_CONST))
				{
					var alpha = (float)textureStage.constantColor[3] / 255;
					sb.AppendLine(CultureInfo.InvariantCulture, $"\t$alpha {alpha}");
				}
			}

			var envMapStage = noEnvMap ? null : stages.FirstOrDefault(x => x.bundles[0].tcGen == TexCoordGen.TCGEN_ENVIRONMENT_MAPPED);

			// A Q3 lit surface with a static, unlit additive overlay - a glow map drawn "blendfunc GL_ONE GL_ONE"
			// over the lightmapped base (e.g. xmetalfloor_wall_14b_ht3) - maps to a $detail layer combined post-
			// lighting via $detailblendmode 5 (TCOMBINE_RGB_ADDITIVE_SELFILLUM): the engine adds the glow texel on
			// top of the lit base color, exactly like Q3's additive pass. $detailscale 1 aligns the glow 1:1 with
			// the base's texcoords (it shares the base UVs). Skipped when the shader also carries a spheremap
			// reflection, since $detail there would collide with the env-map's own detail usage.
			var selfIllumStage = envMapStage == null ? GetSelfIllumOverlayStage(stages, textureStage) : null;
			if (selfIllumStage != null)
			{
				var detailTexture = Path.ChangeExtension(selfIllumStage.bundles[0].images[0], null);
				sb.AppendLine(CultureInfo.InvariantCulture, $"\t$detail \"{detailTexture}\"");
				sb.AppendLine("\t$detailscale 1");
				sb.AppendLine("\t$detailblendmode 5");
				sb.AppendLine("\t$detailblendfactor 1");
			}

			if (envMapStage != null)
			{
				// Emit $spheremap (+ scale, lightmap dimming, base-alpha reflection mask). Q3 chrome draws the
				// reflection as the opaque base with the diffuse alpha-blended over it, so the diffuse alpha
				// masks how much reflection shows; $basealphaenvmapmask reproduces that when the env stage is
				// the opaque base beneath a (forward or reverse) alpha-blended diffuse. Reverse-blend textures
				// have their VTF alpha inverted by TextureConverter so the single (1-alpha) param still applies.
				var hasBaseAlphaMask = textureStage != null && !isEnvMapOnly && ShaderStageUtils.IsOpaqueBlend(envMapStage) &&
					(ShaderStageUtils.IsAlphaBlend(textureStage) || ShaderStageUtils.IsReverseAlphaBlend(textureStage));
				AppendSpheremapParameters(sb, envMapStage, ShaderStageUtils.HasLightmapStage(shader), hasBaseAlphaMask);

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
				var hasOpaqueBaseBeneath = stages.Any(s => s != textureStage && ShaderStageUtils.IsOpaqueBlend(s) && !IsDepthPrimingStage(s));
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
			{
				sb.AppendLine("\t$translucent 1");

				// An env-map-only shader (Q3 glass: an alpha-blended "tcGen environment" stage and nothing else)
				// gets the 1x1 placeholder as its $basetexture. $translucent turns on src-alpha blending, but the
				// alpha it blends with comes from the base texture, and the placeholder carries no alpha channel -
				// so the surface blends at full opacity and the reflection hides whatever is behind the glass.
				// Q3 instead blends that stage by its own texture's alpha, and the spheremap never feeds the
				// surface alpha, so pass the reflection texture's opacity along as a constant $alpha.
				if (isEnvMapOnly)
					AppendEnvMapOnlyAlpha(sb, textureStage!);
			}

			if (textureStage != null && textureStage.bundles[0].texMods.Any(y => y.type == TexMod.TMOD_SCROLL || y.type == TexMod.TMOD_ROTATE ||
				y.type == TexMod.TMOD_STRETCH || y.type == TexMod.TMOD_SCALE))
				ConvertTexMods(sb, textureStage);
		}

		// Emits the surface opacity of an alpha-blended env-map-only stage as a constant $alpha, taken from the
		// mean alpha of its reflection texture. Q3 blends such a stage per-pixel by the texture's alpha sampled
		// at the reflection coords; Source's spheremap path only ever takes the surface alpha from $basetexture,
		// so a single constant stands in - exact for the uniform-alpha textures Q3 glass shaders use, an average
		// otherwise. Skipped when the texture is opaque, which needs no blending anyway.
		private void AppendEnvMapOnlyAlpha(StringBuilder sb, ShaderStage envStage)
		{
			var alpha = GetAverageAlpha(Path.ChangeExtension(envStage.bundles[0].images[0], null));
			if (alpha < 1f)
				sb.AppendLine(CultureInfo.InvariantCulture, $"\t$alpha {alpha}");
		}

		// Mean alpha (0-1) of a shader texture. Returns 1 (opaque) when the image can't be found or read, or
		// when it has no alpha channel - both leave the surface at its unmodulated opacity.
		private float GetAverageAlpha(string texturePath)
		{
			if (averageAlphaCache.TryGetValue(texturePath, out var cached))
				return cached;

			var average = 1f;
			var imagePath = ResolveImagePath(texturePath);
			if (imagePath != null && File.Exists(imagePath))
			{
				try
				{
					using var image = Image.Load<Rgba32>(imagePath);
					ulong total = 0;
					image.ProcessPixelRows(accessor =>
					{
						for (var y = 0; y < accessor.Height; y++)
						{
							foreach (ref var pixel in accessor.GetRowSpan(y))
								total += pixel.A;
						}
					});

					average = (float)total / (image.Width * image.Height * 255f);
				}
				catch (Exception)
				{
					// Unreadable image - fall back to opaque rather than failing the conversion
				}
			}

			averageAlphaCache[texturePath] = average;
			return average;
		}

		// Emits the spheremap reflection params for a Q3 "tcGen environment" shader. Shared by the live
		// material path and FlipbookConverter's baked-animation path so both reproduce the reflection
		// identically. The env stage's image is the reflection texture; a "tcMod scale" on it maps to
		// $spheremapscale (else the shader's identity [1 1]). hasLightmap forwards Q3's lightmap dimming of
		// the reflection ($envmaplightscale), and hasBaseAlphaMask masks the reflection by the base texture
		// alpha ($basealphaenvmapmask) for the chrome-under-alpha-diffuse idiom.
		internal static void AppendSpheremapParameters(StringBuilder sb, ShaderStage envMapStage, bool hasLightmap, bool hasBaseAlphaMask)
		{
			// Q3's "tcGen environment" is a spheremap (a flat 2D texture projected via per-vertex reflection
			// coords - see ioq3 RB_CalcEnvironmentTexCoords), not a cubemap, so route it to $spheremap.
			var sphereTexture = Path.ChangeExtension(envMapStage.bundles[0].images[0], null);
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t$spheremap \"{sphereTexture}\"");

			var sphereScale = envMapStage.bundles[0].texMods.FirstOrDefault(t => t.type == TexMod.TMOD_SCALE);
			if (sphereScale != null)
				sb.AppendLine(CultureInfo.InvariantCulture, $"\t$spheremapscale \"[{sphereScale.scale[0]} {sphereScale.scale[1]}]\"");

			if (hasLightmap)
				sb.AppendLine("\t$envmaplightscale 1");

			if (hasBaseAlphaMask)
				sb.AppendLine("\t$basealphaenvmapmask 1");
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
