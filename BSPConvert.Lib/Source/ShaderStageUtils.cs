using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace BSPConvert.Lib
{
	// Shared analysis of a Q3 shader's blend stack, used by both the flipbook baker (FlipbookConverter) and the live
	// detail-material path (DetailMaterialConverter): which stages carry a visible texture, how a stage's blendFunc
	// classifies, and what overall output mode a stack maps to.
	internal static class ShaderStageUtils
	{
		// How a composited stack is written out, decided by the blend stack (see ClassifyOutputMode).
		public enum OutputMode
		{
			Opaque,    // has an opaque base -> self-contained opaque surface (ordered real-blendFunc replay)
			Additive,  // all additive, no base -> $additive glow (replay from black, black = transparent)
			Brighten   // all dst-color, no base (Q3 liquids) -> screen-composited translucent surface
		}

		// All stages with a visible, non-environment, non-lightmap texture (the layers that compose the surface).
		public static List<ShaderStage> GetTextureStages(Shader shader)
		{
			return shader.GetImageStages()
				.Where(s => s.bundles[0].tcGen != TexCoordGen.TCGEN_ENVIRONMENT_MAPPED &&
					s.bundles[0].tcGen != TexCoordGen.TCGEN_LIGHTMAP)
				.ToList();
		}

		// Matches a multi-pass animated surface the unified compositor can bake, and reports which output mode it
		// needs. Requires >= 2 visible stages (a single animated stage is better served live by MaterialConverter's
		// texture-transform proxies) and at least one reproducibly animated stage. Mixed translucent stacks with no
		// opaque base (neither all-additive nor all-brighten) are left for the normal path.
		public static bool IsAnimatedStackShader(Shader shader, out List<ShaderStage> stages, out OutputMode mode)
		{
			stages = GetTextureStages(shader);
			mode = OutputMode.Opaque;
			if (stages.Count < 2 || !AnimatesReproducibly(stages))
				return false;

			if (stages.Any(IsOpaqueBlend))
			{
				mode = OutputMode.Opaque;
				return true;
			}
			if (stages.All(IsAdditiveBlend))
			{
				mode = OutputMode.Additive;
				return true;
			}
			if (stages.All(IsBrightenBlend))
			{
				mode = BrightenOrOpaque(shader);
				return true;
			}
			return false;
		}

		// True when at least one stage animates in a way the compositor reproduces: scroll, rotate, stretch, an
		// rgb/alpha waveform, or an animMap sequence. (Transform/turbulent shear isn't replayed.)
		public static bool AnimatesReproducibly(List<ShaderStage> stages)
		{
			return stages.Any(s =>
				s.bundles[0].numImageAnimations > 1 ||
				(s.rgbGen == ColorGen.CGEN_WAVEFORM && s.rgbWave.frequency > 0f) ||
				(s.alphaGen == AlphaGen.AGEN_WAVEFORM && s.alphaWave.frequency > 0f) ||
				s.bundles[0].texMods.Any(t =>
					t.type == TexMod.TMOD_SCROLL ||
					t.type == TexMod.TMOD_ROTATE ||
					(t.type == TexMod.TMOD_STRETCH && t.wave.frequency > 0f)));
		}

		public static OutputMode ClassifyOutputMode(Shader shader, List<ShaderStage> stages)
		{
			if (stages.Any(IsOpaqueBlend))
				return OutputMode.Opaque;
			if (stages.All(IsAdditiveBlend))
				return OutputMode.Additive;
			return BrightenOrOpaque(shader);
		}

		// The output mode of a stack whose stages all composite against the framebuffer. Normally that means a
		// translucent Q3 liquid, but the stack is opaque when the shader's own lightmap stage is its base: Q3's
		// lit-surface idiom draws "map $lightmap" first with no blendFunc and multiplies the diffuse onto it, and
		// GetTextureStages drops lightmap stages because Source's LightmappedGeneric applies the lightmap itself.
		// Left uncounted, such a surface (say a diffuse filter under a scrolling brighten overlay) looks base-less
		// and is written out see-through when it is in fact fully opaque.
		public static OutputMode BrightenOrOpaque(Shader shader) =>
			HasOpaqueLightmapBase(shader) ? OutputMode.Opaque : OutputMode.Brighten;

		// True when the shader's lightmap stage is itself an opaque base (see BrightenOrOpaque).
		public static bool HasOpaqueLightmapBase(Shader shader)
		{
			return shader.stages != null && shader.stages.Any(s => IsLightmapStage(s) && IsOpaqueBlend(s));
		}

		private static bool IsLightmapStage(ShaderStage stage) =>
			stage.bundles[0].tcGen == TexCoordGen.TCGEN_LIGHTMAP || stage.bundles[0].images[0] == "$lightmap";

		// An opaque base stage writes solid color to the framebuffer ("GL_one GL_zero" or no blendFunc).
		public static bool IsOpaqueBlend(ShaderStage stage) => IsOpaqueBlendFlags(stage.flags);

		public static bool IsOpaqueBlendFlags(ShaderStageFlags flags)
		{
			var srcBlend = flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			var noBlend = srcBlend == 0 && dstBlend == 0;
			var isOneZero = srcBlend == ShaderStageFlags.GLS_SRCBLEND_ONE && dstBlend == ShaderStageFlags.GLS_DSTBLEND_ZERO;
			return noBlend || isOneZero;
		}

		public static bool IsAdditiveBlend(ShaderStage stage)
		{
			var srcBlend = stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = stage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			return dstBlend == ShaderStageFlags.GLS_DSTBLEND_ONE &&
				(srcBlend == ShaderStageFlags.GLS_SRCBLEND_ONE || srcBlend == ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA);
		}

		// A "GL_dst_color ..." brightening blend - the Q3 layered-liquid signature, where each pass multiplies and
		// brightens the live framebuffer. Can't be baked against a fixed base, so these use the screen composite.
		public static bool IsBrightenBlend(ShaderStage stage)
		{
			return (stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS) == ShaderStageFlags.GLS_SRCBLEND_DST_COLOR;
		}

		// A standard "GL_src_alpha GL_one_minus_src_alpha" alpha-blended overlay (a translucent detail/decal layer).
		public static bool IsAlphaBlend(ShaderStage stage)
		{
			var srcBlend = stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = stage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			return srcBlend == ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA &&
				dstBlend == ShaderStageFlags.GLS_DSTBLEND_ONE_MINUS_SRC_ALPHA;
		}

		// A multiplicative (darkening) blend that tints whatever's behind it: "GL_zero GL_src_color" or its
		// "GL_dst_color GL_zero" equivalent. Framebuffer-dependent like a brighten layer, but reads as a surface tint.
		public static bool IsMultiplyBlend(ShaderStage stage)
		{
			var srcBlend = stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = stage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			return (srcBlend == ShaderStageFlags.GLS_SRCBLEND_ZERO && dstBlend == ShaderStageFlags.GLS_DSTBLEND_SRC_COLOR) ||
				(srcBlend == ShaderStageFlags.GLS_SRCBLEND_DST_COLOR && dstBlend == ShaderStageFlags.GLS_DSTBLEND_ZERO);
		}

		// A "GL_one GL_src_color" self-tinting blend: the stage writes its own texture at full strength (srcFactor
		// ONE) while tinting whatever's already drawn by its own color (dstFactor SRC_COLOR). Q3 uses this idiom for
		// a surface's real diffuse texture layered over an earlier background pass (e.g. a pool floor drawn over
		// scrolling caustic ripples) - visually the stage's own texture dominates the result, so like a plain opaque
		// stage it's a valid $basetexture candidate, not just an overlay.
		public static bool IsSelfTintBlend(ShaderStage stage)
		{
			var srcBlend = stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = stage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			return srcBlend == ShaderStageFlags.GLS_SRCBLEND_ONE && dstBlend == ShaderStageFlags.GLS_DSTBLEND_SRC_COLOR;
		}

		// A reverse alpha blend ("GL_one_minus_src_alpha GL_src_alpha"). Drawn over an opaque reflection base, this
		// reveals the underlying reflection where the stage's alpha is high (refl*alpha) rather than low - the Q3
		// blue-metal chrome idiom whose diffuse alpha is authored inverted.
		public static bool IsReverseAlphaBlend(ShaderStage stage)
		{
			var srcBlend = stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = stage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			return srcBlend == ShaderStageFlags.GLS_SRCBLEND_ONE_MINUS_SRC_ALPHA &&
				dstBlend == ShaderStageFlags.GLS_DSTBLEND_SRC_ALPHA;
		}

		// True when the shader draws to a lightmap (an explicit "$lightmap" stage or a tcGen-lightmap stage). Used
		// to forward Q3's lightmap dimming of a spheremap reflection ($envmaplightscale).
		public static bool HasLightmapStage(Shader shader)
		{
			return shader.stages != null && shader.stages.Any(IsLightmapStage);
		}

		// A transparent overlay blend - additive ("GL_one GL_one") or alpha ("GL_src_alpha GL_one_minus_src_alpha").
		// A stage that's neither is an opaque base (e.g. a plain map, or a "GL_dst_color" lightmap multiply).
		public static bool IsOverlayBlend(ShaderStage stage)
		{
			var srcBlend = stage.flags & ShaderStageFlags.GLS_SRCBLEND_BITS;
			var dstBlend = stage.flags & ShaderStageFlags.GLS_DSTBLEND_BITS;
			var isAlphaBlend = srcBlend == ShaderStageFlags.GLS_SRCBLEND_SRC_ALPHA &&
				dstBlend == ShaderStageFlags.GLS_DSTBLEND_ONE_MINUS_SRC_ALPHA;
			return IsAdditiveBlend(stage) || isAlphaBlend;
		}

		// Combined tcmod scale magnitude across a stage's texMods (sign/flip dropped - it only mirrors the pattern).
		public static Vector2 GetScaleFromTexMods(List<TexModInfo> texMods)
		{
			var scale = Vector2.One;
			foreach (var texMod in texMods)
			{
				if (texMod.type == TexMod.TMOD_SCALE)
					scale *= new Vector2(texMod.scale[0], texMod.scale[1]);
			}
			return new Vector2(MathF.Abs(scale.X), MathF.Abs(scale.Y));
		}

		// gcd of two non-negative values via the Euclidean algorithm with a tolerance, so floats that are
		// near-multiples of a common base collapse to it (e.g. 0.04 and 0.03 -> 0.01) instead of a tiny residual.
		public static float RateGcd(float a, float b)
		{
			a = MathF.Abs(a);
			b = MathF.Abs(b);
			while (b > 1e-4f)
			{
				var rem = a - b * MathF.Floor(a / b);
				a = b;
				b = rem;
			}
			return a;
		}
	}
}
