using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using static BSPConvert.Lib.ShaderStageUtils;

namespace BSPConvert.Lib
{
	// Converts Q3 liquid/effect shaders whose layers animate only by affine texture transforms - scrolling and/or
	// center-pivoted rotation, plus static scaling - into a live two-layer Source material ($basetexture + $detail)
	// driven by texture-transform proxies, instead of baking a looping flipbook. The continuous scroll/rotation has no
	// loop, so it avoids the flipbook's frame-budget collapse (slow, incommensurate rates compressing into a near-
	// static cycle). Tried before the flipbook baker; returns false to defer a shader it can't carry (animMaps,
	// stretch/waveforms, opaque non-animating bases, missing images) back to the baker.
	public class DetailMaterialConverter
	{
		private readonly string pk3Dir;
		private readonly FlipbookOptions options;
		private readonly Func<string, string?> resolveImagePath;
		private readonly bool noEnvMap;
		private readonly Action<string> copyExternalContent;

		public DetailMaterialConverter(string pk3Dir, FlipbookOptions options, Func<string, string?> resolveImagePath, bool noEnvMap, Action<string> copyExternalContent)
		{
			this.pk3Dir = pk3Dir;
			this.options = options;
			this.resolveImagePath = resolveImagePath;
			this.noEnvMap = noEnvMap;
			this.copyExternalContent = copyExternalContent;
		}

		// Converts the shader to a live base/detail material if it's a transform-animated (scroll/rotate) shader we can
		// carry. Returns false (deferring to the flipbook baker / normal material path) otherwise.
		public bool TryConvert(string textureName, Shader shader)
		{
			if (!options.enabled)
				return false;

			// Both lit and unlit shaders are carried here. Lit surfaces emit LightmappedGeneric (keeping their
			// lightmap), unlit ones UnlitGeneric - both drive the detail layer's own $detailtexturetransform so the
			// two layers animate independently.

			// Animated multi-pass stacks: a transform-animated shader converts to a live surface for every 2-layer
			// scroll/rotate stack, plus larger stacks whose flipbook loop would collapse. Anything else (stretch
			// pulses, waveforms, animMaps, short well-behaved loops) is left to the flipbook baker.
			if (IsAnimatedStackShader(shader, out var stages, out var mode))
			{
				if (IsCarryableStack(stages) && (stages.Count == 2 || WouldBakedLoopCollapse(stages)))
					return TryConvertDetailMaterial(textureName, shader, stages, mode);

				return false;
			}

			// Liquids that mix framebuffer blends (e.g. a brightening "GL_dst_color" ripple over a darkening
			// "GL_zero GL_src_color" surface) fit no single bake mode, so the stack baker skips them and they'd
			// otherwise fall through to a plain base material. Carry them here, translucent unless the shader's
			// lightmap stage is an opaque base beneath them (BrightenOrOpaque).
			var liquidStages = GetTextureStages(shader);
			if (IsTranslucentLiquidStack(liquidStages))
				return TryConvertDetailMaterial(textureName, shader, liquidStages, BrightenOrOpaque(shader));

			return false;
		}

		// True when every stage's reproducible animation is an affine texture transform the live surface can carry -
		// scrolling, center-pivoted rotation, or static scaling - with no animMap sequence, stretch pulse, or rgb/alpha
		// waveform. Source's texture-transform proxies drive each layer's own $basetexturetransform/$detailtexturetransform
		// (scroll via translate, rotation via rotate); turb shear has no affine equivalent and is ignored, exactly as the
		// flipbook baker already ignores it.
		private static bool IsCarryableStack(List<ShaderStage> stages)
		{
			return stages.All(s =>
				s.bundles[0].numImageAnimations <= 1 &&
				!(s.rgbGen == ColorGen.CGEN_WAVEFORM && s.rgbWave.frequency > 0f) &&
				!(s.alphaGen == AlphaGen.AGEN_WAVEFORM && s.alphaWave.frequency > 0f) &&
				!s.bundles[0].texMods.Any(t =>
					t.type == TexMod.TMOD_STRETCH && t.wave.frequency > 0f));
		}

		// Whether the flipbook bake of this stack would have to compress its loop into the frame budget (its true
		// seamless period exceeds maxFrames/fps). That compression is what makes slow liquids collapse into a near-
		// static single cycle, so it's the signal to fall back to the live $detail surface for larger stacks.
		private bool WouldBakedLoopCollapse(List<ShaderStage> stages)
		{
			var fps = Math.Clamp(options.fps, 1, 60);
			var maxFrames = Math.Clamp(options.maxFrames, 1, 4096);
			var rates = GatherStageRates(stages);
			if (rates.Count == 0)
				return false;

			var rateGcd = rates.Aggregate(RateGcd);
			var trueLoop = rateGcd <= 1e-4f ? float.MaxValue : 1f / rateGcd;
			return trueLoop > (float)maxFrames / fps;
		}

		// Cycles-per-second of every reproduced periodic element across the stages (mirrors FlipbookConverter's
		// GatherRates, which works on already-loaded layers; this reads the stages directly so the loop length can
		// be judged before any image is loaded).
		private static List<float> GatherStageRates(List<ShaderStage> stages)
		{
			var rates = new List<float>();
			foreach (var stage in stages)
			{
				var bundle = stage.bundles[0];
				if (bundle.numImageAnimations > 1)
					rates.Add(bundle.imageAnimationSpeed / bundle.numImageAnimations);

				foreach (var texMod in bundle.texMods)
				{
					if (texMod.type == TexMod.TMOD_STRETCH && texMod.wave.frequency > 0f)
						rates.Add(texMod.wave.frequency);
					else if (texMod.type == TexMod.TMOD_SCROLL)
					{
						if (MathF.Abs(texMod.scroll[0]) > 1e-4f)
							rates.Add(MathF.Abs(texMod.scroll[0]));
						if (MathF.Abs(texMod.scroll[1]) > 1e-4f)
							rates.Add(MathF.Abs(texMod.scroll[1]));
					}
					else if (texMod.type == TexMod.TMOD_ROTATE && MathF.Abs(texMod.rotateSpeed) > 1e-4f)
						rates.Add(MathF.Abs(texMod.rotateSpeed) / 360f);
				}

				if (stage.rgbGen == ColorGen.CGEN_WAVEFORM && stage.rgbWave.frequency > 0f)
					rates.Add(stage.rgbWave.frequency);
				if (stage.alphaGen == AlphaGen.AGEN_WAVEFORM && stage.alphaWave.frequency > 0f)
					rates.Add(stage.alphaWave.frequency);
			}
			return rates;
		}

		// A stack of framebuffer blends (brightening, multiplying, additive or alpha overlays) with no opaque stage
		// of its own and reproducible scrolling - a layered Q3 liquid (e.g. a "GL_dst_color" ripple over a
		// "GL_zero GL_src_color" surface). The mix of blend types fits no single flipbook bake mode, so the stack
		// baker skips them; they're routed straight to the live $basetexture+$detail surface instead. Whether the
		// result is actually translucent is BrightenOrOpaque's call - a lightmap stage can still be its base.
		private bool IsTranslucentLiquidStack(List<ShaderStage> stages)
		{
			return stages.Count >= 2
				&& !stages.Any(IsOpaqueBlend)
				&& stages.All(s => IsBrightenBlend(s) || IsMultiplyBlend(s) || IsAdditiveBlend(s) || IsAlphaBlend(s))
				&& AnimatesReproducibly(stages)
				&& IsCarryableStack(stages);
		}

		// Converts a transform-animated stack into a live two-layer material: the most surface-like stage becomes
		// $basetexture, the next becomes $detail, composited by the engine's detail texturing every frame. The detail
		// rides its own $detailtexturetransform (UnlitGeneric, gated in TryConvert), so both layers scroll and rotate
		// independently at their true Q3 rates. The continuous scroll/rotation has no loop, sidestepping the flipbook's
		// frame-budget collapse. Returns false (leaving the stack to the flipbook baker) when no usable base/detail
		// pair can be formed or an image is missing.
		private bool TryConvertDetailMaterial(string textureName, Shader shader, List<ShaderStage> stages, OutputMode mode)
		{
			if (!ChooseDetailLayers(shader, stages, mode, out var baseStage, out var detailStage))
				return false;

			var baseImg = GetStageImagePath(baseStage);
			var detailImg = GetStageImagePath(detailStage);
			if (baseImg == null || detailImg == null ||
				resolveImagePath(baseImg) == null || resolveImagePath(detailImg) == null)
				return false;

			// The material points at these source images by name, so make sure both are present in pk3Dir (copying
			// from external content if needed) for the texture pass to convert and embed them - otherwise the surface
			// renders as a missing white texture.
			copyExternalContent(baseImg);
			copyExternalContent(detailImg);

			WriteDetailMaterialVmt(textureName, shader, baseStage, detailStage, baseImg, detailImg, mode);
			return true;
		}

		// Picks which stage drives the surface ($basetexture) and which rides on it as $detail. The base is the most
		// surface-like stage (opaque > multiplicative/alpha tint > brightening > additive glow); the detail scrolls
		// and/or rotates on its own transform, so the base need not move so long as one of the two layers does.
		private bool ChooseDetailLayers(Shader shader, List<ShaderStage> stages, OutputMode mode, out ShaderStage baseStage, out ShaderStage detailStage)
		{
			baseStage = null!;
			detailStage = null!;

			var usable = stages.Where(s => GetStageImagePath(s) != null).ToList();
			if (usable.Count < 2)
				return false;

			// Among the most surface-like stages, prefer the last one drawn: Q3 replays the stack in order, so a
			// later opaque/self-tint stage (e.g. a floor's real diffuse texture layered over an earlier scrolling
			// caustic background pass) is what actually dominates the final pixel, not an earlier stage it draws over.
			var maxPriority = usable.Max(BasePriority);
			baseStage = usable.Last(s => BasePriority(s) == maxPriority);

			// An opaque-base stack must keep an opaque (or self-tinting, "GL_one GL_src_color") stage as
			// $basetexture. When the stack is opaque only because the shader's lightmap stage is its base, there is
			// no such texture stage to keep - Source's LightmappedGeneric supplies the lightmap itself - so the
			// priority pick above stands.
			if (mode == OutputMode.Opaque && !IsOpaqueBlend(baseStage) && !IsSelfTintBlend(baseStage))
			{
				var opaqueStage = usable.LastOrDefault(s => IsOpaqueBlend(s) || IsSelfTintBlend(s));
				if (opaqueStage != null)
					baseStage = opaqueStage;
				else if (!HasOpaqueLightmapBase(shader))
					return false;
			}

			// Detail = the most prominent remaining stage (prefer one that moves, then the strongest scroll, then a
			// blended stage over a flatly opaque one - an opaque detail would just replace the base's texels instead
			// of overlaying it).
			var chosenBase = baseStage;
			detailStage = usable
				.Where(s => s != chosenBase)
				.OrderByDescending(s => HasScroll(s) || HasRotate(s))
				.ThenByDescending(ScrollMagnitude)
				.ThenBy(s => IsOpaqueBlend(s) ? 1 : 0)
				.FirstOrDefault()!;
			if (detailStage == null)
				return false;

			// need at least one moving (scrolling or rotating) layer to be worthwhile
			return HasScroll(baseStage) || HasScroll(detailStage) || HasRotate(baseStage) || HasRotate(detailStage);
		}

		// Writes the live two-layer VMT. Lit surfaces use LightmappedGeneric (keeping the BSP lightmap), unlit ones
		// UnlitGeneric; both expose an independent $detailtexturetransform. $basetexture is animated by a
		// TextureTransform proxy ($scale = its tcmod scale, $translate driven by per-axis LinearRamps, and - when the
		// stage spins - $rotate driven by a LinearRamp at its tcMod rotate degrees/sec). The $detail layer rides its own
		// $detailtexturetransform on $translate2/$rotate2, scrolling at its true Q3 rate pre-divided by $detailscale
		// (which the engine multiplies back in); $detailscale carries the detail's signed Q3 tiling. Rotation needs no
		// such pre-divide - a uniform $detailscale only scales the rotation matrix, leaving its angle (and so its
		// degrees/sec) unchanged. The combine mode is mapped from the detail's Q3 blendFunc. Brighten/liquid surfaces
		// render translucent via $alpha.
		private void WriteDetailMaterialVmt(string textureName, Shader shader, ShaderStage baseStage, ShaderStage detailStage, string baseImg, string detailImg, OutputMode mode)
		{
			var baseScale = GetSignedScale(baseStage);
			var baseScroll = GetScrollVec(baseStage);
			var detailScroll = GetScrollVec(detailStage);
			var baseRotate = GetRotateSpeed(baseStage);
			var detailRotate = GetRotateSpeed(detailStage);

			// $detailscale is the detail's true (signed) Q3 tiling; the engine scales the detail translate by it, so
			// pre-divide the scroll rate to land on the true Q3 speed.
			var detailScaleParam = SignedScalar(GetSignedScale(detailStage));
			var detailTranslateRate = detailScroll / detailScaleParam;

			// Lit surfaces keep their lightmap via LightmappedGeneric; unlit ones use UnlitGeneric. Both honor the
			// independent $detailtexturetransform the detail proxy drives.
			var shaderType = shader.surfaceFlags.HasFlag(Q3SurfaceFlags.SURF_NOLIGHTMAP) ? "UnlitGeneric" : "LightmappedGeneric";

			var sb = new StringBuilder();
			sb.AppendLine(shaderType);
			sb.AppendLine("{");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t$basetexture \"{baseImg}\"");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t$detail \"{detailImg}\"");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t$detailscale {detailScaleParam}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t$detailblendmode {DetailCombineMode(detailStage)}");
			sb.AppendLine("\t$detailblendfactor 1");

			// Q3 liquids fake reflections with flat textures drawn through "tcGen environment". GetTextureStages
			// dropped those env stages, so pull the reflection stage from the full shader and route it to Strata's
			// Q3-accurate $spheremap path (the flat texture projected per-vertex), shared with the material and
			// flipbook paths. Works for both LightmappedGeneric and UnlitGeneric; $envmaplightscale is only emitted
			// when a lightmap stage is present (so unlit surfaces don't get the shadow-dimming).
			if (!noEnvMap && TryGetEnvStage(shader, out var envStage) && resolveImagePath(GetStageImagePath(envStage)!) != null)
			{
				copyExternalContent(GetStageImagePath(envStage)!); // ensure the reflection texture reaches a VTF
				MaterialConverter.AppendSpheremapParameters(sb, envStage, HasLightmapStage(shader), hasBaseAlphaMask: false);
			}

			if (mode == OutputMode.Additive)
			{
				sb.AppendLine("\t$additive 1");
			}
			else if (mode == OutputMode.Brighten)
			{
				// No baked per-texel alpha on a live surface, so translucency comes from a flat $alpha (the autoAlpha
				// default maps to a sensible half-transparent water look; an explicit options.alpha below 1 overrides).
				var alpha = options.alpha < 1f ? Math.Clamp(options.alpha, 0f, 1f) : 0.5f;
				sb.AppendLine(CultureInfo.InvariantCulture, $"\t$alpha {alpha}");
			}

			if (shader.cullType == CullType.TWO_SIDED)
				sb.AppendLine("\t$nocull 1");

			sb.AppendLine(CultureInfo.InvariantCulture, $"\t$scale \"[{baseScale.X} {baseScale.Y}]\"");
			sb.AppendLine("\t$translate \"[0.0 0.0]\"");
			sb.AppendLine("\t$translate2 \"[0.0 0.0]\"");
			if (baseRotate != 0f)
				sb.AppendLine("\t$rotate 0.0");
			if (detailRotate != 0f)
				sb.AppendLine("\t$rotate2 0.0");

			AppendLiquidProxies(sb, baseScroll, detailTranslateRate, baseRotate, detailRotate);

			sb.AppendLine("}");

			var vmtPath = Path.Combine(pk3Dir, textureName + ".vmt");
			Directory.CreateDirectory(Path.GetDirectoryName(vmtPath)!);
			File.WriteAllText(vmtPath, sb.ToString());
		}

		// Emits the proxy block animating both layers: per-axis LinearRamps feed $translate (folded with $scale into
		// $basetexturetransform) and $translate2 (into the detail's own $detailtexturetransform); when a layer spins, a
		// further LinearRamp feeds its $rotate/$rotate2 angle into the same transform.
		private static void AppendLiquidProxies(StringBuilder sb, Vector2 baseScroll, Vector2 detailScroll, float baseRotate, float detailRotate)
		{
			sb.AppendLine("\tProxies");
			sb.AppendLine("\t{");

			AppendScrollRamp(sb, "$translate", baseScroll);
			AppendScrollRamp(sb, "$translate2", detailScroll);

			if (baseRotate != 0f)
				AppendRotateRamp(sb, "$rotate", baseRotate);
			if (detailRotate != 0f)
				AppendRotateRamp(sb, "$rotate2", detailRotate);

			AppendTransformProxy(sb, "$scale", baseRotate != 0f ? "$rotate" : null, "$translate", "$basetexturetransform");
			AppendTransformProxy(sb, null, detailRotate != 0f ? "$rotate2" : null, "$translate2", "$detailtexturetransform");

			sb.AppendLine("\t}");
		}

		// Two LinearRamps driving the x/y components of a translate var at the given per-axis scroll rates.
		private static void AppendScrollRamp(StringBuilder sb, string translateVar, Vector2 scroll)
		{
			for (var axis = 0; axis < 2; axis++)
			{
				sb.AppendLine("\t\tLinearRamp");
				sb.AppendLine("\t\t{");
				sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\trate {(axis == 0 ? scroll.X : scroll.Y)}");
				sb.AppendLine("\t\t\tinitialValue 0.0");
				sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\tresultVar \"{translateVar}[{axis}]\"");
				sb.AppendLine("\t\t}");
			}
		}

		// A LinearRamp driving a rotation angle var at the given rate in degrees per second (Q3's tcMod rotate speed);
		// the engine's RotateZ matrix takes degrees, so the ramp feeds the angle straight in.
		private static void AppendRotateRamp(StringBuilder sb, string rotateVar, float degreesPerSecond)
		{
			sb.AppendLine("\t\tLinearRamp");
			sb.AppendLine("\t\t{");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\trate {degreesPerSecond}");
			sb.AppendLine("\t\t\tinitialValue 0.0");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\tresultVar \"{rotateVar}\"");
			sb.AppendLine("\t\t}");
		}

		// A TextureTransform proxy folding optional scale and rotate vars and a translate var into a result transform
		// matrix (the proxy applies them about the texture's center: scale, then rotate, then translate).
		private static void AppendTransformProxy(StringBuilder sb, string? scaleVar, string? rotateVar, string translateVar, string resultVar)
		{
			sb.AppendLine("\t\tTextureTransform");
			sb.AppendLine("\t\t{");
			if (scaleVar != null)
				sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\tscaleVar {scaleVar}");
			if (rotateVar != null)
				sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\trotateVar {rotateVar}");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\ttranslateVar {translateVar}");
			sb.AppendLine("\t\t\tinitialValue 0");
			sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t\tresultVar {resultVar}");
			sb.AppendLine("\t\t}");
		}

		// Maps a detail stage's Q3 blendFunc onto a Source detail combine mode (see common_ps_fxc.h TCOMBINE_*):
		// additive overlays add, "GL_dst_color" brightening liquids mod2x, plain multiplies multiply, and anything
		// else (alpha/blend overlays) composites over the base using the detail's alpha.
		private static int DetailCombineMode(ShaderStage stage)
		{
			if (IsAdditiveBlend(stage))
				return 1; // TCOMBINE_RGB_ADDITIVE: base + detail

			var srcBlend = stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = stage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			if (srcBlend == ShaderStageFlags.GLS_SRCBLEND_DST_COLOR)
				return 0; // TCOMBINE_RGB_EQUALS_BASE_x_DETAILx2: base * 2 * detail (Q3 layered-liquid brighten)
			if ((srcBlend == ShaderStageFlags.GLS_SRCBLEND_ZERO && dstBlend == ShaderStageFlags.GLS_DSTBLEND_SRC_COLOR) ||
				(srcBlend == ShaderStageFlags.GLS_SRCBLEND_DST_COLOR && dstBlend == ShaderStageFlags.GLS_DSTBLEND_ZERO))
				return 8; // TCOMBINE_MULTIPLY: base * detail

			return 2; // TCOMBINE_DETAIL_OVER_BASE: lerp toward detail by its alpha
		}

		// How surface-like a stage is, for choosing $basetexture: an opaque base (or a self-tinting "GL_one
		// GL_src_color" stage - see IsSelfTintBlend) outranks a multiplicative/alpha tint, which outranks a
		// brightening ripple, which outranks an additive glow (best left as the overlay).
		private static int BasePriority(ShaderStage stage)
		{
			if (IsOpaqueBlend(stage) || IsSelfTintBlend(stage)) return 4;
			if (IsMultiplyBlend(stage)) return 3;
			if (IsAlphaBlend(stage)) return 2;
			if (IsBrightenBlend(stage)) return 1;
			return 0;
		}

		// Combined tcmod scale of a stage keeping its sign (a negative axis mirrors the pattern); used so the surface
		// and detail tile and mirror as authored, unlike GetScaleFromTexMods which drops the sign for frequency math.
		private static Vector2 GetSignedScale(ShaderStage stage)
		{
			var scale = Vector2.One;
			foreach (var texMod in stage.bundles[0].texMods)
			{
				if (texMod.type == TexMod.TMOD_SCALE)
					scale *= new Vector2(texMod.scale[0], texMod.scale[1]);
			}
			return scale;
		}

		// Collapses a per-axis scale to the single scalar $detailscale expects: the geometric-mean magnitude, made
		// negative (a mirror/flip) when either Q3 axis was negative.
		private static float SignedScalar(Vector2 scale)
		{
			var mag = MathF.Sqrt(MathF.Max(MathF.Abs(scale.X), 1e-4f) * MathF.Max(MathF.Abs(scale.Y), 1e-4f));
			mag = Math.Clamp(mag, 0.01f, 100f);
			return (scale.X < 0f || scale.Y < 0f) ? -mag : mag;
		}

		private static bool HasScroll(ShaderStage stage) => ScrollMagnitude(stage) > 1e-4f;

		private static bool HasRotate(ShaderStage stage) => MathF.Abs(GetRotateSpeed(stage)) > 1e-4f;

		// Net rotation rate of a stage in degrees per second (summing any rotate tcmods). Q3's tcMod rotate spins the
		// texture about its center, which the TextureTransform proxy's rotateVar reproduces as a center-pivoted RotateZ.
		private static float GetRotateSpeed(ShaderStage stage)
		{
			var speed = 0f;
			foreach (var texMod in stage.bundles[0].texMods)
			{
				if (texMod.type == TexMod.TMOD_ROTATE)
					speed += texMod.rotateSpeed;
			}
			return speed;
		}

		private static float ScrollMagnitude(ShaderStage stage) => GetScrollVec(stage).Length();

		// Net per-axis scroll rate of a stage (summing any scroll tcmods), in texture cycles per second.
		private static Vector2 GetScrollVec(ShaderStage stage)
		{
			var scroll = Vector2.Zero;
			foreach (var texMod in stage.bundles[0].texMods)
			{
				if (texMod.type == TexMod.TMOD_SCROLL)
					scroll += new Vector2(texMod.scroll[0], texMod.scroll[1]);
			}
			return scroll;
		}

		// The stage's first frame image (no extension, forward slashes) as referenced by a VMT, or null when absent.
		private static string? GetStageImagePath(ShaderStage stage)
		{
			var image = stage.bundles[0].images[0];
			if (string.IsNullOrEmpty(image))
				return null;
			return Path.ChangeExtension(image, null).Replace('\\', '/');
		}

		// Finds the shader's "tcGen environment" reflection stage - the flat texture Q3 used to fake reflections -
		// preferring the additive ("GL_one GL_one") reflection stage over any other env stage. Returns false when
		// the shader carries no usable env stage.
		private bool TryGetEnvStage(Shader shader, out ShaderStage envStage)
		{
			envStage = null!;
			if (shader.stages == null)
				return false;

			var envStages = shader.stages
				.Where(s => s.bundles[0].tcGen == TexCoordGen.TCGEN_ENVIRONMENT_MAPPED && GetStageImagePath(s) != null)
				.ToList();
			envStage = envStages.FirstOrDefault(IsAdditiveBlend) ?? envStages.FirstOrDefault()!;
			return envStage != null;
		}
	}
}
