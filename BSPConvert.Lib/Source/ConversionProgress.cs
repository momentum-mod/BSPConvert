using System;
using System.Threading;

namespace BSPConvert.Lib
{
	// How far a conversion is, reported by BSPConverter to its IProgress<ConversionProgress>
	public readonly record struct ConversionProgress(
		// 0-1, over the whole input file (every map it has)
		float Fraction,
		// What is being done, e.g. "Converting textures"
		string Stage,
		// The map being converted, or null before the first one
		string? MapName,
		// Which of the input's maps that is (0-based) and how many it has, or 0 and 0 before the first one
		int MapIndex,
		int MapCount,
		// How many of the stage's items (textures, models...) are done, and how many there are, or 0 and 0 when the
		// stage doesn't count them
		int Done,
		int Total);

	// Turns the stages a conversion goes through into ConversionProgress reports, and stops the conversion at the next
	// stage or item when it's cancelled.
	//
	// A conversion is split into sections (loading the input, each map's conversion, writing each map), and a section
	// into stages that each cover a share of it. The engine converters estimate the shares from what the map has and
	// how long that took in measured conversions: encoding textures takes most of the time, in proportion to their
	// pixels, then baking Quake 3 flipbooks and compiling GoldSrc models. Those stages report their items as they go, so
	// the progress keeps moving through them.
	public class ProgressTracker
	{
		// Reports closer together than this are dropped, except a stage's first and last item
		private const float MinReportStep = 0.002f;

		private readonly IProgress<ConversionProgress>? progress;
		private readonly CancellationToken cancellation;

		private float sectionStart;
		private float sectionEnd = 1f;
		// Where the current stage starts in the section and how much of it the stage covers, as parts of the section
		private float stageStart;
		private float stageShare;
		private string stage = "";
		private string? mapName;
		private int mapIndex;
		private int mapCount;
		private float lastReported = -1f;

		public ProgressTracker(IProgress<ConversionProgress>? progress, CancellationToken cancellation = default)
		{
			this.progress = progress;
			this.cancellation = cancellation;
		}

		// Starts a section covering start to end of the whole conversion (0-1). Its stages divide it up from the start.
		public void BeginSection(float start, float end)
		{
			sectionStart = Math.Clamp(start, 0f, 1f);
			sectionEnd = Math.Clamp(end, sectionStart, 1f);
			stageStart = 0f;
			stageShare = 0f;
		}

		// Sets the map the stages are converting, for the reports
		public void SetMap(string? name, int index, int count)
		{
			mapName = name;
			mapIndex = index;
			mapCount = count;
		}

		// Ends the current stage and starts the next one, covering share (0-1) of the section after the stages before it.
		// The shares of a section's stages add up to 1. Throws OperationCanceledException if the conversion was
		// cancelled.
		public void Stage(string name, float share)
		{
			cancellation.ThrowIfCancellationRequested();

			stageStart = Math.Min(stageStart + stageShare, 1f);
			stageShare = Math.Clamp(share, 0f, 1f - stageStart);
			stage = name;
			Report(0, 0, 0f, force: true);
		}

		// Reports that done of the current stage's total items are done. Throws OperationCanceledException if the
		// conversion was cancelled.
		public void Item(int done, int total)
		{
			Item(done, total, total > 0 ? (float)done / total : 0f);
		}

		// Reports that done of the current stage's total items are done, which is stageFraction (0-1) of the stage's
		// work, for stages whose items take very different times. Throws OperationCanceledException if the conversion
		// was cancelled.
		public void Item(int done, int total, float stageFraction)
		{
			cancellation.ThrowIfCancellationRequested();

			if (total > 0)
				Report(Math.Clamp(done, 0, total), total, Math.Clamp(stageFraction, 0f, 1f), force: done <= 0 || done >= total);
		}

		// Reports that the whole conversion is done
		public void Finish()
		{
			BeginSection(1f, 1f);
			Stage("Done", 0f);
		}

		private void Report(int done, int total, float stageFraction, bool force)
		{
			if (progress == null)
				return;

			var sectionFraction = stageStart + stageShare * stageFraction;
			var fraction = sectionStart + (sectionEnd - sectionStart) * sectionFraction;
			if (!force && fraction - lastReported < MinReportStep)
				return;

			lastReported = fraction;
			progress.Report(new ConversionProgress(fraction, stage, mapName, mapIndex, mapCount, done, total));
		}
	}
}
