using sourcepp.vtfpp;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BSPConvert.Lib.GoldSrc
{
	// How a brush entity's rendermode blends its surfaces
	public enum BlendMode
	{
		Opaque,
		// kRenderTransTexture (and kRenderTransColor, approximated): blended by renderamt
		Translucent,
		// kRenderTransAdd: added, scaled by renderamt
		Additive
	}

	// How a converted texture's VTF animates
	public enum TextureAnimation
	{
		None,
		// An animated texture sequence, played at GoldSrc's 10 frames per second
		Sequence,
		// Baked water warp (see GoldSrcMaterialConverter.ConvertWarped)
		Warp
	}

	// Converts GoldSrc miptextures to a VTF and VMT each, written under the content directory as
	// <contentDir>/<materialName>.vtf/.vmt (the layout SourceBspBuilder reads texdata from).
	public class GoldSrcMaterialConverter
	{
		// Palette index drawn transparent on '{' textures
		private const byte TransparentIndex = 255;
		// GoldSrc advances animated textures at a fixed 10 frames per second
		private const int AnimationFrameRate = 10;

		// GoldSrc brightens the textures of BSPs, WADs and studio models as it loads them, apart from masked ones ('{'
		// textures and masked model textures), to (c / 255)^(texgamma / gamma) with the default texgamma of 2 and gamma
		// of 2.5 (BuildGammaTable, and Image_SetPalette's LUMP_TEXGAMMA in Xash). Its lightmaps go through a gamma table
		// that's about the identity at the defaults, so they're converted without one.
		private static readonly byte[] TexGammaTable = Enumerable.Range(0, 256)
			.Select(i => (byte)Math.Clamp((int)(Math.Pow(i / 255.0, 2.0 / 2.5) * 255.0), 0, 255))
			.ToArray();

		// GoldSrc blends translucent surfaces in gamma space, where Strata, rendering in HDR, blends in linear space. That
		// makes a dark translucent surface darken what's behind it much less, and a bright one brighten it more. So a
		// translucent material's alpha makes the blend match GoldSrc's in front of mid grey, by its texture's average
		// brightness (see GetLinearBlendAlpha).
		private const float BlendReferenceBackground = 0.5f;
		private const float DisplayGamma = 2.2f;

		// GoldSrc maps water textures to repeat every 64 texels whatever their size, and ignores the face's texture
		// offset (R_TextureCoord)
		private const int WaterRepeatSize = 64;
		// How far water warps, in repeats: 8 texels (EmitWaterPolys)
		private const float WarpAmplitude = 8f / WaterRepeatSize;
		// The baked warp's texture is this many repeats wide and high (see ConvertWarped)
		private const int WarpRepeats = 4;
		// Frames baked per phase step, which play at about 10 frames per second
		private const int WarpFrames = 16;
		// Larger textures are scaled down to this size per repeat, which keeps a baked warp's VTF to a few MB
		private const int MaxWarpRepeatSize = 128;

		private static readonly VTF.CreationOptions VtfOptions = new VTF.CreationOptions
		{
			Version = 6,
			CompressionLevel = 0,
			OutputFormat = ImageFormat.STRATA_BC7,
			// GoldSrc textures are multiples of 16 but often not powers of two. Keep their size so they stay sharp;
			// the VMT's mapping size keeps texture coordinates right whatever the VTF's size.
			WidthResizeMethod = ImageConversion.ResizeMethod.NONE,
			HeightResizeMethod = ImageConversion.ResizeMethod.NONE,
			ComputeMips = 1,
			ComputeThumbnail = 1,
			ComputeReflectivity = 1,
			ComputeTransparencyFlags = 1,
		};

		// Skybox faces are clamped so filtering doesn't wrap around to the opposite edge and leave seams, and drawn
		// at about their full size, so like Valve's skyboxes they don't need mips
		private static readonly VTF.CreationOptions SkyboxVtfOptions = new VTF.CreationOptions
		{
			Version = 6,
			CompressionLevel = 0,
			OutputFormat = ImageFormat.STRATA_BC7,
			WidthResizeMethod = ImageConversion.ResizeMethod.POWER_OF_TWO_BIGGER,
			HeightResizeMethod = ImageConversion.ResizeMethod.POWER_OF_TWO_BIGGER,
			VTFFlags = VTF.Flags.V0_CLAMP_S | VTF.Flags.V0_CLAMP_T | VTF.Flags.V0_NO_MIP | VTF.Flags.V0_NO_LOD,
			ComputeMips = 0,
			ComputeThumbnail = 1,
			ComputeReflectivity = 1,
		};

		// Model textures are mapped from 0 to 1 whatever their size, so they can be resized to the powers of two block
		// compression needs
		private static readonly VTF.CreationOptions ModelVtfOptions = new VTF.CreationOptions
		{
			Version = 6,
			CompressionLevel = 0,
			OutputFormat = ImageFormat.STRATA_BC7,
			WidthResizeMethod = ImageConversion.ResizeMethod.POWER_OF_TWO_BIGGER,
			HeightResizeMethod = ImageConversion.ResizeMethod.POWER_OF_TWO_BIGGER,
			ComputeMips = 1,
			ComputeThumbnail = 1,
			ComputeReflectivity = 1,
			ComputeTransparencyFlags = 1,
		};

		private readonly string contentDir;
		private readonly List<string> writtenFiles = new List<string>();

		public GoldSrcMaterialConverter(string contentDir)
		{
			this.contentDir = contentDir;
		}

		// The VTFs and VMTs written so far
		public IReadOnlyList<string> WrittenFiles => writtenFiles;

		public bool Convert(string materialName, MipTexture texture)
		{
			var isAlphaTested = IsAlphaTested(texture.Name);
			var vtfPath = GetBasePath(materialName) + ".vtf";
			if (!VTF.Create(DecodeRGBA(texture, isAlphaTested), ImageFormat.RGBA8888, (ushort)texture.Width, (ushort)texture.Height, vtfPath, VtfOptions))
				return false;

			writtenFiles.Add(vtfPath);
			WriteVmt(materialName, materialName, texture, isAlphaTested, TextureAnimation.None);
			return true;
		}

		// A water texture with GoldSrc's warp baked into its frames. GoldSrc warps water as it draws it, moving each
		// vertex's texture coordinates to s + 8 sin(t / 8 + time) and t + 8 sin(s / 8 + time) (EmitWaterPolys), but only at
		// the vertices of the 64 unit grid it cuts water faces into (GL_SubdivideSurface), so the warp is linear in
		// between, and its phase steps by 8 radians from one grid line to the next. On a texture at scale 1 the grid lines
		// are a repeat apart, so the rows of repeat j shift sideways by 8 sin(8j + time) texels, the columns of repeat i
		// shift by 8 sin(8i + time), and the shift is linear in between.
		// The baked texture is WarpRepeats repeats wide and high, so its phase steps by 2π / WarpRepeats (π/2, near 8
		// radians less 2π) per repeat and it tiles. Moving the time on by a phase step moves the warp by a repeat
		// diagonally, so only one step's frames are baked, and the material moves them a repeat at a time (see
		// CreateVmt).
		// TODO: Textures at other scales, whose grid lines aren't a repeat apart, and func_water's waves (its scale),
		// which move the vertices up and down
		public bool ConvertWarped(string materialName, MipTexture texture)
		{
			var isAlphaTested = IsAlphaTested(texture.Name);
			var pixels = DecodeRGBA(texture, isAlphaTested);
			var repeatWidth = Math.Min(texture.Width, MaxWarpRepeatSize);
			var repeatHeight = Math.Min(texture.Height, MaxWarpRepeatSize);
			var width = (ushort)(repeatWidth * WarpRepeats);
			var height = (ushort)(repeatHeight * WarpRepeats);
			var vtfPath = GetBasePath(materialName) + ".vtf";

			using (var vtf = new VTF())
			{
				vtf.Version = VtfOptions.Version;
				vtf.ImageWidthResizeMethod = VtfOptions.WidthResizeMethod;
				vtf.ImageHeightResizeMethod = VtfOptions.HeightResizeMethod;
				for (var frame = 0; frame < WarpFrames; frame++)
				{
					// The first frame sets the size the frames are allocated with
					var framePixels = BakeWarpFrame(pixels, texture.Width, texture.Height, repeatWidth, repeatHeight, frame);
					if (!vtf.SetImage(framePixels, ImageFormat.RGBA8888, width, height, frame: (ushort)frame) ||
						(frame == 0 && !vtf.SetFrameCount(WarpFrames)))
						return false;
				}

				vtf.ComputeReflectivity();
				vtf.SetRecommendedMipCount();
				vtf.ComputeMips();
				vtf.SetFormat(VtfOptions.OutputFormat);
				vtf.ComputeTransparencyFlags();
				if (!vtf.Bake(vtfPath))
					return false;
			}

			writtenFiles.Add(vtfPath);
			WriteVmt(materialName, materialName, texture, isAlphaTested, TextureAnimation.Warp);
			return true;
		}

		// One frame of the warp (see ConvertWarped), WarpRepeats by WarpRepeats repeats of repeatWidth by repeatHeight
		// texels each
		private static byte[] BakeWarpFrame(byte[] pixels, int textureWidth, int textureHeight, int repeatWidth, int repeatHeight, int frame)
		{
			var phaseStep = 2f * MathF.PI / WarpRepeats;
			var time = phaseStep * frame / WarpFrames;

			// How far each grid line shifts, in repeats, with the first repeated past the end to interpolate to
			var lineShifts = new float[WarpRepeats + 1];
			for (var i = 0; i <= WarpRepeats; i++)
				lineShifts[i] = WarpAmplitude * MathF.Sin(phaseStep * i + time);

			// A texture scaled down averages the texels each of its texels covers
			var samplesX = (textureWidth + repeatWidth - 1) / repeatWidth;
			var samplesY = (textureHeight + repeatHeight - 1) / repeatHeight;
			var width = repeatWidth * WarpRepeats;
			var height = repeatHeight * WarpRepeats;
			var result = new byte[width * height * 4];
			Span<float> sum = stackalloc float[4];
			Span<float> sample = stackalloc float[4];
			for (var y = 0; y < height; y++)
			{
				for (var x = 0; x < width; x++)
				{
					sum.Clear();
					for (var sy = 0; sy < samplesY; sy++)
					{
						for (var sx = 0; sx < samplesX; sx++)
						{
							// Where the texel is, in repeats, and where the warp moves it to
							var u = (x + (sx + 0.5f) / samplesX) / repeatWidth;
							var v = (y + (sy + 0.5f) / samplesY) / repeatHeight;
							var warpedU = u + InterpolateLineShift(lineShifts, v);
							var warpedV = v + InterpolateLineShift(lineShifts, u);
							SampleBilinear(pixels, textureWidth, textureHeight, warpedU * textureWidth, warpedV * textureHeight, sample);
							for (var c = 0; c < 4; c++)
								sum[c] += sample[c];
						}
					}

					var samples = samplesX * samplesY;
					for (var c = 0; c < 4; c++)
						result[(y * width + x) * 4 + c] = (byte)Math.Clamp((int)MathF.Round(sum[c] / samples), 0, 255);
				}
			}

			return result;
		}

		// The shift between the grid lines either side of a position in repeats
		private static float InterpolateLineShift(float[] lineShifts, float position)
		{
			var line = Math.Clamp((int)position, 0, WarpRepeats - 1);
			return lineShifts[line] + (lineShifts[line + 1] - lineShifts[line]) * (position - line);
		}

		// Bilinearly filters a tiling RGBA image at a position in texels
		private static void SampleBilinear(byte[] pixels, int width, int height, float x, float y, Span<float> result)
		{
			x -= 0.5f;
			y -= 0.5f;
			var x0 = (int)MathF.Floor(x);
			var y0 = (int)MathF.Floor(y);
			var fx = x - x0;
			var fy = y - y0;
			x0 = ((x0 % width) + width) % width;
			y0 = ((y0 % height) + height) % height;
			var x1 = (x0 + 1) % width;
			var y1 = (y0 + 1) % height;
			for (var c = 0; c < 4; c++)
			{
				var top = pixels[(y0 * width + x0) * 4 + c] * (1f - fx) + pixels[(y0 * width + x1) * 4 + c] * fx;
				var bottom = pixels[(y1 * width + x0) * 4 + c] * (1f - fx) + pixels[(y1 * width + x1) * 4 + c] * fx;
				result[c] = top * (1f - fy) + bottom * fy;
			}
		}

		// An animated texture sequence ("+0name", "+1name", ...): the frames go into one VTF stored under the first
		// frame's material, and every frame's material plays it at GoldSrc's 10 frames per second. GoldSrc picks the
		// frame from the clock, not from the frame a surface uses, so all of them show the same frame at a time.
		public bool ConvertAnimated(IReadOnlyList<string> frameMaterialNames, IReadOnlyList<MipTexture> frames)
		{
			var first = frames[0];
			var isAlphaTested = IsAlphaTested(first.Name);
			var vtfMaterialName = frameMaterialNames[0];
			var vtfPath = GetBasePath(vtfMaterialName) + ".vtf";

			using (var vtf = new VTF())
			{
				vtf.Version = VtfOptions.Version;
				vtf.ImageWidthResizeMethod = VtfOptions.WidthResizeMethod;
				vtf.ImageHeightResizeMethod = VtfOptions.HeightResizeMethod;
				var width = (ushort)first.Width;
				var height = (ushort)first.Height;
				if (!vtf.SetImage(DecodeRGBA(first, isAlphaTested), ImageFormat.RGBA8888, width, height) ||
					!vtf.SetFrameCount((ushort)frames.Count))
					return false;

				for (var i = 1; i < frames.Count; i++)
				{
					if (!vtf.SetImage(DecodeRGBA(frames[i], isAlphaTested), ImageFormat.RGBA8888, width, height, frame: (ushort)i))
						return false;
				}

				vtf.ComputeReflectivity();
				vtf.SetRecommendedMipCount();
				vtf.ComputeMips();
				vtf.SetFormat(VtfOptions.OutputFormat);
				vtf.ComputeTransparencyFlags();
				if (!vtf.Bake(vtfPath))
					return false;
			}

			writtenFiles.Add(vtfPath);
			foreach (var materialName in frameMaterialNames)
				WriteVmt(materialName, vtfMaterialName, first, isAlphaTested, TextureAnimation.Sequence);

			return true;
		}

		// A texture and the alternate texture its brush entity switches to, as the two frames of one VTF that
		// ToggleTexture materials pick from (see WriteVariant). Returns false if they aren't the same size.
		public bool ConvertToggled(string vtfMaterialName, MipTexture texture, MipTexture alternate)
		{
			if (texture.Width != alternate.Width || texture.Height != alternate.Height)
				return false;

			var isAlphaTested = IsAlphaTested(texture.Name);
			var vtfPath = GetBasePath(vtfMaterialName) + ".vtf";
			using (var vtf = new VTF())
			{
				vtf.Version = VtfOptions.Version;
				vtf.ImageWidthResizeMethod = VtfOptions.WidthResizeMethod;
				vtf.ImageHeightResizeMethod = VtfOptions.HeightResizeMethod;
				var width = (ushort)texture.Width;
				var height = (ushort)texture.Height;
				if (!vtf.SetImage(DecodeRGBA(texture, isAlphaTested), ImageFormat.RGBA8888, width, height) ||
					!vtf.SetFrameCount(2) ||
					!vtf.SetImage(DecodeRGBA(alternate, IsAlphaTested(alternate.Name)), ImageFormat.RGBA8888, width, height, frame: 1))
					return false;

				vtf.ComputeReflectivity();
				vtf.SetRecommendedMipCount();
				vtf.ComputeMips();
				vtf.SetFormat(VtfOptions.OutputFormat);
				vtf.ComputeTransparencyFlags();
				if (!vtf.Bake(vtfPath))
					return false;
			}

			writtenFiles.Add(vtfPath);
			return true;
		}

		// A sprite as a Sprite material with a frame per sprite frame, which env_sprite plays at its framerate. Its
		// rendermode comes from the entity, as in GoldSrc.
		public bool ConvertSprite(string materialName, GoldSrcSprite sprite)
		{
			var basePath = GetBasePath(materialName);
			using (var vtf = new VTF())
			{
				vtf.Version = VtfOptions.Version;
				vtf.ImageWidthResizeMethod = VtfOptions.WidthResizeMethod;
				vtf.ImageHeightResizeMethod = VtfOptions.HeightResizeMethod;
				var width = (ushort)sprite.Width;
				var height = (ushort)sprite.Height;
				for (var i = 0; i < sprite.Frames.Count; i++)
				{
					var pixels = sprite.Frames[i];
					// Transparent texels take the color of their neighbors, so filtering doesn't darken the edges
					if (sprite.TextureFormat != SpriteTextureFormat.Additive)
						BleedIntoTransparentPixels(pixels, sprite.Width, sprite.Height);

					// The first frame sets the size the frames are allocated with
					if (!vtf.SetImage(pixels, ImageFormat.RGBA8888, width, height, frame: (ushort)i) ||
						(i == 0 && !vtf.SetFrameCount((ushort)sprite.Frames.Count)))
						return false;
				}

				// Sprites are drawn once, so their edges shouldn't wrap around
				vtf.AddFlags(VTF.Flags.V0_CLAMP_S | VTF.Flags.V0_CLAMP_T);
				vtf.ComputeReflectivity();
				vtf.SetRecommendedMipCount();
				vtf.ComputeMips();
				vtf.SetFormat(VtfOptions.OutputFormat);
				vtf.ComputeTransparencyFlags();
				if (!vtf.Bake(basePath + ".vtf"))
					return false;
			}

			writtenFiles.Add(basePath + ".vtf");

			var vmt = new StringBuilder();
			vmt.AppendLine("\"Sprite\"");
			vmt.AppendLine("{");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$basetexture\" \"{materialName}\"");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$spriteorientation\" \"{GetSpriteOrientation(sprite.Type)}\"");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$spriteorigin\" \"[{sprite.OriginX:0.####} {sprite.OriginY:0.####}]\"");
			vmt.AppendLine("}");
			File.WriteAllText(basePath + ".vmt", vmt.ToString());
			writtenFiles.Add(basePath + ".vmt");

			return true;
		}

		private static string GetSpriteOrientation(SpriteType type)
		{
			return type switch
			{
				SpriteType.FacingUpright => "facing_upright",
				SpriteType.Parallel => "vp_parallel",
				SpriteType.Oriented => "oriented",
				SpriteType.ParallelOriented => "vp_parallel_oriented",
				_ => "parallel_upright",
			};
		}

		// A studio model's texture, lit like a model unless it's fullbright. Masked textures are alpha tested and
		// additive ones added, as GoldSrc draws them whatever the entity's rendermode.
		// TODO: Chrome textures, which GoldSrc maps by the view direction
		public bool ConvertModelTexture(string materialName, GoldSrcModel.Texture texture)
		{
			var basePath = GetBasePath(materialName);
			var isMasked = (texture.Flags & GoldSrcModel.STUDIO_NF_MASKED) != 0;
			var pixels = (byte[])texture.Pixels.Clone();
			if (isMasked)
			{
				BleedIntoTransparentPixels(pixels, texture.Width, texture.Height);
			}
			else
			{
				for (var i = 0; i < pixels.Length; i++)
				{
					if (i % 4 != 3)
						pixels[i] = TexGammaTable[pixels[i]];
				}
			}

			if (!VTF.Create(pixels, ImageFormat.RGBA8888, (ushort)texture.Width, (ushort)texture.Height, basePath + ".vtf", ModelVtfOptions))
				return false;

			writtenFiles.Add(basePath + ".vtf");

			var isFullbright = (texture.Flags & GoldSrcModel.STUDIO_NF_FULLBRIGHT) != 0;
			var vmt = new StringBuilder();
			vmt.AppendLine(isFullbright ? "\"UnlitGeneric\"" : "\"VertexLitGeneric\"");
			vmt.AppendLine("{");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$basetexture\" \"{materialName}\"");
			if (isMasked)
				vmt.AppendLine("\t\"$alphatest\" \"1\"");
			if ((texture.Flags & GoldSrcModel.STUDIO_NF_ADDITIVE) != 0)
				vmt.AppendLine("\t\"$additive\" \"1\"");
			vmt.AppendLine("}");
			File.WriteAllText(basePath + ".vmt", vmt.ToString());
			writtenFiles.Add(basePath + ".vmt");

			return true;
		}

		// An infodecal's texture as a Source decal material, the size GoldSrc draws it (a unit per texel). GoldSrc
		// blends decals over the surface by their alpha before its lightmap lights them, like a lightmapped translucent
		// decal. A decal from decals.wad whose last palette color isn't the transparent blue is a gradient: that color,
		// with each texel's palette index as its alpha (LUMP_GRADIENT in Xash). Others are masked like '{' textures.
		// Neither gets texture gamma.
		public bool ConvertDecal(string materialName, MipTexture texture, bool fromDecalWad)
		{
			var palette = texture.Palette;
			var isGradient = fromDecalWad && !(palette[255 * 3] == 0 && palette[255 * 3 + 1] == 0 && palette[255 * 3 + 2] == 255);
			byte[] pixels;
			if (isGradient)
			{
				pixels = new byte[texture.Width * texture.Height * 4];
				for (var i = 0; i < texture.Pixels.Length; i++)
				{
					pixels[i * 4] = palette[255 * 3];
					pixels[i * 4 + 1] = palette[255 * 3 + 1];
					pixels[i * 4 + 2] = palette[255 * 3 + 2];
					pixels[i * 4 + 3] = texture.Pixels[i];
				}
			}
			else
			{
				pixels = DecodeRGBA(texture, fromDecalWad || IsAlphaTested(texture.Name));
			}

			var basePath = GetBasePath(materialName);
			if (!VTF.Create(pixels, ImageFormat.RGBA8888, (ushort)texture.Width, (ushort)texture.Height, basePath + ".vtf", VtfOptions))
				return false;

			writtenFiles.Add(basePath + ".vtf");

			var vmt = new StringBuilder();
			vmt.AppendLine("\"LightmappedGeneric\"");
			vmt.AppendLine("{");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$basetexture\" \"{materialName}\"");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$mappingwidth\" \"{texture.Width}\"");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$mappingheight\" \"{texture.Height}\"");
			vmt.AppendLine("\t\"$decal\" \"1\"");
			vmt.AppendLine("\t\"$translucent\" \"1\"");
			vmt.AppendLine("}");
			File.WriteAllText(basePath + ".vmt", vmt.ToString());
			writtenFiles.Add(basePath + ".vmt");

			return true;
		}

		// A skybox face from a sky image (TGA or BMP), drawn fullbright like GoldSrc's sky
		public bool ConvertSkyboxFace(string materialName, string imagePath)
		{
			var basePath = GetBasePath(materialName);
			if (!VTF.Create(imagePath, basePath + ".vtf", SkyboxVtfOptions))
				return false;

			writtenFiles.Add(basePath + ".vtf");

			var vmt = new StringBuilder();
			vmt.AppendLine("\"UnlitGeneric\"");
			vmt.AppendLine("{");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$basetexture\" \"{materialName}\"");
			vmt.AppendLine("\t\"$nofog\" \"1\"");
			vmt.AppendLine("\t\"$ignorez\" \"1\"");
			vmt.AppendLine("}");
			File.WriteAllText(basePath + ".vmt", vmt.ToString());
			writtenFiles.Add(basePath + ".vmt");

			return true;
		}

		private string GetBasePath(string materialName)
		{
			var basePath = Path.Combine(contentDir, materialName.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
			return basePath;
		}

		// GoldSrc texture name without an animation or random tiling frame prefix ("+0", "+a", "-0")
		private static string GetBaseName(string textureName)
		{
			return textureName.Length > 2 && (textureName[0] == '+' || textureName[0] == '-') ? textureName.Substring(2) : textureName;
		}

		// Textures the engine draws as warped, unlit liquid (SURF_DRAWTURB). Animated textures ("+0water") aren't.
		public static bool IsTurbulent(string textureName)
		{
			return textureName.StartsWith('!') ||
				textureName.StartsWith("water", StringComparison.OrdinalIgnoreCase) ||
				textureName.StartsWith("laser", StringComparison.OrdinalIgnoreCase);
		}

		private static bool IsAlphaTested(string textureName)
		{
			return GetBaseName(textureName).StartsWith('{');
		}

		// A material drawing an already converted texture (vtfMaterialName's VTF) the way a brush entity does: blended
		// by its rendermode (amount is its renderamt as 0-1), scrolling at scrollSpeed, a func_conveyor's speed, and if
		// toggled, drawing the frame of the VTF (see ConvertToggled) that the entity's texture frame index picks
		public void WriteVariant(string materialName, string vtfMaterialName, MipTexture texture, TextureAnimation animation, BlendMode blendMode,
			float amount, float scrollSpeed, bool toggled)
		{
			WriteVmt(materialName, vtfMaterialName, texture, IsAlphaTested(texture.Name), animation, blendMode, amount, scrollSpeed, toggled);
		}

		private void WriteVmt(string materialName, string baseTexture, MipTexture texture, bool isAlphaTested, TextureAnimation animation,
			BlendMode blendMode = BlendMode.Opaque, float amount = 1f, float scrollSpeed = 0f, bool toggled = false)
		{
			var vmtPath = GetBasePath(materialName) + ".vmt";
			File.WriteAllText(vmtPath, CreateVmt(baseTexture, texture, isAlphaTested, animation, blendMode, amount, scrollSpeed, toggled));
			writtenFiles.Add(vmtPath);
		}

		private static byte[] DecodeRGBA(MipTexture texture, bool isAlphaTested)
		{
			var palette = texture.Palette;
			var pixels = new byte[texture.Width * texture.Height * 4];
			for (var i = 0; i < texture.Pixels.Length; i++)
			{
				var index = texture.Pixels[i];
				for (var c = 0; c < 3; c++)
				{
					var value = palette[index * 3 + c];
					pixels[i * 4 + c] = isAlphaTested ? value : TexGammaTable[value];
				}
				pixels[i * 4 + 3] = isAlphaTested && index == TransparentIndex ? (byte)0 : (byte)255;
			}

			if (isAlphaTested)
				BleedIntoTransparentPixels(pixels, texture.Width, texture.Height);

			return pixels;
		}

		// Transparent texels keep the palette's key color (usually pure blue), which bilinear filtering and mipmaps
		// blend into the visible edges. Give them the average color of their opaque neighbors instead, growing out
		// from the edges until every transparent texel has one.
		private static void BleedIntoTransparentPixels(byte[] pixels, int width, int height)
		{
			var filled = new bool[width * height];
			var anyOpaque = false;
			for (var i = 0; i < filled.Length; i++)
			{
				filled[i] = pixels[i * 4 + 3] != 0;
				anyOpaque |= filled[i];
			}

			if (!anyOpaque)
				return;

			var newlyFilled = new List<(int index, byte r, byte g, byte b)>();
			do
			{
				newlyFilled.Clear();
				for (var y = 0; y < height; y++)
				{
					for (var x = 0; x < width; x++)
					{
						var i = y * width + x;
						if (filled[i])
							continue;

						int r = 0, g = 0, b = 0, count = 0;
						for (var dy = -1; dy <= 1; dy++)
						{
							for (var dx = -1; dx <= 1; dx++)
							{
								// Textures tile, so neighbors wrap around
								var n = ((y + dy + height) % height) * width + (x + dx + width) % width;
								if (!filled[n])
									continue;

								r += pixels[n * 4];
								g += pixels[n * 4 + 1];
								b += pixels[n * 4 + 2];
								count++;
							}
						}

						if (count > 0)
							newlyFilled.Add((i, (byte)(r / count), (byte)(g / count), (byte)(b / count)));
					}
				}

				foreach (var (index, r, g, b) in newlyFilled)
				{
					pixels[index * 4] = r;
					pixels[index * 4 + 1] = g;
					pixels[index * 4 + 2] = b;
					filled[index] = true;
				}
			}
			while (newlyFilled.Count > 0);
		}

		private static string CreateVmt(string baseTexture, MipTexture texture, bool isAlphaTested, TextureAnimation animation, BlendMode blendMode,
			float amount, float scrollSpeed, bool toggled)
		{
			// Water isn't lightmapped in GoldSrc, and neither are translucent or additive brush entities
			var isTurbulent = IsTurbulent(texture.Name);
			var isUnlit = isTurbulent || blendMode != BlendMode.Opaque;
			var shader = isUnlit ? "UnlitGeneric" : "LightmappedGeneric";

			var vmt = new StringBuilder();
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\"{shader}\"");
			vmt.AppendLine("{");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$basetexture\" \"{baseTexture}\"");
			// Texture coordinates are in texels of the original texture, apart from water's (see WaterRepeatSize), and
			// a baked warp's texture is several repeats
			var mappingWidth = isTurbulent ? WaterRepeatSize : texture.Width;
			var mappingHeight = isTurbulent ? WaterRepeatSize : texture.Height;
			if (animation == TextureAnimation.Warp)
			{
				mappingWidth *= WarpRepeats;
				mappingHeight *= WarpRepeats;
			}
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$mappingwidth\" \"{mappingWidth}\"");
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$mappingheight\" \"{mappingHeight}\"");
			switch (blendMode)
			{
				case BlendMode.Opaque:
					if (isAlphaTested)
						vmt.AppendLine("\t\"$alphatest\" \"1\"");
					break;
				case BlendMode.Translucent:
					// Blends by the texture's alpha too, so '{' texels stay transparent
					vmt.AppendLine("\t\"$translucent\" \"1\"");
					var alpha = GetLinearBlendAlpha(texture, isAlphaTested, amount);
					if (alpha < 1f)
						vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$alpha\" \"{alpha:0.###}\"");
					break;
				case BlendMode.Additive:
					// renderamt scales how much is added
					vmt.AppendLine("\t\"$additive\" \"1\"");
					if (isAlphaTested)
						vmt.AppendLine("\t\"$alphatest\" \"1\"");
					if (amount < 1f)
						vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$color\" \"[{amount:0.###} {amount:0.###} {amount:0.###}]\"");
					break;
			}
			if (animation == TextureAnimation.Warp)
			{
				vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$warpframes\" \"{WarpFrames}\"");
				vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"$warprepeats\" \"{WarpRepeats}\"");
				vmt.AppendLine("\t\"$warpoffset\" \"[0 0]\"");
				// Proxies only write variables the material has, and do float math on float ones
				foreach (var variable in WarpFloatVariables)
					vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\"{variable}\" \"0.0\"");
			}
			if (animation != TextureAnimation.None || scrollSpeed != 0f || toggled)
			{
				vmt.AppendLine("\t\"Proxies\"");
				vmt.AppendLine("\t{");
				if (animation == TextureAnimation.Warp)
					AppendWarpProxies(vmt);
				if (toggled)
				{
					// The frame index wraps, so env_texturetoggle's IncrementTextureIndex flips between the frames
					vmt.AppendLine("\t\t\"ToggleTexture\"");
					vmt.AppendLine("\t\t{");
					vmt.AppendLine("\t\t\t\"toggleTextureVar\" \"$basetexture\"");
					vmt.AppendLine("\t\t\t\"toggleTextureFrameNumVar\" \"$frame\"");
					vmt.AppendLine("\t\t\t\"toggleShouldWrap\" \"1\"");
					vmt.AppendLine("\t\t}");
				}
				if (animation == TextureAnimation.Sequence)
				{
					vmt.AppendLine("\t\t\"AnimatedTexture\"");
					vmt.AppendLine("\t\t{");
					vmt.AppendLine("\t\t\t\"animatedTextureVar\" \"$basetexture\"");
					vmt.AppendLine("\t\t\t\"animatedTextureFrameNumVar\" \"$frame\"");
					vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\t\t\"animatedTextureFrameRate\" \"{AnimationFrameRate}\"");
					vmt.AppendLine("\t\t}");
				}
				if (scrollSpeed != 0f)
				{
					// GoldSrc moves the texture coordinates back along S by the speed in texels per second (forward when
					// the conveyor runs backwards), whatever the texture's scale on the face
					vmt.AppendLine("\t\t\"TextureScroll\"");
					vmt.AppendLine("\t\t{");
					vmt.AppendLine("\t\t\t\"textureScrollVar\" \"$basetexturetransform\"");
					vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\t\t\"textureScrollRate\" \"{MathF.Abs(scrollSpeed) / texture.Width:0.#####}\"");
					vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\t\t\"textureScrollAngle\" \"{(scrollSpeed >= 0f ? 180 : 0)}\"");
					vmt.AppendLine("\t\t}");
				}
				vmt.AppendLine("\t}");
			}
			vmt.AppendLine("}");

			return vmt.ToString();
		}

		// The alpha that blends a texture in linear space over mid grey the way GoldSrc blends it by amount in gamma space
		// (see BlendReferenceBackground)
		private static float GetLinearBlendAlpha(MipTexture texture, bool isAlphaTested, float amount)
		{
			if (amount >= 1f)
				return 1f;

			var surface = GetAverageBrightness(texture, isAlphaTested);
			var background = BlendReferenceBackground;
			var target = MathF.Pow(amount * surface + (1f - amount) * background, DisplayGamma);
			var linearSurface = MathF.Pow(surface, DisplayGamma);
			var linearBackground = MathF.Pow(background, DisplayGamma);

			// A surface about as bright as the background looks the same at any alpha
			if (MathF.Abs(linearBackground - linearSurface) < 0.01f)
				return amount;

			return Math.Clamp((linearBackground - target) / (linearBackground - linearSurface), 0f, 1f);
		}

		// The average brightness (0-1, gamma space) of a texture's visible texels as GoldSrc draws them
		private static float GetAverageBrightness(MipTexture texture, bool isAlphaTested)
		{
			var palette = texture.Palette;
			double sum = 0;
			var count = 0;
			foreach (var index in texture.Pixels)
			{
				if (isAlphaTested && index == TransparentIndex)
					continue;

				int Channel(int c) => isAlphaTested ? palette[index * 3 + c] : TexGammaTable[palette[index * 3 + c]];
				sum += 0.2126 * Channel(0) + 0.7152 * Channel(1) + 0.0722 * Channel(2);
				count++;
			}

			return count > 0 ? (float)(sum / count / 255.0) : BlendReferenceBackground;
		}

		// Plays a baked warp (see ConvertWarped): the time counts phase steps, the fraction of the current step picks
		// the frame, and the texture moves by a repeat diagonally per step, wrapping after WarpRepeats of them. The
		// frame and offset both come from the same step count, so they change together.
		private static readonly string[] WarpFloatVariables = { "$warpstep", "$warpstepint", "$warpstepfrac", "$warpframe", "$warpcycle" };

		private static void AppendWarpProxies(StringBuilder vmt)
		{
			var stepsPerSecond = WarpRepeats / (2f * MathF.PI);
			AppendProxy(vmt, "LinearRamp", ("rate", stepsPerSecond.ToString("0.######", CultureInfo.InvariantCulture)), ("initialValue", "0"),
				("resultVar", "$warpstep"));
			AppendProxy(vmt, "Int", ("srcVar1", "$warpstep"), ("resultVar", "$warpstepint"));
			AppendProxy(vmt, "Subtract", ("srcVar1", "$warpstep"), ("srcVar2", "$warpstepint"), ("resultVar", "$warpstepfrac"));
			AppendProxy(vmt, "Multiply", ("srcVar1", "$warpstepfrac"), ("srcVar2", "$warpframes"), ("resultVar", "$warpframe"));
			// The fraction times the frame count can round up to the count
			AppendProxy(vmt, "Clamp", ("srcVar1", "$warpframe"), ("min", "0"), ("max", (WarpFrames - 1).ToString(CultureInfo.InvariantCulture)),
				("resultVar", "$frame"));
			AppendProxy(vmt, "Divide", ("srcVar1", "$warpstepint"), ("srcVar2", "$warprepeats"), ("resultVar", "$warpcycle"));
			AppendProxy(vmt, "Frac", ("srcVar1", "$warpcycle"), ("resultVar", "$warpoffset[0]"));
			AppendProxy(vmt, "Frac", ("srcVar1", "$warpcycle"), ("resultVar", "$warpoffset[1]"));
			AppendProxy(vmt, "TextureTransform", ("translateVar", "$warpoffset"), ("resultVar", "$basetexturetransform"));
		}

		private static void AppendProxy(StringBuilder vmt, string name, params (string key, string value)[] keys)
		{
			vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\t\"{name}\"");
			vmt.AppendLine("\t\t{");
			foreach (var (key, value) in keys)
				vmt.AppendLine(CultureInfo.InvariantCulture, $"\t\t\t\"{key}\" \"{value}\"");
			vmt.AppendLine("\t\t}");
		}
	}
}
